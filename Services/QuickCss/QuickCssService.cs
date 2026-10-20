using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using NexLauncher.Models;

namespace NexLauncher.Services.QuickCss;

/// <summary>Owns one window's CSS overlay and watches the chosen file, including atomic editor saves.</summary>
public sealed class QuickCssService : IQuickCssService
{
    private readonly object _gate = new();
    private readonly QuickCssStyleApplier _applier;
    private QuickCssSettings _settings = new();
    private QuickCssResult _lastResult = new(false, 0, Array.Empty<QuickCssDiagnostic>(), "Quick CSS выключен.");
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadCancellation;
    private long _generation;
    private bool _disposed;
    private string? _watcherWarning;

    public QuickCssService(Window window) => _applier = new QuickCssStyleApplier(window);
    public event EventHandler<QuickCssResult>? Changed;

    public Task<QuickCssResult> ConfigureAsync(QuickCssSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _settings = new QuickCssSettings { Enabled = settings.Enabled, FilePath = settings.FilePath?.Trim() ?? "", AutoReload = settings.AutoReload,
                Appearance = QuickCssAppearance.ValidateAndCopy(settings.Appearance) };
            ReplaceWatcher();
            return StartReloadLocked(cancellationToken, debounce: false);
        }
    }

    public Task<QuickCssResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A directory that did not exist during configuration may have appeared since then.
            if (_watcher is null && _settings.Enabled && _settings.AutoReload) ReplaceWatcher();
            return StartReloadLocked(cancellationToken, debounce: false);
        }
    }

    private Task<QuickCssResult> StartReloadLocked(CancellationToken cancellationToken, bool debounce)
    {
        _reloadCancellation?.Cancel();
        _reloadCancellation?.Dispose();
        _reloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _reloadCancellation.Token;
        var generation = ++_generation;
        var settings = new QuickCssSettings { Enabled = _settings.Enabled, FilePath = _settings.FilePath, AutoReload = _settings.AutoReload,
            Appearance = new(_settings.Appearance) };
        return RunReloadAsync(settings, generation, token, debounce);
    }

    private async Task<QuickCssResult> RunReloadAsync(QuickCssSettings settings, long generation, CancellationToken token, bool debounce)
    {
        if (debounce) await Task.Delay(300, token).ConfigureAwait(false);
        try
        {
            if (!settings.Enabled)
                return await PublishAsync(generation, token, () =>
                {
                    _applier.Clear();
                    var rules = QuickCssAppearance.Rules(settings.Appearance);
                    if (rules.Count > 0)
                    {
                        using var baseline = new PreparedQuickCss(new(rules, []), new Dictionary<string, Avalonia.Media.Imaging.Bitmap>(), []);
                        _applier.Apply(baseline);
                    }
                    return new(false, 0, Array.Empty<QuickCssDiagnostic>(), "Quick CSS выключен. Оформление из настроек активно.");
                }).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(settings.FilePath)) throw new InvalidDataException("Выбери файл .css с темой.");
            var path = Path.GetFullPath(settings.FilePath);
            using var prepared = await Task.Run(async () =>
            {
                var bytes = await QuickCssImageLoader.ReadBoundedAsync(path, 128 * 1024, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var source = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff');
                var document = QuickCssParser.Parse(source);
                document = document with { Rules = QuickCssAppearance.Rules(settings.Appearance).Concat(document.Rules).ToArray() };
                token.ThrowIfCancellationRequested();
                return await QuickCssImageLoader.PrepareAsync(document, path, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return await PublishAsync(generation, token, () =>
            {
                var result = _applier.Apply(prepared);
                if (_watcherWarning is null) return result;
                var diagnostics = new System.Collections.Generic.List<QuickCssDiagnostic>(result.Diagnostics) { new(0, _watcherWarning) };
                return result with { Diagnostics = diagnostics, Message = result.Message + " Автообновление недоступно." };
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return await PublishAsync(generation, token, () => new(_applier.IsApplied, _lastResult.RuleCount,
                new[] { new QuickCssDiagnostic(0, ex.Message) },
                _applier.IsApplied ? "Не удалось прочитать тему. Последний применённый стиль сохранён." : "Не удалось применить Quick CSS.", _lastResult.HomeLayout, _lastResult.Appearance)).ConfigureAwait(false);
        }
    }

    private async Task<QuickCssResult> PublishAsync(long generation, CancellationToken token, Func<QuickCssResult> createResult)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            QuickCssResult result;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation) throw new OperationCanceledException(token);
                result = createResult();
                _lastResult = result;
                Changed?.Invoke(this, result);
            }
            return result;
        });
    }

    private void ReplaceWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watcherWarning = null;
        if (!_settings.Enabled || !_settings.AutoReload || string.IsNullOrWhiteSpace(_settings.FilePath)) return;
        try
        {
            var fullPath = Path.GetFullPath(_settings.FilePath);
            var directory = Path.GetDirectoryName(fullPath)!;
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileChanged;
            watcher.Error += OnWatcherError;
            _watcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
            _watcherWarning = "Не удалось наблюдать за файлом темы: " + ex.Message;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => QueueReload(sender);
    private void OnWatcherError(object sender, ErrorEventArgs args) => QueueReload(sender);

    private void QueueReload(object sender)
    {
        Task<QuickCssResult> task;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher) || !_settings.Enabled || !_settings.AutoReload) return;
            task = StartReloadLocked(CancellationToken.None, debounce: true);
        }
        _ = ObserveWatcherReloadAsync(task);
    }

    private static async Task ObserveWatcherReloadAsync(Task<QuickCssResult> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            _watcher?.Dispose();
            _watcher = null;
            _reloadCancellation?.Cancel();
            _reloadCancellation?.Dispose();
            _reloadCancellation = null;
        }
        if (Dispatcher.UIThread.CheckAccess()) _applier.Dispose();
        else Dispatcher.UIThread.Post(_applier.Dispose);
    }
}
