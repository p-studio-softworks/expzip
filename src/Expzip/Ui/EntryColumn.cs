namespace Expzip.Ui;

/// <summary>
/// 一覧の列 (#23)。
/// </summary>
/// <remarks>
/// 並び順は見出しの文字列ではなくこの値で覚える。見出しは言語によって変わるため、
/// 文字列で持つと言語を切り替えた瞬間に並び順を見失う。
/// </remarks>
internal enum EntryColumn
{
    /// <summary>名前。既定の並び順。</summary>
    Name,

    /// <summary>元の大きさ。</summary>
    Size,

    /// <summary>圧縮後の大きさ。</summary>
    Compressed,

    /// <summary>圧縮率。</summary>
    Ratio,

    /// <summary>更新日時。</summary>
    Date,
}
