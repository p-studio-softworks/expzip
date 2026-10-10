using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Expzip.Ui;

/// <summary>
/// 大きさの揃った項目を、折り返して並べる一覧の台 (#216)。アイコンの形と「一覧」の形で使う。
/// </summary>
/// <remarks>
/// <para>
/// **見えている項目しか部品を作らない (仮想化)。**WPF の <c>WrapPanel</c> は全部の項目に部品を作るので、
/// 数万個の書庫では開くたびに固まる (詳細の形の一覧と同じ理由、#14)。
/// 項目の大きさが揃っているので、位置は番号から計算で決まる。
/// </para>
/// <para>
/// 横に並べるとき (<see cref="Orientation.Horizontal"/>) は左から右へ並べて下へ折り返し、縦に送る。
/// 縦に並べるとき (「一覧」の形) は上から下へ並べて右へ折り返し、横に送る。どちらもエクスプローラーと同じ。
/// </para>
/// <para>
/// 送る向きに並ぶ 1 段を「段」、段の中の位置を「枠」と呼ぶ。横に並べるときは段が行、枠が列になる。
/// </para>
/// </remarks>
internal sealed class EntryWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(EntryWrapPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(EntryWrapPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(EntryWrapPanel),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>項目 1 個の幅。</summary>
    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>項目 1 個の高さ。</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>並べる向き。</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    /// <summary>横に並べて下へ折り返すか。</summary>
    private bool Rows => Orientation == Orientation.Horizontal;

    /// <summary>段の間隔 (送る向きの 1 段の大きさ)。</summary>
    private double LinePitch => Rows ? ItemHeight : ItemWidth;

    /// <summary>枠の間隔。</summary>
    private double SlotPitch => Rows ? ItemWidth : ItemHeight;

    /// <summary>1 段に入る項目の数。最後に並べたときの値。</summary>
    public int PerLine { get; private set; } = 1;

    private int Count => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    // ------------------------------------------------------------------ 並べる

    protected override Size MeasureOverride(Size availableSize)
    {
        // 部品を作る仕組みは、子の一覧に触れたときに用意される
        _ = InternalChildren;

        var count = Count;
        var slotSpace = Rows ? availableSize.Width : availableSize.Height;
        PerLine = double.IsInfinity(slotSpace)
            ? Math.Max(1, count)
            : Math.Max(1, (int)Math.Floor(slotSpace / SlotPitch));

        var lines = count == 0 ? 0 : (count + PerLine - 1) / PerLine;
        var extent = Rows
            ? new Size(PerLine * ItemWidth, lines * ItemHeight)
            : new Size(lines * ItemWidth, PerLine * ItemHeight);

        UpdateScrollInfo(availableSize, extent);

        var (first, last) = RealizedRange(count);
        Realize(first, last);
        CleanUp(first, last);

        return new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;

        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0)
            {
                continue;
            }

            var cell = CellBounds(index);
            cell.Offset(-_offset.X, -_offset.Y);
            children[i].Arrange(cell);
        }

        return finalSize;
    }

    /// <summary>部品を作る項目の範囲。見えている段の前後に 1 段ずつ余分に取る。</summary>
    private (int First, int Last) RealizedRange(int count)
    {
        if (count == 0)
        {
            return (0, -1);
        }

        var view = Rows ? _viewport.Height : _viewport.Width;
        if (double.IsInfinity(view) || view <= 0)
        {
            return (0, count - 1);
        }

        var offset = Rows ? _offset.Y : _offset.X;
        var firstLine = Math.Max(0, (int)Math.Floor(offset / LinePitch) - 1);
        var lastLine = (int)Math.Ceiling((offset + view) / LinePitch);

        return (firstLine * PerLine, Math.Min(count - 1, (lastLine + 1) * PerLine - 1));
    }

    /// <summary>範囲の項目に部品を作り、大きさを測る。</summary>
    private void Realize(int first, int last)
    {
        if (last < first)
        {
            return;
        }

        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        var size = new Size(ItemWidth, ItemHeight);

        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var i = first; i <= last; i++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child)
                {
                    break;
                }

                if (newlyRealized)
                {
                    if (childIndex >= children.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                child.Measure(size);
            }
        }
    }

    /// <summary>
    /// 範囲の外へ出た部品を捨てる。ただし、キーボードの入力先を持つ部品は残す。
    /// 捨てると入力先が一覧の外へ飛び、矢印キーで続けて選べなくなる。
    /// </summary>
    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;

        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last || children[i].IsKeyboardFocusWithin)
            {
                continue;
            }

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            // 中身を丸ごと差し替えた (別のフォルダーへ移った、並べ替えた)。先頭から見せる
            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0)
                {
                    RemoveInternalChildRange(0, InternalChildren.Count);
                }

                _offset = default;
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }

        base.OnItemsChanged(sender, args);
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index >= 0 && index < Count)
        {
            ScrollCellIntoView(index);
        }
    }

    // ------------------------------------------------------------------ 位置の計算

    /// <summary>項目の位置と大きさ。スクロールの分は引かない (中身の座標)。</summary>
    public Rect CellBounds(int index)
    {
        var line = index / PerLine;
        var slot = index % PerLine;
        return Rows
            ? new Rect(slot * ItemWidth, line * ItemHeight, ItemWidth, ItemHeight)
            : new Rect(line * ItemWidth, slot * ItemHeight, ItemWidth, ItemHeight);
    }

    /// <summary>項目の段と枠。</summary>
    public (int Line, int Slot) Locate(int index) => (index / PerLine, index % PerLine);

    /// <summary>段と枠から項目の番号を出す。</summary>
    public int IndexAt(int line, int slot) => line * PerLine + slot;

    /// <summary>
    /// 中身の座標の四角にかかる段と枠の範囲。境目にちょうど触れただけの項目は含めない。
    /// 段が無ければ LastLine が FirstLine より小さい。
    /// </summary>
    public (int FirstLine, int LastLine, int FirstSlot, int LastSlot) CellsWithin(Rect area)
    {
        var (lineLow, lineHigh, slotLow, slotHigh) = Rows
            ? (area.Top, area.Bottom, area.Left, area.Right)
            : (area.Left, area.Right, area.Top, area.Bottom);

        var count = Count;
        var lines = count == 0 ? 0 : (count + PerLine - 1) / PerLine;

        return (
            Math.Max(0, (int)Math.Floor(lineLow / LinePitch)),
            Math.Min(lines - 1, (int)Math.Ceiling(lineHigh / LinePitch) - 1),
            Math.Max(0, (int)Math.Floor(slotLow / SlotPitch)),
            Math.Min(PerLine - 1, (int)Math.Ceiling(slotHigh / SlotPitch) - 1));
    }

    /// <summary>項目が見えるところまでスクロールする。</summary>
    private void ScrollCellIntoView(int index)
    {
        var cell = CellBounds(index);

        var x = _offset.X;
        if (cell.Right > x + _viewport.Width)
        {
            x = cell.Right - _viewport.Width;
        }

        if (cell.Left < x)
        {
            x = cell.Left;
        }

        var y = _offset.Y;
        if (cell.Bottom > y + _viewport.Height)
        {
            y = cell.Bottom - _viewport.Height;
        }

        if (cell.Top < y)
        {
            y = cell.Top;
        }

        SetHorizontalOffset(x);
        SetVerticalOffset(y);
    }

    // ------------------------------------------------------------------ スクロール (IScrollInfo)

    /// <summary>1 回の送りの大きさ。詳細の形の一覧と同じく 16 px を 1 行とみなす。</summary>
    private const double LineStep = 16;

    private static double WheelStep => LineStep * SystemParameters.WheelScrollLines;

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private void UpdateScrollInfo(Size available, Size extent)
    {
        var viewport = new Size(
            double.IsInfinity(available.Width) ? extent.Width : available.Width,
            double.IsInfinity(available.Height) ? extent.Height : available.Height);

        if (viewport == _viewport && extent == _extent)
        {
            return;
        }

        _viewport = viewport;
        _extent = extent;

        // 窓を広げたり項目が減ったりして、送れる量より先にいたら戻す
        _offset.X = Clamp(_offset.X, _extent.Width - _viewport.Width);
        _offset.Y = Clamp(_offset.Y, _extent.Height - _viewport.Height);

        ScrollOwner?.InvalidateScrollInfo();
    }

    private static double Clamp(double value, double max)
        => double.IsNaN(value) ? 0 : Math.Max(0, Math.Min(value, Math.Max(0, max)));

    public void SetHorizontalOffset(double offset)
    {
        offset = Clamp(offset, _extent.Width - _viewport.Width);
        if (offset == _offset.X)
        {
            return;
        }

        _offset.X = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetVerticalOffset(double offset)
    {
        // 「一覧」の形は横にしか送れない。一覧が Home / End で頼む「縦の端まで」は、横の端までにする。
        // そうしないと End で最後の項目へ行けず、見えている中の最後で止まる
        if (!Rows && double.IsInfinity(offset))
        {
            SetHorizontalOffset(offset);
            return;
        }

        offset = Clamp(offset, _extent.Height - _viewport.Height);
        if (offset == _offset.Y)
        {
            return;
        }

        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - (Rows ? ItemHeight : LineStep));

    public void LineDown() => SetVerticalOffset(_offset.Y + (Rows ? ItemHeight : LineStep));

    public void LineLeft() => SetHorizontalOffset(_offset.X - (Rows ? LineStep : ItemWidth));

    public void LineRight() => SetHorizontalOffset(_offset.X + (Rows ? LineStep : ItemWidth));

    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);

    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);

    // 「一覧」の形は横にしか送れないので、ホイールで横に送る (エクスプローラーと同じ)
    public void MouseWheelUp()
    {
        if (Rows)
        {
            SetVerticalOffset(_offset.Y - WheelStep);
        }
        else
        {
            SetHorizontalOffset(_offset.X - ItemWidth);
        }
    }

    public void MouseWheelDown()
    {
        if (Rows)
        {
            SetVerticalOffset(_offset.Y + WheelStep);
        }
        else
        {
            SetHorizontalOffset(_offset.X + ItemWidth);
        }
    }

    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - WheelStep);

    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + WheelStep);

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        // 渡されるのは部品の中のどこか。この台の直下の子までさかのぼる
        DependencyObject? current = visual;
        while (current is not null && !ReferenceEquals(VisualTreeHelper.GetParent(current), this))
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is not UIElement child)
        {
            return Rect.Empty;
        }

        var position = InternalChildren.IndexOf(child);
        var index = position < 0
            ? -1
            : ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(position, 0));
        if (index < 0)
        {
            return Rect.Empty;
        }

        ScrollCellIntoView(index);

        var cell = CellBounds(index);
        cell.Offset(-_offset.X, -_offset.Y);
        return cell;
    }
}
