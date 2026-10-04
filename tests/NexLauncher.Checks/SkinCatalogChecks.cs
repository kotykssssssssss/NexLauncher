using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.QuickCss;
using NexLauncher.Services.Skins;
using NexLauncher.Services.Skins.Catalog;
using NexLauncher.Services.Storage;
using NexLauncher.ViewModels;
using NexLauncher.Views;
using SkiaSharp;

internal static class SkinCatalogChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "skin-catalog space Мир"); Directory.CreateDirectory(directory);
        var validator = new SkinValidator(); var library = new SkinLibrary(Path.Combine(directory, "data"), validator);
        var empty = await library.SearchAsync("", 0, 24, default);
        check(empty.Total == 0 && !Directory.Exists(library.DirectoryPath), "skin library first read creates no data or invented remote results");
        var invalidRootSource = new SkinLibrary("invalid\0root", validator);
        await Reject<ArgumentException>(() => invalidRootSource.SearchAsync("", 0, 24, default), check,
            "optional source validates storage on use rather than crashing launcher construction");
        var path = Path.Combine(directory, "Adventure Скин.png"); var bytes = Png(); await File.WriteAllBytesAsync(path, bytes);
        var item = await library.ImportAsync(path, SkinModel.Classic, default);
        var duplicate = await library.ImportAsync(path, SkinModel.Classic, default);
        check(item.Id == duplicate.Id && (await library.SearchAsync("", 0, 24, default)).Total == 1, "identical PNG/model imports reuse a verified library entry");
        await library.SetModelAsync(item, SkinModel.Slim, default);
        await File.WriteAllTextAsync(path, "source can disappear");
        var fresh = new SkinLibrary(Path.Combine(directory, "data"), validator);
        var found = await fresh.SearchAsync("сКиН", 0, 24, default); var texture = await fresh.ReadAsync(found.Items.Single(), default);
        check(texture.Model == SkinModel.Slim && texture.Image.Png.Length > 50 && found.Items[0].Name == "Adventure Скин", "Unicode search and imported PNG/model survive restart without original source path");
        check(!(await File.ReadAllTextAsync(Path.Combine(library.DirectoryPath, item.Id, "skin.json"))).Contains(directory), "library metadata contains no original absolute path, account or credentials");
        check((await fresh.SearchAsync("missing", 0, 24, default)).Total == 0, "local search distinguishes empty results");
        await Reject<SkinException>(() => fresh.ReadAsync(item with { Id = "../outside" }, default), check, "skin catalog rejects traversal identifiers");
        await Reject<SkinException>(() => fresh.ReadAsync(item with { SourceId = "foreign" }, default), check, "source identity cannot address another provider's storage");
        await Reject<SkinException>(() => fresh.SearchAsync(new string('a', 101), 0, 24, default), check, "library search bounds query length");
        await Reject<SkinException>(() => fresh.SearchAsync("", -1, 24, default), check, "library rejects invalid pagination");
        await Reject<OperationCanceledException>(() => fresh.SearchAsync("", 0, 24, new(true)), check, "catalog search honors cancellation");
        await Reject<OperationCanceledException>(() => fresh.ImportAsync("unused", SkinModel.Classic, new(true)), check, "cancelled import reads and commits nothing");
        foreach (var bad in new[] { new byte[] { 0, 1, 2 }, Png(128), new byte[SkinValidator.MaxBytes + 1] })
        {
            await File.WriteAllBytesAsync(path, bad);
            await Reject<SkinException>(() => fresh.ImportAsync(path, SkinModel.Classic, default), check, "skin catalog import rejects invalid, wrong-size or oversized PNG");
        }
        await File.WriteAllBytesAsync(path, Png(64, 32));
        var legacy = await fresh.ImportAsync(path, SkinModel.Slim, default);
        check(legacy.Legacy && legacy.Model == SkinModel.Classic, "legacy import reliably maps to Classic without guessing a modern PNG's arm model");
        await Reject<SkinException>(() => fresh.SetModelAsync(legacy, SkinModel.Slim, default), check, "legacy entry cannot persist Slim metadata");
        await Reject<SkinException>(() => fresh.SetModelAsync(item, (SkinModel)42, default), check, "library rejects invalid model values");
        var export = new SkinPngExport(validator); var exported = Path.Combine(directory, "Export Мир.png");
        await export.SaveAsync(texture.Image, exported, default);
        check(validator.Validate(await File.ReadAllBytesAsync(exported)).Png.SequenceEqual(texture.Image.Png), "export writes a validated actual PNG to user-selected Unicode path");
        var before = await File.ReadAllBytesAsync(exported);
        await Reject<OperationCanceledException>(() => export.SaveAsync(texture.Image, exported, new(true)), check, "cancelled export does not overwrite existing destination");
        check((await File.ReadAllBytesAsync(exported)).SequenceEqual(before) && Directory.GetFiles(directory, "*.tmp").Length == 0, "export cancellation retains existing PNG and leaves no partial download");
        await Reject<SkinException>(() => export.SaveAsync(texture.Image, "relative.png", default), check, "export requires an explicitly selected full PNG path");
        var pngPath = Path.Combine(library.DirectoryPath, item.Id, "skin.png"); var original = await File.ReadAllBytesAsync(pngPath);
        await File.WriteAllBytesAsync(pngPath, [0, 1, 2]);
        await Reject<SkinException>(() => fresh.ReadAsync(item, default), check, "corrupted library PNG cannot reach preview or Skin Manager");
        check((await File.ReadAllBytesAsync(pngPath)).SequenceEqual(new byte[] { 0, 1, 2 }), "corrupted PNG original remains recoverable");
        await File.WriteAllBytesAsync(pngPath, original);
        var metadataPath = Path.Combine(library.DirectoryPath, item.Id, "skin.json"); var metadata = await File.ReadAllBytesAsync(metadataPath);
        await File.WriteAllTextAsync(metadataPath, "{broken");
        check((await fresh.SearchAsync("", 0, 24, default)).Notice.Contains("Оригинальные"), "damaged metadata is diagnosed without replacing it with an empty library");
        check(await File.ReadAllTextAsync(metadataPath) == "{broken", "metadata parsing preserves the broken source file");
        await File.WriteAllBytesAsync(metadataPath, metadata);
        var stored = JsonSerializer.Deserialize<StoredLibrarySkin>(metadata, AtomicJson.Options)!;
        await AtomicJson.WriteAsync(metadataPath, stored with { FormatVersion = 2 }, default);
        await Reject<SkinException>(() => fresh.SetModelAsync(item, SkinModel.Classic, default), check, "future metadata format cannot be silently overwritten by a model change");
        await File.WriteAllBytesAsync(metadataPath, metadata);
        var manual = Path.Combine(library.DirectoryPath, item.Id, "manual.png"); await File.WriteAllTextAsync(manual, "untouched");
        await fresh.RemoveAsync(item, default);
        check(File.Exists(manual) && !File.Exists(metadataPath) && !File.Exists(pngPath), "library removal only deletes managed matching PNG and metadata, preserving unknown files");
        check((await fresh.SearchAsync("", 0, 24, default)).Notice == "", "retired entry with preserved manual files is not misreported as corrupt metadata");
        await File.WriteAllBytesAsync(Path.Combine(library.DirectoryPath, legacy.Id, "skin.png"), [4, 5, 6]);
        await fresh.RemoveAsync(legacy, default);
        check(File.Exists(Path.Combine(library.DirectoryPath, legacy.Id, "skin.png")), "removal preserves externally replaced PNG instead of deleting unverified user content");
        var currentTime = DateTimeOffset.UtcNow; var cache = new SkinCatalogPreviewCache(() => currentTime);
        var a = await cache.GetAsync(texture, default); var b = await cache.GetAsync(texture, default);
        check(a.SequenceEqual(b) && !ReferenceEquals(a, b) && cache.Count == 1, "thumbnail cache hit returns independent bytes without repeated stored files");
        using (var bitmap = SKBitmap.Decode(a)) check(bitmap.Width == 192 && bitmap.Height == 192, "card previews reuse the existing cuboid renderer with bounded thumbnail dimensions");
        currentTime = currentTime.AddMinutes(6); await cache.GetAsync(texture, default);
        check(cache.Count == 1, "expired thumbnail is replaced rather than accumulating stale entries");
        for (var i = 0; i < 35; i++) await cache.GetAsync(new(validator.Validate(Png(tint: (byte)i)), SkinModel.Classic), default);
        check(cache.Count == SkinCatalogPreviewCache.Capacity, "thumbnail cache is bounded and never evicts user skin files");
        await Reject<OperationCanceledException>(() => cache.GetAsync(texture, new(true)), check, "preview generation honors cancellation");
        await Ui(directory, bytes, validator, check);
        await SourceFailures(directory, validator, check);
    }

    private static async Task Ui(string root, byte[] png, SkinValidator validator, Action<bool, string> check)
    {
        var accounts = new FakeAccounts(); await accounts.CreateLocalAccountAsync("Library_Player", default);
        var local = new OfflineSkinService(new SkinStorage(Path.Combine(root, "account-skins"), validator));
        var store = new ConfigurationStore(Path.Combine(root, "shell"));
        var shell = new MainWindowViewModel(store, new FakeMinecraft(), accounts, skins: local);
        await shell.InitializeAsync();
        var window = new MainWindow(shell) { Width = 1060, Height = 760 }; window.Show();
        shell.NavigateCommand.Execute("skins");
        var model = shell.SkinCatalog; await Idle(model);
        check(shell.IsSkinsPage && !shell.IsStandardPage && model.IsEmpty && model.Source.Id == "local", "sidebar navigation opens an explicit local skin catalogue with an honest empty state");
        var path = Path.Combine(root, "Imported Skin.png"); await File.WriteAllBytesAsync(path, png);
        model.PickImportAsync = () => Task.FromResult<string?>(path);
        await model.ImportCommand.ExecuteAsync(null); await Layout();
        check(model.IsDetails && model.PreviewImage is not null && model.Results.Count == 1 && model.Results[0].Thumbnail is not null,
            "native import opens actual details and rendered grid thumbnail without applying to account");
        check((await local.GetAsync(accounts.Accounts[0], default)).Image is null, "catalog import does not mutate Local account skin");
        model.SelectedModel = SkinManagerViewModel.Models[1]; await model.SaveModelCommand.ExecuteAsync(null);
        check(model.SelectedItem!.Model == SkinModel.Slim && model.PreviewModel == SkinModel.Slim && !model.HasModelChanges, "details model selection persists into shared library metadata and preview");
        var exportPath = Path.Combine(root, "UI exported.png"); model.PickExportAsync = _ => Task.FromResult<string?>(exportPath);
        await model.DownloadCommand.ExecuteAsync(null);
        check(File.Exists(exportPath) && validator.Validate(await File.ReadAllBytesAsync(exportPath)).Png.SequenceEqual(model.PreviewImage!.Png), "details Save PNG exports the exact selected entry through native picker callback");
        var legacyPath = Path.Combine(root, "Legacy Skin.png"); await File.WriteAllBytesAsync(legacyPath, Png(64, 32));
        model.PickImportAsync = () => Task.FromResult<string?>(legacyPath);
        await model.ImportCommand.ExecuteAsync(null); await Layout();
        check(model.SelectedItem is { Legacy: true, Model: SkinModel.Classic } && model.AvailableModels.Count == 1 && model.SelectedModel?.Model == SkinModel.Classic,
            "details switches from Slim to legacy Classic without a null selection or false model option");
        model.SelectedModel = SkinManagerViewModel.Models[1];
        check(!model.UseCommand.CanExecute(null) && !model.SaveModelCommand.CanExecute(null), "legacy Slim cannot be handed to account application even through programmatic selection");
        model.SelectedModel = SkinManagerViewModel.Models[0]; model.RemoveCommand.Execute(null); await model.ConfirmRemoveCommand.ExecuteAsync(null);
        model.BackCommand.Execute(null);
        var uiLibrary = new SkinLibrary(store.DataDirectory, validator);
        for (var i = 0; i < 6; i++)
        {
            var additional = Path.Combine(root, "Skin " + i + " — длинное имя для проверки переносов.png");
            await File.WriteAllBytesAsync(additional, Png(tint: (byte)(20 + i)));
            await uiLibrary.ImportAsync(additional, SkinModel.Classic, default);
        }
        await model.SearchCommand.ExecuteAsync(null); await Layout();
        var view = window.GetVisualDescendants().OfType<SkinCatalogView>().Single();
        foreach (var width in new[] { 900, 1060, 1920 })
        {
            window.Width = width; await Layout();
            var cards = view.FindControl<ItemsControl>("SkinCards")!; var scroll = view.FindControl<ScrollViewer>("CardsScroll")!;
            check(view.Bounds.Width >= (window.ClientSize.Width - 196) * .9 && scroll.Extent.Width <= scroll.Viewport.Width + 1 && cards.Bounds.Width > 500,
                "skin catalogue uses full main content and wraps cards without horizontal overflow at " + width);
            var buttons = view.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("qc-skin-card")).ToArray();
            check(buttons.Length == 7 && buttons.All(x => x.Bounds.Width <= 188), "multiple skins and long names retain bounded responsive card widths at " + width);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root, "skin-catalog-" + width + ".png"), PngBitmapEncoderOptions.Default);
        }
        model.Query = "Imported Skin"; await Idle(model);
        model.DetailsCommand.Execute(model.Results[0]); await Idle(model); await Layout();
        foreach (var width in new[] { 900, 1920 })
        {
            window.Width = width; await Layout();
            check(view.FindControl<StackPanel>("DetailsActions")!.Bounds.Width >= 280 && view.FindControl<Grid>("DetailsLayout")!.Bounds.Width <= view.Bounds.Width,
                "full skin details keeps preview and actions usable without overlap at " + width);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root, "skin-details-" + width + ".png"), PngBitmapEncoderOptions.Default);
        }
        model.BackCommand.Execute(null);
        check(model.Query == "Imported Skin" && model.Results.Count == 1, "details Back preserves library query and result page");
        model.DetailsCommand.Execute(model.Results[0]); await Idle(model); await Layout();
        var css = Path.Combine(root, "catalog.css");
        await File.WriteAllTextAsync(css, ".skin-card { background: #302040; border-radius: 16px; } #skin-details { background: #203040; }");
        using var theme = new QuickCssService(window); var report = await theme.ConfigureAsync(new() { Enabled = true, FilePath = css, AutoReload = false }); await Layout();
        check(report.IsApplied && view.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("qc-skin-details")).Background is ISolidColorBrush brush && brush.Color == Color.Parse("#203040"),
            "skin details supports stable public Quick CSS selectors");
        model.BackCommand.Execute(null); await Layout();
        check(view.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("qc-skin-card") && x.IsEffectivelyVisible).Background is ISolidColorBrush cardBrush && cardBrush.Color == Color.Parse("#302040"),
            "skin card Quick CSS overrides preserve the existing theme engine");
        model.DetailsCommand.Execute(model.Results[0]); await Idle(model);
        var active = accounts.ActiveAccountId;
        await model.UseCommand.ExecuteAsync(null); await Layout();
        check(shell.IsSettingsPage && shell.Accounts.Skin is { IsOpen: true, HasChanges: true, PreviewModel: SkinModel.Slim } && accounts.ActiveAccountId == active,
            "Use hands selected PNG/model to the existing Skin Manager as a draft without changing active login");
        check((await local.GetAsync(accounts.Accounts[0], default)).Image is null, "handoff never applies a skin until the existing Apply button is pressed");
        await shell.Accounts.Skin!.ApplyCommand.ExecuteAsync(null);
        check((await local.GetAsync(accounts.Accounts[0], default)).Model == SkinModel.Slim, "existing account Apply pipeline saves the handed-off skin and explicit model");
        shell.NavigateCommand.Execute("skins"); await Idle(model);
        model.RemoveCommand.Execute(null);
        check(model.ShowRemoveConfirmation && model.ConfirmRemoveCommand.CanExecute(null), "library deletion requires an explicit confirmation");
        await model.ConfirmRemoveCommand.ExecuteAsync(null);
        check(model.IsEmpty && (await local.GetAsync(accounts.Accounts[0], default)).Image is not null && File.Exists(path), "deleting catalogue entry preserves applied account skin and original imported PNG");
        window.Close();
    }

    private static async Task SourceFailures(string root, SkinValidator validator, Action<bool, string> check)
    {
        var source = new DeferredSource(); var library = new SkinLibrary(Path.Combine(root, "unused"), validator);
        using var model = new SkinCatalogViewModel(source, library, new(), new(validator), () => true, (_, _) => Task.CompletedTask, _ => { });
        model.Open([], null); await Until(() => model.IsSearching);
        model.CancelCommand.Execute(null); source.Result.SetCanceled(); await Idle(model);
        check(!model.IsSearching && !model.HasError, "cancelled provider request leaves UI responsive without a fabricated result or network error");
        source.Error = new IOException("response invalid"); await model.SearchCommand.ExecuteAsync(null);
        check(model.HasError && !model.IsSearching && !model.UseCommand.CanExecute(null), "provider failure has a recoverable error and cannot enable account application");
        source.Error = null; source.Result = new(TaskCreationOptions.RunContinuationsAsynchronously); source.Result.SetResult(new([], 0));
        await model.SearchCommand.ExecuteAsync(null);
        check(model.IsEmpty && !model.HasError, "retry after provider error restores a genuine empty-result state");
        using var manager = new SkinManagerViewModel(new NeverSkinService(), validator, action => action(default), () => true);
        var online = new LauncherAccount("licensed", "Licensed", "11111111111111111111111111111111"); manager.SetAccount(online);
        await manager.PrepareDraftAsync(validator.Validate(Png()), SkinModel.Slim);
        check(manager.HasChanges && manager.IsOpen && !manager.IsLocal && manager.ApplyLabel.Contains("Minecraft"), "catalog draft preparation for Microsoft never calls auth/API and retains explicit online Apply action");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayed = new SkinManagerViewModel(new NeverSkinService(), validator, async action => { await release.Task; await action(default); }, () => true);
        delayed.SetAccount(online);
        var prepare = delayed.PrepareDraftAsync(validator.Validate(Png()), SkinModel.Classic);
        delayed.SetAccount(LocalAccountIdentity.CreateProfile("Other_Player")); release.SetResult();
        check(!await prepare && !delayed.HasChanges && delayed.Username == "Other_Player",
            "queued source draft cannot land in a different account after selection changes");
    }
    private static byte[] Png(int width = 64, int height = 64, byte tint = 80)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(new SKColor(tint, 120, 160));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static async Task Reject<T>(Func<Task> action, Action<bool, string> check, string message) where T : Exception
    { try { await action(); } catch (T) { check(true, message); return; } throw new Exception("Expected " + typeof(T).Name + ": " + message); }
    private static Task Idle(SkinCatalogViewModel model) => Until(() => !model.IsBusy && !model.IsSearching && !model.IsLoadingDetails);
    private static async Task Until(Func<bool> condition)
    { var until = DateTime.UtcNow.AddSeconds(12); while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(15); } }
    private static async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }
    private sealed class DeferredSource : ISkinCatalogSource
    {
        public SkinCatalogSource Source => new("fixture", "Fixture", "Only test data");
        public TaskCompletionSource<SkinCatalogPage> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Error;
        public Task<SkinCatalogPage> SearchAsync(string query, int offset, int limit, CancellationToken token) => Error is null ? Result.Task : Task.FromException<SkinCatalogPage>(Error);
        public Task<SkinCatalogTexture> ReadAsync(SkinCatalogItem item, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class NeverSkinService : ISkinService
    {
        public Task<AccountSkin> GetAsync(LauncherAccount account, CancellationToken token) => throw new Exception("Draft must not fetch credentials");
        public Task<AccountSkin> ApplyAsync(LauncherAccount account, SkinImage image, SkinModel model, CancellationToken token) => throw new Exception("Draft must not upload");
        public Task<AccountSkin> ResetAsync(LauncherAccount account, CancellationToken token) => throw new Exception("Draft must not reset");
    }
}
