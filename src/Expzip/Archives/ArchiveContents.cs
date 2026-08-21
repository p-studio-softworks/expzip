namespace Expzip.Archives;

/// <summary>読み込んだ書庫の内容。</summary>
internal sealed class ArchiveContents
{
    /// <summary>書庫ファイルのパス。</summary>
    public required string FilePath { get; init; }

    /// <summary>ルートフォルダ。</summary>
    public required ArchiveFolder Root { get; init; }

    /// <summary>書庫に含まれるファイル数(フォルダを除く)。</summary>
    public int FileCount { get; init; }

    /// <summary>展開後サイズの合計(バイト)。</summary>
    public long TotalLength { get; init; }

    /// <summary>圧縮後サイズの合計(バイト)。</summary>
    public long TotalCompressedLength { get; init; }

    /// <summary>
    /// パスが通常ではない項目の数(ファイルとフォルダの合計)。
    /// 0 より大きい場合、書庫を開いた時点で注意を促す (#36)。
    /// </summary>
    public int SuspiciousCount { get; init; }
}
