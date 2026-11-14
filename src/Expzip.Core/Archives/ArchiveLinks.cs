using System.IO;
using System.Reflection;
using System.Text;
using SharpCompress.Common;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>
/// 展開で、リンクの扱いを変える指定 (#367)。画面の「展開」では使わない。
/// </summary>
internal sealed record LinkOptions
{
    /// <summary>
    /// 取り出す項目 (書庫の中の名前)。リンクは、このどれかの中にあり、指す先も同じ項目の中にあるものだけを扱う。
    /// ドラッグと、選んだ項目から新しい書庫を作るときに使う。落とした先や新しい書庫では、項目どうしの位置が書庫の中と変わるため。
    /// </summary>
    public IReadOnlyList<string>? Roots { get; init; }

    /// <summary>
    /// ジャンクションを取り出さない。ドラッグで使う。ジャンクションは指す先を絶対パスで持つので、
    /// エクスプローラーがコピーや移動をすると、正しい場所を指さなくなる (#367 で決めた)。
    /// </summary>
    public bool JunctionsNotAllowed { get; init; }

    /// <summary>
    /// リンクをディスクに作らず、<see cref="ExtractResult.Links"/> に返す。選んだ項目から新しい書庫を作るときに使う。
    /// 開発者モードがオフの PC でも作れるようにするため。ハードリンクとファイルのコピーは ZIP に入れられないので、失敗にする。
    /// </summary>
    public bool Collect { get; init; }
}

/// <summary>確かめたうえで、ディスクに作らずに返したリンク (#367)。</summary>
/// <param name="Name">書庫の中の名前 (整えたもの。区切りは <c>/</c>)。</param>
/// <param name="Target">リンクのあるフォルダーからの相対パス (区切りは <c>/</c>)。指す先は同じ取り出す項目の中。</param>
/// <param name="ToFolder">フォルダーを指すか。</param>
internal sealed record CollectedLink(string Name, string Target, bool ToFolder);

/// <summary>
/// tar・ZIP・7z のリンクの見分け方と、指す先の読み方 (#367)。一覧、展開、パスワード付きの展開で同じものを使う。
/// </summary>
internal static class ArchiveLinks
{
    /// <summary>指す先として読む長さの上限。これより長いものは展開しない。</summary>
    public const int MaxTarget = 4096;

    /// <summary>7z の、Unix の属性があることを示す印 (FILE_ATTRIBUTE_UNIX_EXTENSION)。上位 16 ビットが Unix の mode。</summary>
    private const uint SevenZipUnixExtension = 0x8000;

    /// <summary>Windows のリパースポイントの属性。7-Zip の <c>-snl</c> で入れたリンク。</summary>
    private const uint ReparsePointAttribute = 0x400;

    /// <summary>読めないバイトがあれば例外を出す UTF-8。黙って別の文字に置き換えると、指す先が変わる。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 7z の項目の属性から、リンクの種類を決める。<see cref="RarLinkKind.WindowsSymbolicLink"/> は 7-Zip の <c>-snl</c> の形で、
    /// 展開しない (中身は Windows の内部のデータ。#377)。両方の印があるものも、こちらとして扱う。
    /// </summary>
    public static SevenZipLink SevenZipKind(IEntry entry) => SevenZipKind(SevenZipAttributes(entry));

    /// <summary>
    /// 7z の項目の属性 (32 ビットすべて)。SharpCompress の <see cref="IEntry.Attrib"/> は、Unix の属性がある項目では 0 を返すので、
    /// <c>ExtendedAttrib</c> を読む。
    /// </summary>
    private static long? SevenZipAttributes(IEntry entry)
        => entry is SharpCompress.Common.SevenZip.SevenZipEntry seven ? seven.ExtendedAttrib : entry.Attrib;

    /// <summary>属性の値から、7z の項目のリンクの種類を決める。</summary>
    public static SevenZipLink SevenZipKind(long? attributes)
    {
        if (attributes is not { } value)
        {
            return SevenZipLink.None;
        }

        var bits = (uint)value;
        if ((bits & ReparsePointAttribute) != 0)
        {
            return SevenZipLink.WindowsReparse;
        }

        return (bits & SevenZipUnixExtension) != 0 && ZipLinks.IsLink(unchecked((int)bits))
            ? SevenZipLink.Symbolic
            : SevenZipLink.None;
    }

    /// <summary>tar の項目の種類。SharpCompress は公開していないので、中の値を読む。</summary>
    /// <remarks>
    /// <see cref="IEntry.LinkTarget"/> があるかどうかでは決めない。PAX のグローバルヘッダーに <c>linkpath</c> があると、
    /// ふつうのファイルにも指す先が入ることがある。読めなくなったら (SharpCompress の版を上げたときなど)、
    /// 指す先のある項目を <see cref="RarLinkKind.LegacyUnixSymbolicLink"/> (種類が分からない) として返し、展開しない。
    /// </remarks>
    public static RarLinkKind TarKind(IEntry entry)
    {
        var type = TarEntryType(entry);
        return type switch
        {
            (byte)'2' => RarLinkKind.UnixSymbolicLink,
            (byte)'1' => RarLinkKind.HardLink,
            null when !string.IsNullOrEmpty(entry.LinkTarget) => RarLinkKind.LegacyUnixSymbolicLink,
            _ => RarLinkKind.None,
        };
    }

    private static readonly FieldInfo? TarFilePartField
        = typeof(SharpCompress.Common.Tar.TarEntry).GetField("_filePart", BindingFlags.Instance | BindingFlags.NonPublic);

    private static byte? TarEntryType(IEntry entry)
    {
        try
        {
            if (entry is not SharpCompress.Common.Tar.TarEntry || TarFilePartField?.GetValue(entry) is not { } part)
            {
                return null;
            }

            var header = part.GetType().GetProperty("Header", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(part);
            var type = header?.GetType().GetProperty("EntryType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(header);
            return type is null ? null : Convert.ToByte(type, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is TargetException or MemberAccessException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// 指す先を中身から読む (ZIP と 7z)。読めないもの、長すぎるもの、壊れているものは <see langword="null"/> で、理由を返す。
    /// </summary>
    /// <param name="open">中身を開くもの。</param>
    /// <param name="declaredSize">書庫に書かれた大きさ。分からなければ -1。</param>
    /// <param name="expectedCrc">書庫に書かれた CRC。分からなければ -1。</param>
    /// <remarks>
    /// 書かれた大きさが上限を超えるものは開かない。開いたあとも上限 + 1 バイトまでしか読まない。
    /// 大きさを小さく偽って大きく膨らむ項目を、中止できないまま最後まで伸長しないため。
    /// </remarks>
    public static string? ReadTarget(
        Func<Stream> open, long declaredSize, long expectedCrc, CancellationToken cancellationToken, out string reason)
    {
        reason = Strings.LinkTargetUnreadable;
        if (declaredSize > MaxTarget)
        {
            return null;
        }

        byte[] buffer;
        int read;
        try
        {
            using var stream = open();
            buffer = new byte[MaxTarget + 1];
            read = 0;
            while (read < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }
        catch (OperationCanceledException)
        {
            // 呼び出し側が中止を確かめる
            return null;
        }
        catch (Exception ex) when (CancellableCopy.IsLibraryFailure(ex) || ex is IOException or InvalidDataException)
        {
            reason = Strings.Reason(new DamagedDataException(ex));
            return null;
        }

        if (read > MaxTarget)
        {
            return null;
        }

        if (expectedCrc >= 0)
        {
            var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
            crc.Update(new ArraySegment<byte>(buffer, 0, read));
            if (crc.Value != expectedCrc)
            {
                reason = Strings.Reason(new DamagedDataException(new InvalidDataException("CRC")));
                return null;
            }
        }

        if (read == 0)
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(buffer, 0, read);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// 書庫の中の名前を、書き出す先を決めるとき (<see cref="ArchiveExtractor.TargetFor"/>) と同じ手順で整える。
    /// 区切りは <c>/</c>。展開先の外に出るもの、空になるものは <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 先頭の <c>./</c>、途中の <c>.</c> と空の区切り、末尾の点と空白を、Windows と同じに落とす。別々に整えると、
    /// <c>./</c> 付きの tar で正しいリンクを展開できなかったり、<c>a//lnk</c> から <c>..</c> を 1 つ多く使えたりする。
    /// </remarks>
    public static string? Canonical(string name)
    {
        var target = ArchiveExtractor.TargetFor(Anchor, name, basePath: null);
        if (target is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(Anchor, target);
        return relative is "." or "" ? null : relative.Replace('\\', '/');
    }

    /// <summary>名前を整えるときの、仮の展開先。ディスクには触れない。</summary>
    private static readonly string Anchor = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "expzip-link-anchor"));
}

/// <summary>7z の項目のリンクの種類 (#367)。</summary>
internal enum SevenZipLink
{
    /// <summary>リンクではない。</summary>
    None,

    /// <summary>Unix のシンボリックリンク (Linux の 7-Zip などで作ったもの)。中身が指す先。</summary>
    Symbolic,

    /// <summary>Windows のリパースポイント (7-Zip の <c>-snl</c>)。展開しない。</summary>
    WindowsReparse,
}
