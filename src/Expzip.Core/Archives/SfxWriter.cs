using System.IO;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>自己解凍書庫を作る (#29)。</summary>
/// <remarks>
/// <para>
/// 取り出すプログラム (スタブ) の後ろに ZIP をそのまま繋ぐだけ。中央ディレクトリの
/// 位置は書き換えない。前に付いた分のずれは終端レコードから逆算できるため、
/// 読む側で辻褄が合う。読み取り (#32) も 7-Zip も、この形をそのまま開ける。
/// </para>
/// <para>
/// スタブは <c>tools/sfx</c> にある C のプログラムで、出来上がりを同梱してある。
/// 詳しくは仕様書 4.7節。
/// </para>
/// </remarks>
internal static class SfxWriter
{
    /// <summary>同梱してあるスタブの置き場。</summary>
    private const string StubResource = "Expzip.Resources.sfx-x86.exe";

    /// <summary>作った自己解凍書庫の拡張子。</summary>
    public const string Extension = ".exe";

    /// <summary>
    /// スタブが扱える最大の書庫。これを超えると ZIP64 になり、スタブが読めない。
    /// </summary>
    /// <remarks>
    /// 4GB の壁そのものではなく、中央ディレクトリの位置が 32bit に収まる範囲。
    /// 余裕を見て少し手前で断る。
    /// </remarks>
    public const long SizeLimit = 4L * 1024 * 1024 * 1024 - 64 * 1024;

    /// <summary>スタブが扱える最大の件数。これを超えると ZIP64 になる。</summary>
    public const int CountLimit = 0xFFFF - 1;

    /// <summary>自己解凍書庫にできない理由。作れる場合は <see langword="null"/>。</summary>
    /// <remarks>
    /// 作ってから動かないと分かるのがいちばん困る。作る前に断る。
    /// </remarks>
    public static SfxRejection? Reject(ArchiveContents contents)
    {
        if (contents.Format != ArchiveFormat.Zip)
        {
            return SfxRejection.NotZip;
        }

        if (contents.IsSelfExtracting)
        {
            return SfxRejection.AlreadySelfExtracting;
        }

        if (contents.RequiresPassword || contents.HasEncryptedEntries)
        {
            return SfxRejection.Encrypted;
        }

        if (contents.FileCount > CountLimit)
        {
            return SfxRejection.TooMany;
        }

        try
        {
            if (new FileInfo(contents.FilePath).Length > SizeLimit)
            {
                return SfxRejection.TooBig;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 大きさを見られないだけなら、作ってみる方が親切
        }

        return null;
    }

    /// <summary>自己解凍書庫を書き出す。</summary>
    /// <param name="archivePath">元になる ZIP。</param>
    /// <param name="destinationPath">書き出す先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="IOException">読み書きに失敗した場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static void Create(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        using var stub = typeof(SfxWriter).Assembly.GetManifestResourceStream(StubResource)
            ?? throw new InvalidOperationException(Strings.EmbeddedToolMissing(StubResource));

        using var source = File.OpenRead(archivePath);

        // 途中で止められたら書きかけを残さない。中途半端な .exe は、
        // 動かして初めて壊れていると分かることになる
        var written = false;

        try
        {
            using (var output = new FileStream(
                       destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stub.CopyTo(output);
                CancellableCopy.Copy(source, output, cancellationToken);
            }

            written = true;
        }
        finally
        {
            if (!written)
            {
                TryDelete(destinationPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>自己解凍書庫にできない理由 (#29)。</summary>
internal enum SfxRejection
{
    /// <summary>ZIP ではない。作れるのは ZIP だけ。</summary>
    NotZip,

    /// <summary>もう自己解凍書庫になっている。</summary>
    AlreadySelfExtracting,

    /// <summary>パスワードが掛かっている。スタブは復号を持たない。</summary>
    Encrypted,

    /// <summary>件数が多すぎる (ZIP64 になる)。</summary>
    TooMany,

    /// <summary>大きすぎる (ZIP64 になる)。</summary>
    TooBig,
}
