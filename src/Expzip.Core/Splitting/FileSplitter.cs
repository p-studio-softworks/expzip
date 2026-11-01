using System.IO;
using System.Reflection;
using System.Text;
using ICSharpCode.SharpZipLib.Checksum;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Splitting;

/// <summary>分割の経過 (#59)。</summary>
/// <param name="Done">ここまでに書き出したバイト数。</param>
/// <param name="Total">元のファイルの大きさ。</param>
/// <param name="CurrentName">いま書いている断片の名前。</param>
internal readonly record struct SplitProgress(long Done, long Total, string CurrentName);

/// <summary>分割の結果。</summary>
/// <param name="Parts">作った断片の数。</param>
/// <param name="JoinerName">作った結合用のプログラムの名前。</param>
/// <param name="Cancelled">途中で中断されたか。</param>
internal sealed record SplitResult(int Parts, string JoinerName, bool Cancelled);

/// <summary>
/// 大きなファイルを決まった大きさに分ける (#59)。
/// </summary>
/// <remarks>
/// <para>
/// 断片は <c>&lt;元の名前&gt;.001</c> <c>.002</c> … とする。7-Zip と同じ形で、
/// 受け取った人が別のソフトでも結合できる。
/// </para>
/// <para>
/// あわせて、断片を結合する小さな実行ファイルを <c>&lt;元の名前&gt;.exe</c> として作る。
/// 受け取った人が Expzip を持っていなくても元に戻せるようにするため。中身は
/// <c>tools/joiner</c> にある C のプログラムで、末尾に元の名前と照合用の値を書き足して配る。
/// </para>
/// <para>
/// <b>元のファイルは消さない。</b> 分割は移動ではなく写しを作る操作として扱う。
/// </para>
/// </remarks>
internal static class FileSplitter
{
    /// <summary>分割サイズの下限。これより小さいと断片が増えすぎる。</summary>
    public const long MinimumChunk = 128 * 1024;

    /// <summary>まとめて読み書きする大きさ。</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>結合用のプログラムの末尾に書き足す印。</summary>
    private static ReadOnlySpan<byte> FooterMagic => "EXPZSPLT"u8;

    /// <summary>書き足す固定部の大きさ。結合用のプログラム側と揃えること。</summary>
    private const int FooterSize = 32;

    /// <summary>同梱してある結合用のプログラムの置き場。</summary>
    private const string JoinerResource = "Expzip.Resources.joiner-x86.exe";

    /// <summary>ファイルを分ける。</summary>
    /// <param name="sourcePath">分ける対象。</param>
    /// <param name="destinationDirectory">断片と結合用のプログラムの置き場。</param>
    /// <param name="chunkSize">1つあたりの大きさ。</param>
    /// <param name="progress">経過の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="IOException">読み書きに失敗した場合。</exception>
    public static SplitResult Split(
        string sourcePath,
        string destinationDirectory,
        long chunkSize,
        IProgress<SplitProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(destinationDirectory, name);

        // 全部を一時的な名前に書いてから、まとめて置き換える。失敗や中止で、前からあった同じ名前のファイルは消えない (#320)
        var written = new List<AtomicFile>();

        // 出所の印は分割ファイルに引き継ぐ。結合用のプログラムには付けない。手元で作っただけのものを実行するたびに
        // Windows の警告が出てしまう。結合用のプログラムは、結合するときに分割ファイルの印を読む (#321)
        var zone = MarkOfTheWeb.TryRead(sourcePath);

        Directory.CreateDirectory(destinationDirectory);

        try
        {
            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var total = source.Length;
            var buffer = new byte[BufferSize];
            var crc = new Crc32();
            var parts = 0;
            long done = 0;

            while (done < total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var partPath = PartPath(target, ++parts);
                var file = AtomicFile.Create(partPath, overwrite: true, zone);
                written.Add(file);
                progress?.Report(new SplitProgress(done, total, Path.GetFileName(partPath)));

                {
                    var part = file.Stream;
                    var remaining = Math.Min(chunkSize, total - done);

                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var want = (int)Math.Min(buffer.Length, remaining);
                        var read = source.Read(buffer, 0, want);

                        if (read <= 0)
                        {
                            // 途中で読めなくなった。書きかけを残さない
                            throw new IOException(Localization.Strings.SplitSourceShrank);
                        }

                        part.Write(buffer, 0, read);
                        crc.Update(new ArraySegment<byte>(buffer, 0, read));
                        remaining -= read;
                        done += read;

                        progress?.Report(new SplitProgress(
                            done, total, Path.GetFileName(partPath)));
                    }
                }
            }

            var joinerPath = target + ".exe";
            var joiner = AtomicFile.Create(joinerPath);
            written.Add(joiner);
            WriteJoiner(joiner.Stream, name, parts, total, crc.Value);

            AtomicFile.CommitAll(written);

            return new SplitResult(parts, Path.GetFileName(joinerPath), false);
        }
        catch (OperationCanceledException)
        {
            // 中途半端な断片は結合に使えず、残しておくと「揃っている」と勘違いさせる。
            // 書きかけは下で消える
            return new SplitResult(0, string.Empty, true);
        }
        finally
        {
            foreach (var file in written)
            {
                file.Dispose();
            }
        }
    }

    /// <summary>分割すると置き換わる、既にあるファイル。</summary>
    public static IReadOnlyList<string> ExistingOutputs(string sourcePath, string destinationDirectory, long chunkSize)
    {
        var target = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
        var parts = CountParts(new FileInfo(sourcePath).Length, chunkSize);

        return Enumerable.Range(1, parts)
            .Select(number => PartPath(target, number))
            .Append(target + ".exe")
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>
    /// 断片の名前。7-Zip と同じく最低3桁にする。
    /// </summary>
    /// <remarks>
    /// 1000 個を超えると4桁になる。結合用のプログラム側も同じ数え方をしている。
    /// </remarks>
    public static string PartPath(string target, int number)
        => $"{target}.{number:D3}";

    /// <summary>この大きさで分けたら断片がいくつになるか。</summary>
    public static int CountParts(long length, long chunkSize)
        => (int)Math.Min(int.MaxValue, (length + chunkSize - 1) / Math.Max(1, chunkSize));

    /// <summary>
    /// 結合用のプログラムを書き出す。
    /// </summary>
    /// <remarks>
    /// 同梱の実行ファイルの後ろに、元の名前と照合用の値を書き足す。形は
    /// <c>tools/joiner/README.md</c> に書いてある。名前を固定部より前に置くのは、
    /// 長さが変わるため。読む側は末尾から固定部を読み、そこに書かれた長さのぶんだけ
    /// 手前へ戻って名前を読む。
    /// </remarks>
    private static void WriteJoiner(Stream output, string name, int parts, long size, long crc)
    {
        using var stub = typeof(FileSplitter).Assembly
            .GetManifestResourceStream(JoinerResource)
            ?? throw new InvalidOperationException(Strings.EmbeddedToolMissing(JoinerResource));

        stub.CopyTo(output);

        var encoded = Encoding.Unicode.GetBytes(name);
        output.Write(encoded);

        var footer = new byte[FooterSize];
        FooterMagic.CopyTo(footer);
        BitConverter.TryWriteBytes(footer.AsSpan(8), 1u);
        BitConverter.TryWriteBytes(footer.AsSpan(12), (uint)parts);
        BitConverter.TryWriteBytes(footer.AsSpan(16), (ulong)size);
        BitConverter.TryWriteBytes(footer.AsSpan(24), (uint)crc);
        BitConverter.TryWriteBytes(footer.AsSpan(28), (uint)name.Length);
        output.Write(footer);
    }
}
