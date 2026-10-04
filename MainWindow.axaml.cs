using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NexLauncher.Services;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;

namespace NexLauncher;

public partial class MainWindow : Window
{
    private Views.QuickCssEditorWindow? _cssEditor;
    public MainWindow() : this(CreateViewModel()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PickInstancesFolderAsync = PickInstancesFolderAsync;
        viewModel.QuickCss.OpenEditorAsync = async path =>
        {
            if (_cssEditor is not null) { _cssEditor.Activate(); return; }
            var editorModel = new QuickCssEditorViewModel(path, new QuickCssEditorFiles(), viewModel.QuickCss.ApplyEditorFileAsync,
                () => viewModel.IsEditable && !viewModel.QuickCss.IsBusy);
            var editor = _cssEditor = new Views.QuickCssEditorWindow(editorModel);
            editor.Closed += (_, _) => _cssEditor = null;
            editor.Show(this); await editor.LoadAsync();
        };
        viewModel.QuickCss.RefreshCommands();
        void PermissionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => _cssEditor?.RefreshPermissions();
        viewModel.PropertyChanged += PermissionsChanged;
        viewModel.QuickCss.PropertyChanged += PermissionsChanged;
        if (viewModel.Accounts.Skin is { } skin) { skin.PickSkinAsync = PickSkinAsync; skin.RefreshCommands(); }
        viewModel.SkinCatalog.PickImportAsync = PickSkinAsync;
        viewModel.SkinCatalog.PickExportAsync = SaveSkinAsync;
        viewModel.SkinCatalog.RefreshCommands();
        Opened += async (_, _) =>
        {

            try { await viewModel.QuickCss.AttachAsync(new QuickCssService(this), PickCssAsync); }
            catch { viewModel.ErrorMessage = "Не удалось загрузить Quick CSS. Проверь файл в настройках."; }
            await viewModel.InitializeAsync();
        };
        Closing += (_, args) =>
        {
            if (_cssEditor?.HasPendingChanges == true) { args.Cancel = true; _cssEditor.Activate(); _cssEditor.RequestClose(); return; }
            viewModel.OnWindowClosing();
        };
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= PermissionsChanged; viewModel.QuickCss.PropertyChanged -= PermissionsChanged;
            _cssEditor?.Close(); viewModel.Dispose();
        };
    }

    private async Task<string?> PickCssAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбрать тему Quick CSS",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Quick CSS") { Patterns = ["*.css"] }]
        });
        try { return files.FirstOrDefault()?.TryGetLocalPath(); }
        finally { foreach (var file in files) file.Dispose(); }
    }

    private async Task<string?> PickSkinAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбрать Minecraft-скин", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PNG skin") { Patterns = ["*.png"] }]
        });
        try { return files.FirstOrDefault()?.TryGetLocalPath(); }
        finally { foreach (var file in files) file.Dispose(); }
    }

    private async Task<string?> PickInstancesFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка новых сборок NexLauncher", AllowMultiple = false });
        try { return folders.FirstOrDefault()?.TryGetLocalPath(); }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }

    private async Task<string?> SaveSkinAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить Minecraft-скин", SuggestedFileName = suggestedName, DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PNG skin") { Patterns = ["*.png"] }]
        });
        try { return file?.TryGetLocalPath(); }
        finally { file?.Dispose(); }
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var store = new ConfigurationStore();
        return new MainWindowViewModel(store, new MinecraftService(store.DataDirectory),
            new AccountService(store.DataDirectory));
    }
}

