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

    /// <summary>書庫内での圧縮後サイズ(バイト)。</summary>
    public long CompressedLength { get; init; }

    /// <summary>最終更新日時。</summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>
    /// パスに <c>..</c> やドライブ指定が含まれ、通常の書庫ではあり得ない形のとき true。
    /// 一覧で警告を出すために使う (#36)。
    /// </summary>
    public bool IsPathSuspicious { get; init; }

    /// <summary>
    /// 圧縮率(%)。圧縮後サイズが元の何%になったかを表し、小さいほどよく縮んでいる。
    /// 展開後サイズが0のときは0を返す。
    /// </summary>
    public double CompressionRatio
        => Length == 0 ? 0 : (double)CompressedLength / Length * 100.0;
}
