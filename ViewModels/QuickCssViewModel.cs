using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services.QuickCss;

namespace NexLauncher.ViewModels;

public partial class QuickCssViewModel : ObservableObject, IDisposable
{
    private readonly string _dataDirectory;
    private readonly Func<QuickCssSettings, Task> _save;
    private readonly Func<bool> _canEdit;
    private IQuickCssService? _service;
    private Func<Task<string?>>? _pick;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private Dictionary<string, string> _appearance = new();
    private IReadOnlyDictionary<string, string>? _themeAppearance;
    public AppearanceViewModel Appearance { get; }
    public Func<string, IReadOnlyDictionary<string, string>, Task<bool>>? UpdateEditorTokensAsync { get; set; }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _autoReload = true;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _status = "Quick CSS выключен.";
    [ObservableProperty] private string _diagnostics = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private HomeLayout? _themeHomeLayout;
    public Func<string, HomeLayout, Task<bool>>? UpdateEditorHomeLayoutAsync { get; set; }
    public bool HasDiagnostics => Diagnostics.Length > 0;
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand BrowseCommand { get; }
    public IAsyncRelayCommand CreateExampleCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }
    public IRelayCommand OpenExternalCommand { get; }
    public Func<string, Task>? OpenEditorAsync { get; set; }

    public QuickCssViewModel(string dataDirectory, Func<QuickCssSettings, Task> save, Func<bool> canEdit)
    {
        _dataDirectory = dataDirectory; _save = save; _canEdit = canEdit;
        Appearance = new AppearanceViewModel(SetAppearanceAsync, CanEdit);
        ApplyCommand = new AsyncRelayCommand(() => GuardAsync(ApplyAsync), CanEdit);
        // Both actions persist pending path/toggle edits and reload from disk.
        ReloadCommand = new AsyncRelayCommand(() => GuardAsync(ApplyAsync), CanEdit);
        BrowseCommand = new AsyncRelayCommand(() => GuardAsync(async () =>
        {
            var path = _pick is null ? null : await _pick();
            if (path is not null) FilePath = path;
        }), CanEdit);
        CreateExampleCommand = new AsyncRelayCommand(() => GuardAsync(async () =>
        {
            FilePath = await CreateFileAsync(reuseExisting: false); Enabled = true;
            await ApplyAsync();
        }), CanEdit);
        OpenCommand = new AsyncRelayCommand(() => GuardAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(FilePath)) FilePath = await CreateFileAsync(reuseExisting: true);
            var path = Path.GetFullPath(FilePath.Trim());
            if (!path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выбери .css файл.");
            if (OpenEditorAsync is not null) await OpenEditorAsync(path);
        }), () => CanEdit() && OpenEditorAsync is not null);
        OpenExternalCommand = new RelayCommand(() =>
        {
            try
            {
                var path = Path.GetFullPath(FilePath);
                if (!path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new InvalidOperationException("Выбери существующий .css файл.");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { Status = "Не удалось открыть CSS. Выбери файл или назначь редактор для .css в системе."; }
        }, () => CanEdit() && !string.IsNullOrWhiteSpace(FilePath));
    }

    private async Task<string> CreateFileAsync(bool reuseExisting, bool minimal = false)
    {
        var folder = Path.Combine(_dataDirectory, "themes"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "quickcss.css");
        if (File.Exists(path))
        {
            if (reuseExisting) return path;
            path = Path.Combine(folder, $"quickcss-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.css");
        }
        if (minimal)
        {
            await using var minimalFile = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await minimalFile.WriteAsync(System.Text.Encoding.UTF8.GetBytes("/* NexLauncher · общие параметры оформления из настроек. */\n"), _lifetime.Token);
            return path;
        }
        await using var source = typeof(QuickCssViewModel).Assembly.GetManifestResourceStream("NexLauncher.QuickCssExample")
            ?? throw new InvalidOperationException("Встроенный пример Quick CSS недоступен.");
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await source.CopyToAsync(target, _lifetime.Token); return path;
    }

    public async Task ApplyEditorFileAsync(string path)
    {
        if (!CanEdit()) throw new InvalidOperationException("Дождись завершения другой операции перед применением темы.");
        IsBusy = true;
        try { FilePath = path; Enabled = true; await ApplyAsync(); }
        finally { IsBusy = false; }
    }

    public async Task AttachAsync(IQuickCssService service, Func<Task<string?>> pick)
    {
        if (_disposed) { service.Dispose(); return; }
        if (_service is not null) _service.Changed -= OnChanged;
        _service?.Dispose();
        _service = service; _pick = pick;
        _service.Changed += OnChanged;
        Show(await _service.ConfigureAsync(Settings(), _lifetime.Token));
    }

    public async Task InitializeAsync(QuickCssSettings settings)
    {
        _appearance = QuickCssAppearance.ValidateAndCopy(settings.Appearance);
        Appearance.Refresh(_appearance, null);
        Enabled = settings.Enabled; FilePath = settings.FilePath; AutoReload = settings.AutoReload;
        if (_service is not null) Show(await _service.ConfigureAsync(Settings(), _lifetime.Token));
    }

    private QuickCssSettings Settings() => new() { Enabled = Enabled, FilePath = FilePath.Trim(), AutoReload = AutoReload, Appearance = new(_appearance) };
    private async Task SetAppearanceAsync(IReadOnlyDictionary<string, string> values)
    {
        if (!CanEdit()) throw new InvalidOperationException("Дождись завершения другой операции Quick CSS.");
        var normalized = QuickCssAppearance.ValidateAndCopy(values);
        IsBusy = true;
        var previous = _appearance;
        try
        {
            if (string.IsNullOrWhiteSpace(FilePath)) FilePath = await CreateFileAsync(reuseExisting: true, minimal: true);
            if (!Path.IsPathFullyQualified(FilePath.Trim()) || !FilePath.Trim().EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выбери полный путь к .css файлу. Оформление не записано.");
            var path = Path.GetFullPath(FilePath.Trim());
            if (UpdateEditorTokensAsync is null || !await UpdateEditorTokensAsync(path, normalized))
            {
                var files = new QuickCssEditorFiles();
                var file = await files.LoadAsync(path, _lifetime.Token);
                var rewritten = await Task.Run(() => QuickCssRootTokens.Rewrite(file.Text, normalized), _lifetime.Token);
                await files.SaveAsync(path, rewritten, file.Revision, _lifetime.Token);
            }
            _appearance = normalized;
            await ApplyAsync();
            Appearance.Refresh(_appearance, _themeAppearance);
        }
        catch { _appearance = previous; throw; }
        finally { IsBusy = false; }
    }
    public async Task SetHomeLayoutAsync(HomeLayout layout)
    {
        if (!CanEdit()) throw new InvalidOperationException("Дождись завершения другой операции Quick CSS.");
        IsBusy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(FilePath)) FilePath = await CreateFileAsync(reuseExisting: true);
            var path = Path.GetFullPath(FilePath.Trim());
            if (UpdateEditorHomeLayoutAsync is null || !await UpdateEditorHomeLayoutAsync(path, layout))
            {
                var files = new QuickCssEditorFiles();
                var file = await files.LoadAsync(path, _lifetime.Token);
                var rewritten = await Task.Run(() => QuickCssHomeLayout.Rewrite(file.Text, layout), _lifetime.Token);
                await files.SaveAsync(path, rewritten, file.Revision, _lifetime.Token);
            }
            // Preserve the enabled/disabled switch; choosing a layout never enables a theme.
            await ApplyAsync();
        }
        finally { IsBusy = false; }
    }
    private async Task ApplyAsync()
    {
        var settings = Settings();
        if (settings.Enabled && (!Path.IsPathFullyQualified(settings.FilePath) ||
            !settings.FilePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Выбери .css файл перед включением Quick CSS.");
        await _save(settings);
        if (_service is not null) Show(await _service.ConfigureAsync(settings, _lifetime.Token));
    }

    private async Task GuardAsync(Func<Task> action)
    {
        if (!CanEdit()) return;
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "Quick CSS: " + ex.Message; }
        finally { IsBusy = false; }
    }
    private void OnChanged(object? sender, QuickCssResult result)
    {
        if (Dispatcher.UIThread.CheckAccess()) Show(result);
        else Dispatcher.UIThread.Post(() => { if (!_disposed) Show(result); });
    }
    private void Show(QuickCssResult result)
    {
        if (_disposed) return;
        ThemeHomeLayout = result.HomeLayout;
        _themeAppearance = result.Appearance;
        Appearance.Refresh(_appearance, _themeAppearance);
        Status = result.Message;
        Diagnostics = string.Join(Environment.NewLine, result.Diagnostics.Take(12).Select(x => $"Строка {x.Line}: {x.Message}"));
        if (result.Diagnostics.Count > 12) Diagnostics += $"\nЕщё ошибок: {result.Diagnostics.Count - 12}.";
    }
    private bool CanEdit() => !_disposed && !IsBusy && _canEdit();
    partial void OnDiagnosticsChanged(string value) => OnPropertyChanged(nameof(HasDiagnostics));
    partial void OnFilePathChanged(string value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    public void RefreshCommands()
    {
        ApplyCommand?.NotifyCanExecuteChanged(); ReloadCommand?.NotifyCanExecuteChanged();
        BrowseCommand?.NotifyCanExecuteChanged(); OpenCommand?.NotifyCanExecuteChanged(); OpenExternalCommand?.NotifyCanExecuteChanged();
        CreateExampleCommand?.NotifyCanExecuteChanged();
        Appearance?.RefreshCommands();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_service is not null) { _service.Changed -= OnChanged; _service.Dispose(); }
        _lifetime.Dispose();
    }
}
