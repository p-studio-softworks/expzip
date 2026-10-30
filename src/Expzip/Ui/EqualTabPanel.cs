using System.Windows;
using System.Windows.Controls;

namespace Expzip.Ui;

/// <summary>
/// タブを同じ幅で並べる台 (#315)。エクスプローラーのタブと同じ考え方にしてある。
/// </summary>
/// <remarks>
/// <para>
/// **幅は、使える幅をタブの数で割って、<see cref="MinTabWidth"/> と <see cref="MaxTabWidth"/> の間に収めたもの。**
/// 値は WinUI の TabView の既定と同じ。名前の長さでは変わらない。
/// </para>
/// <para>
/// 最小の幅でも入らないタブは、はみ出した分を切って隠す。送るのは呼び出し側のボタンから
/// <see cref="Scroll"/> で行う。**<c>ScrollViewer</c> には入れない。**中では幅の限りが無くなり、
/// 割る元の「使える幅」が分からなくなる。
/// </para>
/// </remarks>
internal sealed class EqualTabPanel : Panel
{
    public const double MinTabWidth = 100;
    public const double MaxTabWidth = 240;

    private double _tabWidth;
    private double _offset;
    private bool _showSelected;

    public EqualTabPanel()
    {
        ClipToBounds = true;
    }

    /// <summary>送れる範囲や位置が変わったかもしれないときに出す。</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>左へ送れる。</summary>
    public bool CanScrollBack => _offset > 0;

    /// <summary>右へ送れる。</summary>
    public bool CanScrollForward => _offset < MaxOffset(RenderSize.Width);

    /// <summary>タブ <paramref name="tabs"/> 個分だけ送る。負の値で左へ。</summary>
    public void Scroll(int tabs)
    {
        _offset += tabs * _tabWidth;
        InvalidateArrange();
    }

    /// <summary>次に並べるときに、選んでいるタブが見える位置まで送る。</summary>
    public void ShowSelected()
    {
        _showSelected = true;
        InvalidateArrange();
    }

    private double MaxOffset(double viewport)
        => Math.Max(0, InternalChildren.Count * _tabWidth - viewport);

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        _tabWidth = count == 0
            ? 0
            : Math.Clamp(availableSize.Width / count, MinTabWidth, MaxTabWidth);

        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(_tabWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(Math.Min(count * _tabWidth, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_showSelected)
        {
            _showSelected = false;
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                if (InternalChildren[i] is TabItem { IsSelected: true })
                {
                    _offset = Math.Min(_offset, i * _tabWidth);
                    _offset = Math.Max(_offset, (i + 1) * _tabWidth - finalSize.Width);
                    break;
                }
            }
        }

        // タブを閉じたりウィンドウを広げたりすると、右に空きができる。空きを残さない
        _offset = Math.Clamp(_offset, 0, MaxOffset(finalSize.Width));

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(new Rect(i * _tabWidth - _offset, 0, _tabWidth, finalSize.Height));
        }

        ScrollChanged?.Invoke(this, EventArgs.Empty);
        return finalSize;
    }
}
