using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CoreScope.Controls;

/// <summary>
/// Lightweight rolling line graph drawn directly in OnRender (no chart library needed).
/// The newest sample is always at the right edge; <see cref="Capacity"/> samples span the width.
/// </summary>
public sealed class SparkChart : FrameworkElement
{
    private static readonly Brush FallbackStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(IReadOnlyList<double>), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(SparkChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SparkChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(SparkChart),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(SparkChart),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpanSecondsProperty = DependencyProperty.Register(
        nameof(SpanSeconds), typeof(double), typeof(SparkChart), new FrameworkPropertyMetadata(60.0));

    public static readonly DependencyProperty ValueFormatterProperty = DependencyProperty.Register(
        nameof(ValueFormatter), typeof(Func<double, string>), typeof(SparkChart), new FrameworkPropertyMetadata(null));

    /// <summary>Time covered by a full graph, for the hover tooltip ("3 min ago").</summary>
    public double SpanSeconds { get => (double)GetValue(SpanSecondsProperty); set => SetValue(SpanSecondsProperty, value); }
    public Func<double, string>? ValueFormatter { get => (Func<double, string>?)GetValue(ValueFormatterProperty); set => SetValue(ValueFormatterProperty, value); }

    private int _hover = -1;
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, StaysOpen = true };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var data = Data;
        if (data is null || data.Count < 2 || ActualWidth < 4) return;
        int capacity = Math.Max(Capacity, data.Count);
        double step = ActualWidth / (capacity - 1);
        double startX = ActualWidth - step * (data.Count - 1);
        var pos = e.GetPosition(this);
        int index = (int)Math.Round((pos.X - startX) / step);
        if (index < 0 || index >= data.Count) { HideTip(); return; }
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
        double agoSeconds = SpanSeconds * (data.Count - 1 - index) / Math.Max(1, capacity - 1);
        string when = agoSeconds < 2 ? "now" : agoSeconds < 90 ? $"{agoSeconds:0} s ago" : agoSeconds < 5400 ? $"{agoSeconds / 60:0} min ago" : $"{agoSeconds / 3600:0.#} h ago";
        string value = ValueFormatter is { } f ? f(data[index]) : data[index].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        _tip.Content = $"{value} · {when}";
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = pos.X + 12;
        _tip.VerticalOffset = pos.Y - 32;
        _tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HideTip();
    }

    private void HideTip()
    {
        _tip.IsOpen = false;
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    public IReadOnlyList<double>? Data { get => (IReadOnlyList<double>?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        // Transparent hit-test surface so the control lays out and tooltips work even with no data.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var data = Data;
        if (data is null || data.Count < 2 || width < 4 || height < 4) return;

        double min = Minimum, max = Maximum;
        if (double.IsNaN(min) || double.IsNaN(max))
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var v in data)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            double pad = Math.Max((hi - lo) * 0.15, Math.Abs(hi) * 0.02 + 0.5);
            if (double.IsNaN(min)) min = Math.Max(0, lo - pad);
            if (double.IsNaN(max)) max = hi + pad;
        }
        if (max - min < 1e-9) max = min + 1;

        int capacity = Math.Max(Capacity, data.Count);
        double step = width / (capacity - 1);
        double startX = width - step * (data.Count - 1);
        double inset = StrokeThickness / 2;

        Point PointAt(int i)
        {
            double t = (Math.Clamp(data[i], min, max) - min) / (max - min);
            return new Point(startX + i * step, inset + (height - 2 * inset) * (1 - t));
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineCtx = line.Open())
        using (var areaCtx = area.Open())
        {
            var first = PointAt(0);
            lineCtx.BeginFigure(first, false, false);
            areaCtx.BeginFigure(new Point(first.X, height), true, true);
            areaCtx.LineTo(first, false, false);
            for (int i = 1; i < data.Count; i++)
            {
                var p = PointAt(i);
                lineCtx.LineTo(p, true, true);
                areaCtx.LineTo(p, false, false);
            }
            areaCtx.LineTo(new Point(PointAt(data.Count - 1).X, height), false, false);
        }
        line.Freeze();
        area.Freeze();

        // Theme brushes (e.g. the Windows accent) are live and can't be frozen, so draw with a frozen copy of the color.
        var stroke = Stroke is SolidColorBrush accent ? Freeze(new SolidColorBrush(accent.Color)) : FallbackStroke;
        var fill = stroke is SolidColorBrush solid
            ? Freeze(new SolidColorBrush(solid.Color) { Opacity = 0.16 })
            : Freeze(new SolidColorBrush(Color.FromArgb(40, 0x3B, 0x82, 0xF6)));
        var pen = new Pen(stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);

        // Hover marker: a faint vertical guide and a dot on the line.
        if (_hover >= 0 && _hover < data.Count)
        {
            var p = PointAt(_hover);
            var guide = new Pen(Freeze(new SolidColorBrush(((SolidColorBrush)stroke).Color) { Opacity = 0.45 }), 1);
            guide.Freeze();
            dc.DrawLine(guide, new Point(p.X, 0), new Point(p.X, height));
            dc.DrawEllipse(stroke, null, p, 3.5, 3.5);
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
