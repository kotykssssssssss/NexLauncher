using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using NexLauncher.Models;
using NexLauncher.Services.Loaders;

namespace NexLauncher.ViewModels;

public partial class LoaderOptionsViewModel(ILoaderCatalog catalog, Action changed) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _loading;
    private string _minecraft = "";
    public ModLoader[] Loaders { get; } = Enum.GetValues<ModLoader>();
    public ObservableCollection<LoaderRelease> Versions { get; } = new();
    [ObservableProperty] private ModLoader _loader;
    [ObservableProperty] private LoaderRelease? _selectedRelease;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "Vanilla · без модов";
    public bool NeedsVersion => Loader != ModLoader.Vanilla;
    public bool CanCreate => !IsLoading && (Loader == ModLoader.Vanilla || SelectedRelease?.MinecraftVersion == _minecraft);
    partial void OnLoaderChanged(ModLoader value) { OnPropertyChanged(nameof(NeedsVersion)); _ = RefreshAsync(); }
    partial void OnSelectedReleaseChanged(LoaderRelease? value) { OnPropertyChanged(nameof(CanCreate)); changed(); }
    partial void OnIsLoadingChanged(bool value) { OnPropertyChanged(nameof(CanCreate)); changed(); }
    public void SetMinecraft(string? minecraft) { _minecraft = minecraft ?? ""; _ = RefreshAsync(); }
    private async Task RefreshAsync()
    {
        _loading?.Cancel();
        using var current = new CancellationTokenSource(); _loading = current;
        Versions.Clear(); SelectedRelease = null;
        if (Loader == ModLoader.Vanilla || string.IsNullOrEmpty(_minecraft))
        { Message = Loader == ModLoader.Vanilla ? "Vanilla · без модов" : "Сначала выбери Minecraft."; IsLoading = false; _loading = null; return; }
        IsLoading = true; Message = "Получаем версии с официального сервера…";
        try
        {
            var releases = await catalog.GetVersionsAsync(Loader, _minecraft, current.Token);
            if (!ReferenceEquals(_loading, current)) return;
            foreach (var item in releases) Versions.Add(item);
            SelectedRelease = Versions.FirstOrDefault(x => x.Stable) ?? Versions.FirstOrDefault();
            Message = Versions.Count == 0 ? "Нет совместимых версий. Forge поддерживается с 1.13; NeoForge — с 1.20.2. Попробуй другой Minecraft." : $"Доступно {Versions.Count} версий загрузчика.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(_loading, current)) Message = "Не удалось получить версии: " + ex.Message; }
        finally { if (ReferenceEquals(_loading, current)) { _loading = null; IsLoading = false; } }
    }
    public void Dispose() { _loading?.Cancel(); _loading = null; }
}
