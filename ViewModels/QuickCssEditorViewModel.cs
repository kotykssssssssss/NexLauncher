using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Services.QuickCss;

namespace NexLauncher.ViewModels;

public sealed partial class QuickCssEditorViewModel : ObservableObject, IDisposable
{
    private readonly QuickCssEditorFiles _files;
    private readonly Func<string, Task> _apply;
    private readonly Func<bool> _canApply;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _analysis;
    private string _savedText = "", _revision = "";
    private bool _disposed;
    private long _generation;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _status = "Загрузка файла…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isValidating;
    public string FilePath { get; }
    public bool IsDirty => IsReady && Text != _savedText;
    public string Title => "Quick CSS" + (IsDirty ? " •" : "") + " — NexLauncher";
    public bool CanSave => !_disposed && IsReady && !IsBusy;
    public bool CanApply => CanSave && _canApply();
    public bool HasDiagnostics => Diagnostics.Count > 0;
    public string Summary => IsValidating ? "Проверка…" : Diagnostics.Count == 0 ? "Замечаний не найдено" : $"Замечаний: {Diagnostics.Count} · нажми строку ниже";
    public ObservableCollection<QuickCssDiagnostic> Diagnostics { get; } = new();
    public Task ValidationTask { get; private set; } = Task.CompletedTask;
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SaveApplyCommand { get; }
    public QuickCssEditorViewModel(string path, QuickCssEditorFiles files, Func<string, Task> apply, Func<bool>? canApply = null)
    {
        FilePath = path; _files = files; _apply = apply; _canApply = canApply ?? (() => true);
        Diagnostics.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDiagnostics));
        SaveCommand = new AsyncRelayCommand(async () => { await SaveAsync(false); }, () => CanSave);
        SaveApplyCommand = new AsyncRelayCommand(async () => { await SaveAsync(true); }, () => CanApply);
    }
    public async Task<bool> LoadAsync()
    {
        if (_disposed || IsBusy) return false;
        IsBusy = true;
        try
        {
            var file = await _files.LoadAsync(FilePath, _lifetime.Token);
            if (_disposed) return false;
            _savedText = file.Text; _revision = file.Revision; IsReady = true; Text = file.Text;
            ValidationTask = ScheduleValidation(); // Also validate an empty or unchanged file.
            Status = "Файл загружен. Изменения остаются черновиком до сохранения."; Changed(); return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Status = Error(ex); return false; }
        finally { IsBusy = false; }
    }
    public async Task<bool> SaveAsync(bool apply)
    {
        if (!CanSave || apply && !CanApply) return false;
        IsBusy = true; var text = Text; var saved = false;
        try
        {
            var file = await _files.SaveAsync(FilePath, text, _revision, _lifetime.Token);
            _savedText = text; _revision = file.Revision; saved = true; Changed();
            Status = "Файл сохранён. При включённом автообновлении тема перечитывается автоматически.";
            if (apply) { await _apply(FilePath); Status = "Файл сохранён и передан Quick CSS. Результат применения — в настройках."; }
            return true;
        }
        catch (OperationCanceledException) { Status = saved ? "Файл сохранён. Применение отменено." : "Сохранение отменено."; return false; }
        catch (Exception ex) { Status = saved ? "Файл сохранён, но применить тему не удалось. Проверь результат Quick CSS в настройках." : Error(ex); return false; }
        finally { IsBusy = false; }
    }
    partial void OnTextChanged(string value) { Changed(); ValidationTask = ScheduleValidation(); }
    private Task ScheduleValidation()
    {
        if (_disposed) return Task.CompletedTask;
        _analysis?.Cancel(); _analysis?.Dispose();
        _analysis = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _analysis.Token; var generation = ++_generation; var text = Text;
        Diagnostics.Clear(); IsValidating = true; OnPropertyChanged(nameof(Summary));
        return ValidateAsync();
        async Task ValidateAsync()
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                var document = await Task.Run(() => QuickCssParser.Parse(text), token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || token.IsCancellationRequested || generation != _generation) return;
                    foreach (var diagnostic in QuickCssStyleApplier.Inspect(document)) Diagnostics.Add(diagnostic);
                    IsValidating = false; OnPropertyChanged(nameof(Summary));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || generation != _generation) return;
                    Diagnostics.Add(new(0, "Не удалось проверить текст. Сохранённая тема не изменена."));
                    IsValidating = false; OnPropertyChanged(nameof(Summary));
                });
            }
        }
    }
    private static string Error(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        FileNotFoundException or DirectoryNotFoundException => "CSS-файл или его папка не найдены. Выбери существующий файл в настройках.",
        UnauthorizedAccessException => "Нет доступа к CSS-файлу. Проверь разрешения Windows.",
        IOException => "Не удалось прочитать или сохранить CSS. Проверь диск; черновик остаётся в редакторе.",
        _ => "Не удалось завершить действие с Quick CSS. Черновик остаётся в редакторе."
    };
    partial void OnIsBusyChanged(bool value) => Changed();
    partial void OnIsReadyChanged(bool value) => Changed();
    private void Changed()
    {
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanApply));
        SaveCommand?.NotifyCanExecuteChanged(); SaveApplyCommand?.NotifyCanExecuteChanged();
    }
    public void RefreshPermissions() => Changed();
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel(); _analysis?.Cancel(); _analysis?.Dispose(); _lifetime.Dispose();
    }
}
