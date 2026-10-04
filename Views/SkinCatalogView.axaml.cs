using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NexLauncher.ViewModels;

namespace NexLauncher.Views;

public partial class SkinCatalogView : UserControl
{
    private SkinCatalogViewModel? _observed;
    public SkinCatalogView() { InitializeComponent(); SizeChanged += (_, _) => ArrangeDetails(); }
    private void ArrangeDetails()
    {
        var wide = Bounds.Width >= 720;
        DetailsLayout.ColumnDefinitions[1].Width = new GridLength(wide ? 300 : 0);
        Grid.SetColumn(DetailsActions, wide ? 1 : 0); Grid.SetRow(DetailsActions, wide ? 0 : 1);
        DetailsActions.Margin = wide ? new Thickness(18, 0, 0, 0) : new Thickness(0, 16, 0, 0);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Observe(); }
    protected override void OnDataContextChanged(EventArgs e) { base.OnDataContextChanged(e); Observe(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observed is not null) _observed.PropertyChanged -= Changed;
        _observed = null; base.OnDetachedFromVisualTree(e);
    }
    private void Observe()
    {
        if (_observed is not null) _observed.PropertyChanged -= Changed;
        _observed = DataContext as SkinCatalogViewModel;
        if (_observed is not null) _observed.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SkinCatalogViewModel.IsDetails) && _observed?.IsDetails == true)
            _observed.ScrollOffset = CardsScroll.Offset.Y;
        if (e.PropertyName == nameof(SkinCatalogViewModel.SearchRevision) ||
            e.PropertyName == nameof(SkinCatalogViewModel.IsDetails) && _observed?.IsDetails == false)
            Dispatcher.UIThread.Post(() =>
            { if (_observed is { IsDetails: false }) CardsScroll.Offset = new Vector(0, _observed.ScrollOffset); }, DispatcherPriority.Loaded);
    }
}
