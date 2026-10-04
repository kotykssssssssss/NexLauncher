using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NexLauncher.Models;
using NexLauncher.Services.Skins;

namespace NexLauncher.Views;

public sealed class SkinPreview : Control
{
    public static readonly StyledProperty<SkinImage?> SkinProperty = AvaloniaProperty.Register<SkinPreview, SkinImage?>(nameof(Skin));
    public static readonly StyledProperty<SkinModel> ModelProperty = AvaloniaProperty.Register<SkinPreview, SkinModel>(nameof(Model));
    public SkinImage? Skin { get => GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public SkinModel Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    private readonly SkinPreviewRenderer _renderer = new();
    private Bitmap? _frame;
    private CancellationTokenSource? _request;
    private bool _detached;
    private Point? _drag;
    private double _yaw = -.35, _zoom = 1;
    public SkinPreview() { ClipToBounds = true; Focusable = true; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == SkinProperty || change.Property == ModelProperty) Schedule(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _detached = false; Schedule(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _detached = true; _request?.Cancel(); _request?.Dispose(); _request = null; _frame?.Dispose(); _frame = null; base.OnDetachedFromVisualTree(e); }
    private void Schedule()
    {
        if (_detached) return;
        _request?.Cancel(); _request?.Dispose(); var source = _request = new CancellationTokenSource(); var token = source.Token;
        var skin = Skin; var model = Model; var yaw = _yaw; var zoom = _zoom;
        _ = UpdateAsync();
        async Task UpdateAsync()
        {
            Bitmap? next = null;
            try
            {
                await Task.Delay(25, token);
                var bytes = await Task.Run(() => _renderer.Render(skin, model, yaw, zoom, token), token);
                using var stream = new MemoryStream(bytes, false); next = new Bitmap(stream);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_detached || token.IsCancellationRequested || !ReferenceEquals(_request, source)) return;
                    var old = _frame; _frame = next; next = null; old?.Dispose(); InvalidateVisual();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // A failed preview must never terminate the UI or affect a saved/account skin.
                System.Diagnostics.Trace.WriteLine("Skin preview rendering failed.");
            }
            finally { next?.Dispose(); }
        }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_frame is null) return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        context.DrawImage(_frame, new Rect((Bounds.Width-size)/2, (Bounds.Height-size)/2, size, size));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    { base.OnPointerPressed(e); if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _drag = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true; } }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); if (_drag is { } previous) { var point = e.GetPosition(this); _yaw += (point.X-previous.X)*.015; _drag = point; Schedule(); e.Handled = true; } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); _drag = null; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { base.OnPointerWheelChanged(e); _zoom = Math.Clamp(_zoom + e.Delta.Y*.1, .6, 1.6); Schedule(); e.Handled = true; }
}
