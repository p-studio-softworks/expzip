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
    /// 先頭に取り出すプログラムが付いた自己解凍書庫か (#32)。
    /// </summary>
    public bool IsSelfExtracting { get; init; }

    /// <summary>
    /// 分割された書庫の断片を結合して開いているか (#61)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 書き換えると断片に切り直すことになり、元の分割の大きさも分からない。
    /// 読み取りのみにする。
    /// </para>
    /// <para>
    /// パスだけで決まるので、形式ごとの読む側に持ち回らせない。ただし判定は
    /// ファイルを見に行くため、一度だけ数えて覚えておく。
    /// </para>
    /// </remarks>
    public bool IsSplit => _split ??= SplitVolumes.IsFirstVolume(FilePath);

    private bool? _split;

    /// <summary>
    /// 中身を書き換えられるかどうか。
    /// パスワード付きの書庫も書き換えられるが、作り直しになる (#20)。
    /// </summary>
    /// <remarks>
    /// 自己解凍書庫は読み取りのみ (#32)。書き換えは書庫の部分だけを作り直すことに
    /// なるが、前に付いたプログラムは書庫の位置を自分の中に覚えていることがあり、
    /// 書き換えると自己解凍できなくなる。壊す危険を冒す利点がない。
    /// </remarks>
    public bool IsEditable
        => ArchiveFormats.IsEditable(Format) && !IsSelfExtracting && !IsSplit;
}
