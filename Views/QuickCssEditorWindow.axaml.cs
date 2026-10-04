using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Rendering;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;

namespace NexLauncher.Views;

public partial class QuickCssEditorWindow : Window
{
    private readonly QuickCssEditorViewModel? _viewModel;
    private QuickCssEditorViewModel _model => _viewModel ?? throw new InvalidOperationException("Редактору Quick CSS нужна ViewModel.");
    public QuickCssErrorRenderer Errors { get; } = new();
    private bool _syncing, _allowClose, _reload;
    public bool HasPendingChanges => _viewModel is { } model && (model.IsDirty || model.IsBusy);
    // Required by the XAML compiler/designer. Runtime windows receive their model explicitly.
    public QuickCssEditorWindow() => InitializeComponent();
    public QuickCssEditorWindow(QuickCssEditorViewModel model) : this()
    {
        _viewModel = model; DataContext = model;
        Editor.SyntaxHighlighting = QuickCssHighlighting.Create();
        Editor.Options.EnableHyperlinks = false; Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.ConvertTabsToSpaces = true; Editor.Options.IndentationSize = 4;
        Editor.TextArea.TextView.BackgroundRenderers.Add(Errors);
        Editor.TextChanged += (_, _) => { if (!_syncing) _model.Text = Editor.Text; };
        model.PropertyChanged += ModelChanged;
        model.Diagnostics.CollectionChanged += DiagnosticsChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) => CaretInfo.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";
        Closing += OnClosing;
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; model.Diagnostics.CollectionChanged -= DiagnosticsChanged; model.Dispose(); Editor.TextArea.TextView.BackgroundRenderers.Remove(Errors); };
        AddHandler(KeyDownEvent, OnEditorKey, RoutingStrategies.Tunnel);
    }
    public Task<bool> LoadAsync() => _model.LoadAsync();
    public void RefreshPermissions() => _model.RefreshPermissions();
    public void RequestClose() => Close();
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickCssEditorViewModel.Text) || Editor.Text == _model.Text) return;
        _syncing = true;
        try { Editor.Text = _model.Text; }
        finally { _syncing = false; }
    }
    private void DiagnosticsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    { Errors.SetDiagnostics(_model.Diagnostics); Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection); }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (_model.IsBusy) { e.Cancel = true; _model.Status = "Дождись завершения чтения или сохранения файла."; return; }
        if (!_model.IsDirty) return;
        e.Cancel = true; Ask(reload: false);
    }
    private void Ask(bool reload)
    {
        _reload = reload; ConfirmText.Text = reload ? "Перечитать файл и заменить несохранённый черновик?" : "В редакторе есть несохранённые изменения.";
        DiscardButton.Content = reload ? "Перечитать без сохранения" : "Закрыть без сохранения"; ConfirmPanel.IsVisible = true;
    }
    private async void ReloadClicked(object? sender, RoutedEventArgs e)
    { if (_model.IsDirty) Ask(reload: true); else await LoadAsync(); }
    private async void SaveAndContinueClicked(object? sender, RoutedEventArgs e)
    { if (await _model.SaveAsync(false)) await ContinueAsync(); }
    private async void DiscardClicked(object? sender, RoutedEventArgs e) => await ContinueAsync();
    private void KeepEditingClicked(object? sender, RoutedEventArgs e) { ConfirmPanel.IsVisible = false; Editor.Focus(); }
    private async Task ContinueAsync()
    { ConfirmPanel.IsVisible = false; if (_reload) await LoadAsync(); else { _allowClose = true; Close(); } }
    private void ProblemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Problems.SelectedItem is not QuickCssDiagnostic item || item.Line <= 0 || Editor.Document is null) return;
        var line = Math.Min(item.Line, Editor.Document.LineCount);
        Editor.CaretOffset = Editor.Document.GetLineByNumber(line).Offset; Editor.ScrollToLine(line); Editor.Focus();
    }
    private async void OnEditorKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        await _model.SaveAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }
}
