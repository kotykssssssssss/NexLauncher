using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Services.QuickCss;

namespace NexLauncher.ViewModels;

public sealed partial class AppearanceFieldViewModel : ObservableObject
{
    public AppearanceOption Option { get; }
    public string Label => Option.Label;
    public string Token => Option.Token;
    public string Hint => Option.Hint;
    public bool IsColor => Option.IsColor;
    public bool IsNumber => !Option.IsColor;
    public double Minimum => Option.Minimum;
    public double Maximum => Option.Maximum;
    [ObservableProperty] private string _value;
    public bool IsValid => QuickCssAppearance.TryNormalize(Option, Value, out _);
    public IBrush Preview => IsColor && CssValueParser.TryColor(Value, out var c)
        ? new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)) : Brushes.Transparent;
    public double Number
    {
        get => CssValueParser.TryNumber(Value, Minimum, Maximum, out var n) ? n : Minimum;
        set => Value = Math.Round(value).ToString(CultureInfo.InvariantCulture) + "px";
    }
    public AppearanceFieldViewModel(AppearanceOption option) { Option = option; _value = option.DefaultValue; }
    partial void OnValueChanged(string value)
    { OnPropertyChanged(nameof(Preview)); OnPropertyChanged(nameof(Number)); OnPropertyChanged(nameof(IsValid)); }
}

public sealed record AppearancePreset(string Name, IReadOnlyDictionary<string, string> Values);

public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly Func<IReadOnlyDictionary<string, string>, Task> _save;
    private readonly Func<bool> _canEdit;
    private bool _refreshing;
    private long _editRevision;
    private IReadOnlyDictionary<string, string> _current = new Dictionary<string, string>();
    public ObservableCollection<AppearanceFieldViewModel> Fields { get; } = new(QuickCssAppearance.Options.Select(x => new AppearanceFieldViewModel(x)));
    public AppearancePreset[] Presets { get; } =
    [
        new("Мята · стандартный", new Dictionary<string, string>()),
        new("Сумерки", new Dictionary<string, string> { ["--nx-accent"] = "#C7ACF5", ["--nx-background"] = "#201C2C", ["--nx-sidebar"] = "#191622", ["--nx-surface"] = "#30283D", ["--nx-input"] = "#251F32", ["--nx-border"] = "#51425F", ["--nx-muted"] = "#BCB0CC" }),
        new("Океан", new Dictionary<string, string> { ["--nx-accent"] = "#7ACDF4", ["--nx-background"] = "#17212A", ["--nx-sidebar"] = "#141C24", ["--nx-surface"] = "#24323F", ["--nx-input"] = "#1C2833", ["--nx-border"] = "#3A5265", ["--nx-muted"] = "#A6BDC9" })
    ];
    [ObservableProperty] private AppearancePreset _selectedPreset;
    [ObservableProperty] private bool _hasPendingChanges;
    [ObservableProperty] private string _status = "Изменения применяются кнопкой ниже. Они также записываются в Quick CSS.";
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public IRelayCommand PresetCommand { get; }
    public IRelayCommand RevertCommand { get; }
    public bool IsEditable => _canEdit();
    public AppearanceViewModel(Func<IReadOnlyDictionary<string, string>, Task> save, Func<bool> canEdit)
    {
        _save = save; _canEdit = canEdit; _selectedPreset = Presets[0];
        foreach (var field in Fields) field.PropertyChanged += (_, e) =>
        { if (!_refreshing && e.PropertyName == nameof(AppearanceFieldViewModel.Value)) { _editRevision++; HasPendingChanges = true; } };
        ApplyCommand = new AsyncRelayCommand(SaveAsync, canEdit);
        ResetCommand = new AsyncRelayCommand(async () => { LoadFields(new Dictionary<string, string>()); _editRevision++; HasPendingChanges = true; await SaveAsync(); }, canEdit);
        PresetCommand = new RelayCommand(() => { LoadFields(SelectedPreset.Values); _editRevision++; HasPendingChanges = true; Status = "Пресет выбран. Нажми «Применить оформление»."; }, canEdit);
        RevertCommand = new RelayCommand(() => { LoadFields(_current); HasPendingChanges = false; Status = "Поля перечитаны из текущего оформления."; }, canEdit);
    }
    private async Task SaveAsync()
    {
        if (!_canEdit()) return;
        var revision = _editRevision;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (!QuickCssAppearance.TryNormalize(field.Option, field.Value, out var value))
            { Status = $"{field.Label}: " + (field.IsColor ? "укажи непрозрачный цвет, например #78D9B3." : field.Hint); return; }
            values.Add(field.Token, value);
        }
        try
        {
            await _save(values);
            if (revision != _editRevision)
            {
                HasPendingChanges = true;
                Status = "Предыдущие значения сохранены. Новые правки остались в полях и ещё не применены.";
                return;
            }
            HasPendingChanges = false;
            LoadFields(_current);
            Status = "Оформление сохранено в настройках и CSS. Точные правила CSS могут переопределять общие параметры.";
        }
        catch (Exception ex) { Status = "Оформление не сохранено: " + ex.Message; }
    }
    public void Refresh(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string>? theme)
    {
        var values = new Dictionary<string, string>(baseline, StringComparer.Ordinal);
        if (theme is not null) foreach (var item in theme) values[item.Key] = item.Value;
        _current = values;
        if (!HasPendingChanges) LoadFields(values);
    }
    private void LoadFields(IReadOnlyDictionary<string, string> values)
    {
        _refreshing = true;
        try { foreach (var field in Fields) field.Value = values.TryGetValue(field.Token, out var value) ? value : field.Option.DefaultValue; }
        finally { _refreshing = false; }
    }
    public void RefreshCommands()
    { OnPropertyChanged(nameof(IsEditable)); ApplyCommand.NotifyCanExecuteChanged(); ResetCommand.NotifyCanExecuteChanged(); PresetCommand.NotifyCanExecuteChanged(); RevertCommand.NotifyCanExecuteChanged(); }
}
