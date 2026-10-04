using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using NexLauncher.Services.QuickCss;

namespace NexLauncher.Views;

/// <summary>Underline diagnostic lines using the editor's actual visual geometry, including scrolling.</summary>
public sealed class QuickCssErrorRenderer : IBackgroundRenderer
{
    private readonly HashSet<int> _lines = new();
    private static readonly Pen ErrorPen = new(new SolidColorBrush(Color.Parse("#FF707D")), 1.25);
    public KnownLayer Layer => KnownLayer.Selection;
    public int LineCount => _lines.Count;
    public void SetDiagnostics(IEnumerable<QuickCssDiagnostic> diagnostics)
    { _lines.Clear(); foreach (var item in diagnostics.Where(x => x.Line > 0)) _lines.Add(item.Line); }
    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid || textView.Document is null) return;
        foreach (var visual in textView.VisualLines)
            for (var number = visual.FirstDocumentLine.LineNumber; number <= visual.LastDocumentLine.LineNumber; number++)
            {
                if (!_lines.Contains(number)) continue;
                var line = textView.Document.GetLineByNumber(number);
                var text = textView.Document.GetText(line); var leading = text.Length - text.TrimStart().Length;
                var segment = new TextSegment { StartOffset = line.Offset + leading, Length = System.Math.Max(0, text.TrimEnd().Length - leading) };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    // A single minified/invalid line can be 128 KiB: draw only the visible portion.
                    var start = System.Math.Max(0, rect.Left);
                    var end = System.Math.Min(textView.Bounds.Width, rect.Left + System.Math.Max(4, rect.Width)); var y = rect.Bottom - 1;
                    for (var x = start; x < end; x += 4)
                    { var mid = System.Math.Min(x + 2, end); var right = System.Math.Min(x + 4, end); context.DrawLine(ErrorPen, new Point(x, y), new Point(mid, y-2)); context.DrawLine(ErrorPen, new Point(mid, y-2), new Point(right, y)); }
                }
            }
    }
}
