using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Expzip.Ui;

/// <summary>
/// 飾りの絵 (#126)。支援技術の木には出さない。
/// </summary>
/// <remarks>
/// バージョン情報のアイコンは、すぐ隣に「Expzip」という名前が並ぶ。絵にも名前を入れると
/// 同じことを 2 度読むことになり、入れなければ名前の無い「画像」として読まれる。
/// 絵が伝えることは隣の字に入っているので、<see cref="GlyphText"/> と同じく木から外す。
/// </remarks>
internal sealed class DecorativeImage : Image
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer(Image owner) : ImageAutomationPeer(owner)
    {
        /// <summary>木に出さない。出すと名前の無い画像として読まれる。</summary>
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
