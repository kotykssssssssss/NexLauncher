using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NexLauncher.ViewModels;

namespace NexLauncher.Views;
public partial class ModrinthView : UserControl
{
    private ModrinthViewModel? _observed;
    private bool? _wide;
    public ModrinthView() { InitializeComponent(); SizeChanged += (_, _) => ArrangeBrowser(); }
    private void ArrangeBrowser()
    {
        var wide = Bounds.Width >= 1000;
        BrowserLayout.ColumnDefinitions[0].Width = new GridLength(wide && _observed?.ShowInstalled != true ? 256 : 0);
        FiltersPanel.MaxHeight = wide ? double.PositiveInfinity : Math.Clamp(Bounds.Height * .2, 100, 160);
        if (_wide == wide) return;
        _wide = wide;
        Grid.SetColumn(FiltersPanel, wide ? 0 : 1); Grid.SetRow(FiltersPanel, wide ? 1 : 0);
        FiltersPanel.Margin = wide ? new Thickness(0, 0, 16, 0) : new Thickness(0, 0, 0, 8);
        FiltersExpander.IsExpanded = wide;
        DetailsLayout.ColumnDefinitions[1].Width = new GridLength(wide ? 320 : 0);
        Grid.SetColumn(DetailsActions, wide ? 1 : 0);
        DetailsActions.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 0, 0, 16);
        Grid.SetRow(DetailsDescription, wide ? 0 : 1);
    }
    protected override void OnDataContextChanged(EventArgs e) { base.OnDataContextChanged(e); Observe(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Observe(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observed is not null) _observed.PropertyChanged -= OnPageChanged;
        _observed = null; base.OnDetachedFromVisualTree(e);
    }
    private void Observe()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnPageChanged;
        _observed = DataContext as ModrinthViewModel;
        if (_observed is not null) _observed.PropertyChanged += OnPageChanged;
    }
    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModrinthViewModel.IsDetails))
        {
            if (_observed?.IsDetails == true) { _observed.ScrollOffset = ResultsScroll.Offset.Y; DetailsScroll.Offset = default; }
            else RestoreOffset();
        }
        if (e.PropertyName == nameof(ModrinthViewModel.SearchRevision)) RestoreOffset();
        if (e.PropertyName == nameof(ModrinthViewModel.ShowInstalled)) ArrangeBrowser();
    }
    private void RestoreOffset() => Dispatcher.UIThread.Post(() =>
    { if (_observed is { IsDetails: false }) ResultsScroll.Offset = new Vector(0, _observed.ScrollOffset); }, DispatcherPriority.Loaded);
}
