using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NexLauncher.Services.Sessions;

namespace NexLauncher.Views;

/// <summary>Native, bounded chart. Hover exposes the exact point; no browser or rendering dependency.</summary>
public sealed class StatsChart : Control
{
    public static readonly StyledProperty<ChartPoint[]> DataProperty = AvaloniaProperty.Register<StatsChart, ChartPoint[]>(nameof(Data), []);
    public static readonly StyledProperty<IBrush> AccentProperty = AvaloniaProperty.Register<StatsChart, IBrush>(nameof(Accent), Brushes.MediumAquamarine);
    public static readonly StyledProperty<IBrush> TextBrushProperty = AvaloniaProperty.Register<StatsChart, IBrush>(nameof(TextBrush), Brushes.LightGray);
    public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<StatsChart, string>(nameof(Unit), "");
    public static readonly StyledProperty<bool> IsLineProperty = AvaloniaProperty.Register<StatsChart, bool>(nameof(IsLine));
    public ChartPoint[] Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IBrush TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public bool IsLine { get => GetValue(IsLineProperty); set => SetValue(IsLineProperty, value); }
    static StatsChart() => AffectsRender<StatsChart>(DataProperty, AccentProperty, TextBrushProperty, UnitProperty, IsLineProperty);
    private ChartPoint[] Points => Data.Where(x => double.IsFinite(x.Value) && x.Value >= 0).TakeLast(120).ToArray();
    public override void Render(DrawingContext context)
    {
        base.Render(context); var points = Points;
        var width = Math.Max(1, Bounds.Width - 12); var height = Math.Max(1, Bounds.Height - 50); var bottom = height + 24;
        void Text(string text, double x, double y) => context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11, TextBrush), new Point(x, y));
        if (points.Length == 0) { Text("Нет данных за выбранный период", 8, height / 2); return; }
        var max = Math.Max(1, points.Max(x => x.Value)); var step = width / points.Length;
        context.DrawLine(new Pen(TextBrush, 0.5), new Point(6, bottom), new Point(width, bottom));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(35, 160, 174, 184)), .5);
        for (var i = 1; i <= 3; i++) context.DrawLine(grid, new Point(6, bottom - height * i / 3), new Point(width, bottom - height * i / 3));
        Point? previous = null;
        for (var i = 0; i < points.Length; i++)
        {
            var h = points[i].Value / max * height;
            var center = 6 + (i + .5) * step;
            if (IsLine)
            {
                var point = new Point(center, bottom - h);
                if (previous is { } last) context.DrawLine(new Pen(Accent, 2), last, point);
                context.DrawEllipse(Accent, null, point, 2.5, 2.5); previous = point;
            }
            else
            {
                var barWidth = Math.Min(72, Math.Max(.5, step * .72));
                context.DrawRectangle(Accent, null, new Rect(center - barWidth / 2, bottom - h, barWidth, h), 3, 3);
            }
        }
        Text($"max {max:0.##} {Unit}", 6, 0);
        var first = points[0].Label; if (first.Length > 24) first = first[..21] + "…";
        Text(first, points.Length == 1 ? Math.Max(6, width / 2 - 36) : 6, bottom + 8);
        if (points.Length > 1) Text(points[^1].Label.Length > 24 ? points[^1].Label[..21] + "…" : points[^1].Label, Math.Max(6, width - 135), bottom + 8);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var points = Points;
        if (points.Length == 0) return;
        var index = Math.Clamp((int)((e.GetPosition(this).X - 6) / Math.Max(1, Bounds.Width - 12) * points.Length), 0, points.Length - 1);
        ToolTip.SetTip(this, $"{points[index].Label}: {points[index].Value:0.##} {Unit}");
    }
}
