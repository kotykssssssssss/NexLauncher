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
    public ModrinthView() => InitializeComponent();
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
            Dispatcher.UIThread.Post(() => PageHeading.BringIntoView());
    }
}
