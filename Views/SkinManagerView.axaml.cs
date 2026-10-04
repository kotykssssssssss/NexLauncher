using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace NexLauncher.Views;

public partial class SkinManagerView : UserControl
{
    public SkinManagerView() { InitializeComponent(); SizeChanged += (_, _) => Arrange(); }
    private void Arrange()
    {
        var wide = Bounds.Width >= 650;
        SkinLayout.ColumnDefinitions[1].Width = new GridLength(wide ? 320 : 0);
        Grid.SetColumn(SkinActions, wide ? 1 : 0); Grid.SetRow(SkinActions, wide ? 0 : 1);
        SkinActions.Margin = wide ? new Thickness(20, 0, 0, 0) : new Thickness(0, 16, 0, 0);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) Dispatcher.UIThread.Post(() => this.BringIntoView(), DispatcherPriority.Loaded);
    }
}
