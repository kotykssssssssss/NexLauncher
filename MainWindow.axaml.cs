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
    public MainWindow() : this(CreateViewModel()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PickInstancesFolderAsync = PickInstancesFolderAsync;
        if (viewModel.Accounts.Skin is { } skin) { skin.PickSkinAsync = PickSkinAsync; skin.RefreshCommands(); }
        Opened += async (_, _) =>
        {

            try { await viewModel.QuickCss.AttachAsync(new QuickCssService(this), PickCssAsync); }
            catch { viewModel.ErrorMessage = "Не удалось загрузить Quick CSS. Проверь файл в настройках."; }
            await viewModel.InitializeAsync();
        };
        Closing += (_, _) => viewModel.OnWindowClosing();
        Closed += (_, _) => viewModel.Dispose();
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

    private static MainWindowViewModel CreateViewModel()
    {
        var store = new ConfigurationStore();
        return new MainWindowViewModel(store, new MinecraftService(store.DataDirectory),
            new AccountService(store.DataDirectory));
    }
}

