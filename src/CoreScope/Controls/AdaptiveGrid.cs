using System;
using System.Windows;
using System.Windows.Controls;

namespace CoreScope.Controls;

/// <summary>
/// Equal-width grid that picks its column count from the available width: as many columns as fit at
/// <see cref="MinItemWidth"/>, never more than the item count. Items wrap onto extra rows on narrow windows
/// instead of being clipped (UniformGrid Rows=1 clips when the window is small or the DPI is high).
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    private int _columns = 1;

    private int VisibleCount()
    {
        int n = 0;
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed) n++;
        return n;
    }

    protected override Size MeasureOverride(Size available)
    {
        int count = VisibleCount();
        if (count == 0) return new Size(0, 0);
        double width = double.IsInfinity(available.Width) ? MinItemWidth * count : available.Width;
        _columns = Math.Clamp((int)(width / Math.Max(1, MinItemWidth)), 1, count);
        // Balance rows: 6 items in 4 columns looks worse than 3 + 3.
        int rows = (count + _columns - 1) / _columns;
        _columns = (count + rows - 1) / rows;

        double cellWidth = width / _columns;
        double rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        return new Size(width, rowHeight * rows);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int count = VisibleCount();
        if (count == 0) return final;
        int rows = (count + _columns - 1) / _columns;
        double cellWidth = final.Width / _columns;
        double rowHeight = final.Height / Math.Max(1, rows);
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Arrange(new Rect(i % _columns * cellWidth, i / _columns * rowHeight, cellWidth, rowHeight));
            i++;
        }
        return final;
    }
}
