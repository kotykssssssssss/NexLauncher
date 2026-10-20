using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using NexLauncher.Models;

namespace NexLauncher.ViewModels;

public partial class MainWindowViewModel
{
    private bool _changingHomeLayout;
    private bool _settingHomeLayoutChoice;
    public HomeLayoutChoice[] HomeLayouts => HomeLayoutChoice.Choices;
    [ObservableProperty] private HomeLayoutChoice _selectedHomeLayout = HomeLayoutChoice.Choices[0];
    public Task HomeLayoutChangeTask { get; private set; } = Task.CompletedTask;
    public bool IsDefaultHomeLayout => SelectedHomeLayout?.Layout is null or HomeLayout.Default;
    public bool IsTilesHomeLayout => SelectedHomeLayout?.Layout == HomeLayout.Tiles;
    public bool IsListHomeLayout => SelectedHomeLayout?.Layout == HomeLayout.List;
    public bool ShowDefaultHomeSelector => IsDefaultHomeLayout && HasInstance;

    partial void OnSelectedHomeLayoutChanged(HomeLayoutChoice value)
    {
        OnPropertyChanged(nameof(IsDefaultHomeLayout));
        OnPropertyChanged(nameof(IsTilesHomeLayout));
        OnPropertyChanged(nameof(IsListHomeLayout));
        OnPropertyChanged(nameof(ShowDefaultHomeSelector));
        if (_settingHomeLayoutChoice || _loadingConfiguration || value is null) return;
        HomeLayoutChangeTask = ChangeHomeLayoutAsync(value.Layout);
    }
    private async Task ChangeHomeLayoutAsync(HomeLayout layout)
    {
        if (_changingHomeLayout || !IsEditable) { RefreshHomeLayoutChoice(); return; }
        _changingHomeLayout = true;
        var previous = _configuration.HomeLayout;
        _configuration.HomeLayout = layout;
        try
        {
            await QuickCss.SetHomeLayoutAsync(layout);
            Notice = "Вид главного экрана сохранён в настройках и Quick CSS.";
        }
        catch (Exception ex)
        {
            _configuration.HomeLayout = previous;
            ShowError(ex, "Не удалось синхронизировать вид главного экрана с CSS. Файл не заменён целиком.");
        }
        finally { _changingHomeLayout = false; RefreshHomeLayoutChoice(); }
    }
    private void RefreshHomeLayoutChoice()
    {
        if (_changingHomeLayout) return;
        _settingHomeLayoutChoice = true;
        try { SelectedHomeLayout = HomeLayouts.Single(x => x.Layout == (QuickCss.ThemeHomeLayout ?? _configuration.HomeLayout)); }
        finally { _settingHomeLayoutChoice = false; }
    }
}
