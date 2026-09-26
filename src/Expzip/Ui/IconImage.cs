using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Expzip.Ui;

/// <summary>
/// ファイルやフォルダーの絵 (#158)。支援技術の木には出さない。
/// </summary>
/// <remarks>
/// <see cref="GlyphText"/> と同じ理由。**絵が伝えている事柄は行の名前のほうに入れてある**
/// (<c>EntryRow.RowName</c>) ので、絵そのものは木から外す。出すと、行の中で最初に当たるのが
/// 名前の無い絵になる。
/// </remarks>
internal sealed class IconImage : Image
{
    public IconImage()
    {
        // 表示倍率が 100% を超えるときは 32 px の絵を縮めて出す。縮め方を指定しないと粗くなる
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        // 画面テストが絵を探すための名前。木から外してあっても、省かれたものまで含む木には出る
        AutomationProperties.SetAutomationId(this, "EntryIcon");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new IconPeer(this);

    private sealed class IconPeer(Image owner) : ImageAutomationPeer(owner)
    {
        /// <summary>木に出さない。</summary>
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
