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
        => ArchiveFactory.OpenArchive(path, Options(password));

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
        => ReaderFactory.OpenReader(stream, Options());
}
