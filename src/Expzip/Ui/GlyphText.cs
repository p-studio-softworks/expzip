using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Expzip.Ui;

/// <summary>
/// 絵だけを出す字 (#119)。支援技術の木には出さない。
/// </summary>
/// <remarks>
/// <para>
/// 絵は Segoe MDL2 Assets の私用領域の符号で書いてある。そのまま置くと、
/// 支援技術には**符号そのものが行の字として読まれる**。意味にならないうえ、
/// 名前の前に並ぶので、行の中で最初に当たるのがこれになる。
/// </para>
/// <para>
/// 代わりに名前を入れる手もあるが、こんどは行のすべてに「ファイル」「フォルダー」が
/// 付いて回る。**絵が伝えている事柄は行の名前のほうに入れてある**ので
/// (<c>EntryRow.RowName</c>)、絵そのものは木から外す。マウスを当てたときの説明は
/// これまでどおり出る。
/// </para>
/// </remarks>
internal sealed class GlyphText : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GlyphPeer(this);

    private sealed class GlyphPeer(TextBlock owner) : TextBlockAutomationPeer(owner)
    {
        /// <summary>木に出さない。出すと符号がそのまま読まれる。</summary>
        protected override bool IsControlElementCore() => false;

        protected override string GetNameCore() => string.Empty;
    }
}
