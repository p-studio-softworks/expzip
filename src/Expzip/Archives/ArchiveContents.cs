namespace Expzip.Archives;

/// <summary>読み込んだ書庫の内容。</summary>
internal sealed class ArchiveContents
{
    /// <summary>書庫ファイルのパス。</summary>
    public required string FilePath { get; init; }

    /// <summary>書庫の形式 (#19)。書き換えられるかどうかの判断にも使う。</summary>
    public ArchiveFormat Format { get; init; } = ArchiveFormat.Zip;

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

    /// <summary>
    /// 暗号化されたエントリを含むかどうか (#19)。
    /// 一覧は読めても中身は取り出せないため、開いた時点で知らせる。
    /// </summary>
    public bool HasEncryptedEntries { get; init; }

    /// <summary>
    /// 中身を取り出すのにパスワードが要るか (#20)。
    /// 一覧は読めるので、開くこと自体はできる。
    /// </summary>
    public bool RequiresPassword { get; init; }

    /// <summary>AES で暗号化されているか。false のときは旧方式 (ZipCrypto)。</summary>
    public bool UsesAes { get; init; }

    /// <summary>
    /// 中身を書き換えられるかどうか。
    /// パスワード付きの書庫は、書き換えると暗号化が外れてしまうため触らない (#20)。
    /// </summary>
    public bool IsEditable => ArchiveFormats.IsEditable(Format) && !RequiresPassword;
}
