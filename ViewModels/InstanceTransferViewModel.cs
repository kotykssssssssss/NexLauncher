using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services.Instances;

namespace NexLauncher.ViewModels;

public sealed partial class InstanceTransferViewModel : ObservableObject
{
    private readonly InstanceExportService _exporter;
    private readonly InstanceImportService _importer;
    private readonly Func<Func<CancellationToken, Task>, Task> _run;
    private readonly Func<IProgress<LaunchProgress>> _progress;
    private readonly Func<bool> _canEdit;
    private readonly Func<string> _root;
    private readonly Func<IReadOnlyCollection<string>> _names;
    private readonly Func<GameInstance, CancellationToken, Task> _publish;
    private GameInstance? _instance;
    private InstanceExportPlan? _exportPlan;
    private InstanceImportPreview? _importPreview;
    private string _package = "";
    [ObservableProperty] private bool _isExport;
    [ObservableProperty] private bool _localMods;
    [ObservableProperty] private bool _configs;
    [ObservableProperty] private bool _resourcePacks;
    [ObservableProperty] private bool _shaderPacks;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _summary = "Выбери package .nexpack для проверки.";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _status = "";
    public ObservableCollection<string> FileList { get; } = new();
    public Func<Task<string?>>? PickPackageAsync { get; set; }
    public Func<Task<string?>>? SavePackageAsync { get; set; }
    public bool IsImport => !IsExport;
    public bool IsEditable => _canEdit();
    public bool HasPreview => _exportPlan is not null || _importPreview is not null;
    public bool HasWarnings => Warnings.Length > 0;
    public bool HasStatus => Status.Length > 0;
    public string Title => IsExport ? "Export / Share Instance" : "Import Instance";
    public string TargetDirectory => _root();
    public IAsyncRelayCommand PreviewExportCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand ChoosePackageCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }

    public InstanceTransferViewModel(InstanceExportService exporter, InstanceImportService importer,
        Func<Func<CancellationToken, Task>, Task> run, Func<IProgress<LaunchProgress>> progress, Func<bool> canEdit,
        Func<string> root, Func<IReadOnlyCollection<string>> names, Func<GameInstance, CancellationToken, Task> publish)
    {
        _exporter = exporter; _importer = importer; _run = run; _progress = progress; _canEdit = canEdit;
        _root = root; _names = names; _publish = publish;
        PreviewExportCommand = new AsyncRelayCommand(PreviewExportAsync, () => IsEditable && IsExport && _instance is not null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => IsEditable && IsExport && _exportPlan is not null && SavePackageAsync is not null);
        ChoosePackageCommand = new AsyncRelayCommand(ChoosePackageAsync, () => IsEditable && IsImport && PickPackageAsync is not null);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => IsEditable && IsImport && _importPreview is not null &&
            !string.IsNullOrWhiteSpace(NewName) && NewName.Trim().Length <= 60 && !_names().Any(x => x.Equals(NewName.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
    public void Open(GameInstance? instance, bool export)
    {
        _instance = instance; IsExport = export; Invalidate();
        Summary = export ? instance?.Details + " · " + instance?.Name : "Выбери package .nexpack для проверки.";
        OnPropertyChanged(nameof(TargetDirectory));
    }
    private Task PreviewExportAsync() => _run(async token =>
    {
        Invalidate();
        _exportPlan = await _exporter.PlanAsync(_instance!, _instance!.GameDirectory,
            new(LocalMods, Configs, ResourcePacks, ShaderPacks), _progress(), token);
        ShowManifest(_exportPlan.Manifest); Warnings = string.Join(Environment.NewLine, _exportPlan.Notices);
        Status = "Это snapshot выбранных файлов. Если они изменятся, перед экспортом потребуется новый preview.";
        RefreshCommands();
    });
    private async Task ExportAsync()
    {
        await _run(async token =>
        {
            var target = await SavePackageAsync!(); if (target is null) return;
            await _exporter.ExportAsync(_exportPlan!, target, _progress(), token);
            Status = "Package сохранён локально. Перед передачей проверь configs и права на bundled-файлы.";
        });
    }
    private Task ChoosePackageAsync() => _run(async token =>
    {
        var path = await PickPackageAsync!(); if (path is null) return;
        Invalidate();
        _importPreview = await new InstancePackageReader().PreviewAsync(path, token); _package = path;
        ShowManifest(_importPreview.Manifest); NewName = _importPreview.Manifest.Name;
        Warnings = "Будет создан НОВЫЙ instance. Exact Modrinth версии требуют сети; Minecraft/loader скачиваются штатным installer. Java — автоматическая, абсолютный путь автора не переносится." +
            (_importPreview.Manifest.OmittedFiles.Count > 0 ? "\nАвтор исключил файлы: сборка может быть неполной." : "") +
            (_importPreview.Manifest.Files.Count > 0 ? "\nВ архиве есть local-файлы. Импортируй только доверенные сборки: моды выполняются при запуске Minecraft." : "");
        Status = "Archive paths, размеры и SHA256 bundled-файлов проверены. Совместимость managed-модов будет повторно проверена через API до установки.";
        RefreshCommands();
    });
    private Task ImportAsync() => _run(async token =>
    {
        await _importer.ImportAsync(_package, _importPreview!, NewName, _root(), _names(), _publish, _progress(), token);
        Status = "Instance установлен и зарегистрирован. Аккаунты и существующие сборки не изменены.";
        _importPreview = null; OnPropertyChanged(nameof(HasPreview)); RefreshCommands();
    });
    private void ShowManifest(InstanceManifest manifest)
    {
        var local = manifest.Files.Count(x => x.Path.StartsWith("mods/", StringComparison.Ordinal));
        var omittedLocal = manifest.OmittedFiles.Count(x => x.StartsWith("mods/", StringComparison.Ordinal));
        var estimated = manifest.Files.Sum(x => x.Size);
        Summary = $"{manifest.Name}\nMinecraft {manifest.MinecraftVersion} · {manifest.Loader} {manifest.LoaderVersion}\n" +
            $"Manifest v{manifest.ManifestVersion} · RAM {manifest.MemoryMb} МБ · Java автоматически\n" +
            $"Mods: {manifest.Mods.Count} managed + {local} local/unknown included + {omittedLocal} local omitted\n" +
            $"Configs: {manifest.Files.Count(x => x.Path.StartsWith("config/") || x.Path.StartsWith("defaultconfigs/"))} · " +
            $"Resource packs: {manifest.Files.Count(x => x.Path.StartsWith("resourcepacks/"))} файлов · Shaders: {manifest.Files.Count(x => x.Path.StartsWith("shaderpacks/"))} файлов\n" +
            $"Bundled: {estimated / 1024d / 1024:F2} МиБ до ZIP-сжатия + manifest; managed JAR скачиваются отдельно ({manifest.Mods.Sum(x => x.Size) / 1024d / 1024:F2} МиБ).";
        FileList.Clear();
        foreach (var mod in manifest.Mods) FileList.Add($"Modrinth: {mod.ProjectId} / {mod.VersionId} — {mod.Filename}");
        foreach (var file in manifest.Files) FileList.Add($"Включён: {file.Path} ({file.Size:N0} bytes)");
        foreach (var path in manifest.OmittedFiles) FileList.Add("Исключён: " + path);
        OnPropertyChanged(nameof(HasPreview));
    }
    private void Invalidate()
    {
        _exportPlan = null; _importPreview = null; _package = ""; FileList.Clear(); Warnings = ""; Status = "";
        Summary = IsExport ? "Подготовь новый preview для выбранных параметров." : "Выбери package .nexpack для проверки.";
        OnPropertyChanged(nameof(HasPreview)); RefreshCommands();
    }
    partial void OnIsExportChanged(bool value) { OnPropertyChanged(nameof(IsImport)); OnPropertyChanged(nameof(Title)); }
    partial void OnLocalModsChanged(bool value) => Invalidate();
    partial void OnConfigsChanged(bool value) => Invalidate();
    partial void OnResourcePacksChanged(bool value) => Invalidate();
    partial void OnShaderPacksChanged(bool value) => Invalidate();
    partial void OnNewNameChanged(string value) => RefreshCommands();
    partial void OnWarningsChanged(string value) => OnPropertyChanged(nameof(HasWarnings));
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    public void RefreshCommands()
    {
        PreviewExportCommand?.NotifyCanExecuteChanged(); ExportCommand?.NotifyCanExecuteChanged();
        ChoosePackageCommand?.NotifyCanExecuteChanged(); ImportCommand?.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsEditable));
    }
}
