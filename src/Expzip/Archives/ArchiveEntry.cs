namespace Expzip.Archives;

/// <summary>書庫内の1ファイルを表す。</summary>
internal sealed class ArchiveEntry
{
    /// <summary>書庫内のパス。区切りは <c>/</c> に正規化されている。</summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// 書庫内での元のエントリ名。正規化していないため、展開時に
    /// 書庫から該当エントリを引き当てるのに使う。
    /// </summary>
    public required string SourceName { get; init; }

    /// <summary>ファイル名(パスを含まない)。</summary>
    public required string Name { get; init; }

    /// <summary>展開後のサイズ(バイト)。</summary>
    public long Length { get; init; }

    /// <summary>
    /// 展開後のサイズが分かるかどうか (#68)。
    /// NSIS は展開後の大きさを持たないため、実際に展開するまで分からない。
    /// 分からないものを 0 として出すと「空のファイル」に見えるので区別する。
    /// </summary>
    public bool LengthKnown { get; init; } = true;

    /// <summary>書庫内での圧縮後サイズ(バイト)。</summary>
    public long CompressedLength { get; init; }

    /// <summary>
    /// 圧縮後サイズが分かるかどうか (#19)。
    /// 7z はまとめて圧縮する (ソリッド) ため、エントリごとの圧縮後サイズを持たない。
    /// 分からないものを 0 として出すと「100%縮んだ」ように見えるので区別する。
    /// </summary>
    public bool CompressedLengthKnown { get; init; } = true;

    /// <summary>最終更新日時。</summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>
    /// パスに <c>..</c> やドライブ指定が含まれ、通常の書庫ではあり得ない形のとき true。
    /// 一覧で警告を出すために使う (#36)。
    /// </summary>
    public bool IsPathSuspicious { get; init; }

    /// <summary>
    /// 中身がパスワードで保護されているとき true (#20)。
    /// 一覧で色を変えて示すために使う。ZIP も 7z もエントリごとに掛けられる。
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// 圧縮率(%)。圧縮後サイズが元の何%になったかを表し、小さいほどよく縮んでいる。
    /// 展開後サイズが0のときは0を返す。
    /// </summary>
    public double CompressionRatio
        => Length == 0 ? 0 : (double)CompressedLength / Length * 100.0;
}
