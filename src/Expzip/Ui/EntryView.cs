namespace Expzip.Ui;

/// <summary>
/// 一覧の表示の形 (#216)。エクスプローラーの「表示」と同じ 6 つ。
/// </summary>
/// <remarks>
/// 並びはエクスプローラーのメニューと同じ、大きいほうから。
/// 値は Ctrl+Shift+1〜6 の数字から 1 を引いたものと揃えてある。
/// </remarks>
internal enum EntryView
{
    /// <summary>特大アイコン (256)。</summary>
    ExtraLargeIcons,

    /// <summary>大アイコン (96)。</summary>
    LargeIcons,

    /// <summary>中アイコン (48)。</summary>
    MediumIcons,

    /// <summary>小アイコン (16)。左から右へ並べ、下へ折り返す。</summary>
    SmallIcons,

    /// <summary>一覧 (16)。上から下へ並べ、右へ折り返す。</summary>
    List,

    /// <summary>詳細。列のある表。デフォルト。</summary>
    Details,
}
