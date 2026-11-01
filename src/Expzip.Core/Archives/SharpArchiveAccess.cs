using System.IO;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Expzip.Archives;

/// <summary>
/// SharpCompress を使う形式 (7z / tar) の入口 (#19)。
/// </summary>
/// <remarks>
/// <para>
/// ZIP は <see cref="System.IO.Compression"/> のまま扱う。ZIPだけは書き換えを
/// 「一部の差し替え」で行っており (追加・削除・名前の変更・移動)、EFSフラグごとの
/// 文字コード判定 (#13) や出所の印の扱いも作り込んであるため、置き換える利点がない。
/// </para>
/// <para>
/// エントリ名の文字コード判定は ZIP と同じ規則を使う。tar は名前をそのままの
/// バイト列で持つため、日本語圏の書庫では CP932 が入っていることがある。
/// </para>
/// </remarks>
internal static class SharpArchiveAccess
{
    /// <summary>読み取りの設定。名前の解釈を ZIP と揃える。</summary>
    public static ReaderOptions Options(string? password = null) => new()
    {
        Password = password,
        ArchiveEncoding = new ArchiveEncoding
        {
            // ZIP で使っているのと同じ判定器に通す。UTF-8として厳密に妥当なら
            // UTF-8、そうでなければ従来の日本語コードページとみなす (#13)
            CustomDecoder = (bytes, index, count, _) =>
                ZipArchiveReader.EntryNameEncoding.GetString(bytes, index, count),
        },
    };

    /// <summary>
    /// 7z 書庫を開く。tar と違って中身に random access が要るため、
    /// <see cref="IArchive"/> として開く。
    /// </summary>
    public static IArchive OpenSevenZip(string path, string? password = null)
        => ArchiveFactory.OpenArchive(OpenSevenZipStream(path), Options(password));

    /// <summary>7z のしるし。自己解凍書庫の中で本体が始まる位置を探すのに使う (#32)。</summary>
    private static ReadOnlySpan<byte> SevenZipSignature => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>
    /// 自己解凍部を読み飛ばして探す範囲。
    /// </summary>
    /// <remarks>
    /// 実物の自己解凍部は数百KBで収まる。書庫全体を舐めると、大きな書庫で
    /// 開くたびに何百MBも読むことになるため、頭の一定量だけを見る。
    /// </remarks>
    private const int SevenZipSearchLimit = 4 * 1024 * 1024;

    /// <summary>
    /// 7z 書庫の中身だけを見せる流れを開く。自己解凍書庫なら前の塊を隠す (#32)。
    /// </summary>
    public static Stream OpenSevenZipStream(string path)
    {
        var offset = SevenZipOffset(path);
        var stream = ArchiveFile.OpenRead(path);

        // 7z のしるしが無いものは SharpCompress が中身を見て形式を判断し、xz なら伸長する (#294)
        return new EndGuardStream(new SevenZipVersionStream(offset > 0 ? new OffsetStream(stream, offset) : stream));
    }

    /// <summary>7z の本体が始まる位置を返す。ふつうの 7z では 0。</summary>
    public static long SevenZipOffset(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);

            var window = (int)Math.Min(stream.Length, SevenZipSearchLimit);

            if (window < SevenZipSignature.Length)
            {
                return 0;
            }

            var buffer = new byte[window];
            stream.ReadExactly(buffer);

            // 先頭にあるなら、ふつうの 7z
            var found = buffer.AsSpan().IndexOf(SevenZipSignature);
            return found > 0 ? found : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or EndOfStreamException)
        {
            return 0;
        }
    }

    /// <summary>
    /// tar 書庫を先頭から順に読む <see cref="IReader"/> を開く。
    /// </summary>
    /// <remarks>
    /// gzip / bzip2 / xz で圧縮された tar は、書庫の先頭に圧縮の層があるため
    /// <c>ArchiveFactory</c> では形式を判別できない。<see cref="ReaderFactory"/> は
    /// 圧縮の層を剥がしてから中の tar を読むので、こちらを使う。
    /// 先頭から順にしか読めないが、一覧も取り出しも一度なめれば済む。
    /// </remarks>
    public static IReader OpenTarReader(Stream stream)
        => ReaderFactory.OpenReader(IsXz(stream) ? OpenXz(stream) : stream, Options());

    /// <summary>
    /// xz を読む流れを開く。入力の終わりを超えて読もうとしたら例外にする (#294)。
    /// </summary>
    /// <remarks>
    /// SharpCompress の LZMA2 は入力の終わりで残りの長さを負にして、入力を読まずに回り続ける。その前に読むのは
    /// 2 回ほどなので、回数を許せない。<see cref="ReaderFactory"/> の中の xz には外から見張りを挟めないので、
    /// 先に xz を剥がして渡す。
    /// </remarks>
    public static Stream OpenXz(Stream stream)
        => new SharpCompress.Compressors.Xz.XZStream(new EndGuardStream(stream, allowedEndReads: 0));

    /// <summary>xz のしるしで始まるか。読む位置は変えない。</summary>
    public static bool IsXz(Stream stream)
    {
        Span<byte> head = stackalloc byte[6];
        var start = stream.Position;
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = start;

        return read == head.Length && head.SequenceEqual<byte>([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]);
    }

    /// <summary>
    /// 7z の中身を読む前に、見出しと圧縮データを確かめる (#294)。
    /// </summary>
    /// <exception cref="DamagedDataException">データが切れている場合。</exception>
    /// <exception cref="ArchiveTooLargeException">書庫が大きすぎる場合。</exception>
    public static void CheckSevenZipContent(string path, CancellationToken cancellationToken)
    {
        using var stream = OpenSevenZipStream(path);

        SevenZipHeader.CheckContent(stream, ArchiveLimits.MemoryBudget, cancellationToken);
    }
}
