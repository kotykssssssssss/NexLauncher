using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CmlLib.Core.Auth;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;
using NexLauncher.Views;

internal static class ModrinthBrowserChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "browser"); Directory.CreateDirectory(directory);
        using var transport = new BrowserHttp(); using var client = new HttpClient(transport);
        var http = new LauncherHttp(client); var api = new ModrinthService(http);
        var options = new ModrinthSearchOptions("1.21.1", "fabric", "adventure", "client_and_server", "updated");
        var path = Uri.UnescapeDataString(ModrinthService.SearchPath("space & Unicode 世界", true, null, 20, options));
        check(path.Contains("project_type:modpack") && path.Contains("versions:1.21.1") && path.Contains("categories:fabric") && path.Contains("categories:adventure") && path.Contains("environment:client_and_server") && path.EndsWith("offset=20&index=updated"), "pack search uses real API facets, sort, escaped query and pagination");
        foreach (var loader in new[] { ModLoader.Fabric, ModLoader.Forge, ModLoader.NeoForge })
        {
            var modPath = Uri.UnescapeDataString(ModrinthService.SearchPath("", false, new() { VersionId = "1.20.1", Loader = loader }, 0, options));
            check(modPath.Contains("project_type:mod\"") && modPath.Contains("versions:1.20.1") && !modPath.Contains("versions:1.21.1") && modPath.Contains("categories:" + loader.ToString().ToLowerInvariant()), "mods filters are locked to target instance: " + loader);
        }
        try { ModrinthService.SearchPath("", true, null, 0, options with { Category = "x\" OR project_type:mod" }); throw new Exception("Expected filter rejection"); }
        catch (ArgumentException) { check(true, "filter values cannot inject additional search expressions"); }
        var tags = await api.FilterCatalogAsync(default);
        check(tags.Categories.Count == 2 && tags.Versions.Count == 2 && transport.Paths.Contains("tag/category") && transport.Paths.Contains("tag/game_version"), "filter choices come from official public tag endpoints");
        var fabric = new GameInstance { Name = "Fabric · мир", VersionId = "1.21.1", Loader = ModLoader.Fabric, LoaderVersion = "0.16.10", GameDirectory = Path.Combine(directory, "Fabric") };
        var forge = new GameInstance { Name = "Forge", VersionId = "1.20.1", Loader = ModLoader.Forge, LoaderVersion = "47.4.0", GameDirectory = Path.Combine(directory, "Forge") };
        fabric.GameDirectory = Path.Combine(directory, fabric.Id, "game"); forge.GameDirectory = Path.Combine(directory, forge.Id, "game");
        var store = new ConfigurationStore(Path.Combine(directory, "app-data"));
        await store.SaveAsync(new() { Instances = [fabric, forge], SelectedInstanceId = fabric.Id });
        var shellModel = new MainWindowViewModel(store, new NoMinecraft(), new FakeAccounts(), modrinth: api);
        await shellModel.InitializeAsync();
        fabric = shellModel.Instances[0]; forge = shellModel.Instances[1];
        var model = shellModel.Catalog;
        model.ConfigureInstances([new() { Name = "Vanilla" }, fabric, forge], fabric, x => x.GameDirectory);
        check(model.TargetInstances.Count == 2 && model.TargetInstance == fabric, "global Mods target selector excludes Vanilla and keeps preferred instance");
        model.Open(true, null, ""); await Idle(model);
        check(model.Results.Count == 20 && model.IsPacks && model.Filters.Categories.Any(x => x.Value == "adventure") && model.Filters.MinecraftVersions.Count == 2, "Modpacks opens global catalogue with relevant categories and release Minecraft choices");
        model.Query = "adventure"; model.Filters.Sort = ModrinthFiltersViewModel.Sorts[2]; model.Filters.Loader = ModrinthFiltersViewModel.Loaders[1];
        await Idle(model); await model.NextCommand.ExecuteAsync(null);
        check(model.PageLabel.StartsWith("21–40") && transport.Paths.Last().Contains("index=updated"), "sort and next page affect actual API request");
        transport.FailSearch = true;
        await model.NextCommand.ExecuteAsync(null);
        check(model.HasError && model.PageLabel.StartsWith("21–40"), "failed next page retains the current result page and exposes retry");
        transport.FailSearch = false; await model.SearchCommand.ExecuteAsync(null);
        check(!model.HasError && model.PageLabel.StartsWith("21–40"), "retry restores search without losing current pagination");

        var window = new MainWindow(shellModel) { Width = 1920, Height = 1080 }; window.Show();
        shellModel.CurrentPage = "modrinth";
        var view = window.GetVisualDescendants().OfType<ModrinthView>().Single();
        await Layout();
        var cards = view.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("project-card") && x.IsEffectivelyVisible).ToArray();
        var results = view.FindControl<ScrollViewer>("ResultsScroll")!;
        check(view.Bounds.Width >= (window.ClientSize.Width - 196) * .9 && results.Bounds.Width > 1300 && results.Bounds.Height > 600, "wide Modrinth fills real MainWindow content width and height instead of a fixed central panel");
        check(cards.Length == 20 && cards[0].Bounds.Width >= results.Bounds.Width - 20 && cards[0].Command == model.OpenProjectCommand, "wide horizontal project cards expose a working whole-card details command");
        Save(window, directory, "catalogue-1920");
        results.Offset = new Vector(0, 400); await Layout(); var savedOffset = results.Offset.Y;
        cards[0].Command!.Execute(cards[0].CommandParameter); await Idle(model); await Layout();
        check(model.IsDetails && model.Details?.Project.Id == "pack0020" && model.SelectedVersion?.Id == "version1", "whole-card click opens full project page and selects a compatible concrete version");
        var olderMinecraft = new FilterChoice("1.20.1", "1.20.1");
        model.Filters.MinecraftVersions.Add(olderMinecraft); model.Filters.Minecraft = olderMinecraft;
        await model.RetryDetailsCommand.ExecuteAsync(null);
        check(model.HasNoVersions && !model.PrimaryActionCommand.CanExecute(null), "pack version chooser also honors the selected Minecraft filter");
        model.Filters.Minecraft = model.Filters.MinecraftVersions[0];
        await model.RetryDetailsCommand.ExecuteAsync(null); await Layout();
        var header = view.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("qc-project-header"));
        check(header.Bounds.Width >= view.Bounds.Width - 20 && view.FindControl<ScrollViewer>("DetailsScroll")!.Bounds.Height > 600, "details replaces catalogue across the full content area");
        Save(window, directory, "details-1920");
        model.BackCommand.Execute(null); await Layout();
        check(model.IsBrowsing && model.Query == "adventure" && model.Filters.Sort.Value == "updated" && model.PageLabel.StartsWith("21–40") && Math.Abs(results.Offset.Y - savedOffset) < 2, "Back preserves mode, search, filters, sort, page and scroll position");
        model.ModsModeCommand.Execute(null); await Idle(model);
        check(model.IsMods && model.Query == "" && model.TargetInstance == fabric && model.Results.All(x => x.Hit.ProjectType == "mod") && model.Filters.Categories.Any(x => x.Value == "optimization"), "Mods is separate catalogue with instance context and independent filters");
        model.Query = "performance"; await Idle(model);
        model.TargetInstance = forge; await Idle(model);
        check(Uri.UnescapeDataString(transport.Paths.Last()).Contains("versions:1.20.1") && Uri.UnescapeDataString(transport.Paths.Last()).Contains("categories:forge") && model.Query == "performance", "changing target updates Minecraft and loader facets while retaining mods query");
        model.PacksModeCommand.Execute(null); await Idle(model);
        check(model.Query == "adventure" && model.Filters.Loader.Value == "fabric" && model.PageLabel.StartsWith("21–40"), "switching back to Modpacks restores its independent query, loader and page");
        model.ModsModeCommand.Execute(null); await Idle(model);
        check(model.Query == "performance" && model.TargetInstance == forge && model.IsMods, "switching to Mods restores chosen instance and mods query");
        model.OpenProjectCommand.Execute(model.Results[0]); await Idle(model);
        check(model.HasNoVersions && !model.PrimaryActionCommand.CanExecute(null), "details rejects Fabric-only version for Forge target before installation");
        model.BackCommand.Execute(null); model.TargetInstance = fabric; await Idle(model);

        foreach (var size in new[] { (1060, 760), (900, 650), (1920, 1080) })
        {
            window.Width = size.Item1; window.Height = size.Item2; await Layout();
            var filter = view.FindControl<Border>("FiltersPanel")!;
            var wide = view.Bounds.Width >= 1000;
            Save(window, directory, "mods-" + size.Item1);
            check(Grid.GetRow(filter) == (wide ? 1 : 0) && results.Bounds.Height > 100 && results.Extent.Width <= results.Viewport.Width + 1, $"resize {size.Item1}×{size.Item2}: adaptive filters, scrollable results and no horizontal overflow ({results.Bounds.Height}, {results.Extent.Width}/{results.Viewport.Width})");
            if (!wide)
            {
                view.FindControl<Expander>("FiltersExpander")!.IsExpanded = true; await Layout();
                check(results.Bounds.Height > 70 && filter.Bounds.Height <= view.Bounds.Height * .25 + 1, "expanded compact filters leave results scrollable at " + size.Item1);
                view.FindControl<Expander>("FiltersExpander")!.IsExpanded = false;
            }
        }
        model.InstalledModsCommand.Execute(null); await Layout();
        check(view.FindControl<Grid>("BrowserLayout")!.ColumnDefinitions[0].ActualWidth == 0 && model.HasNoInstalled, "empty managed-mod list uses full width without an unused filter column");
        model.BrowseModsCommand.Execute(null); await Layout();
        var themePath = Path.Combine(directory, "theme.css");
        await File.WriteAllTextAsync(themePath, ".project-card { background: #403050; border-radius: 18px; } #modrinth-filters { background: #203040; } #project-header { background: #402030; } #project-description { background: #203040; }");
        using var theme = new QuickCssService(window);
        var themed = await theme.ConfigureAsync(new() { Enabled = true, FilePath = themePath, AutoReload = false }); await Layout();
        var card = view.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("qc-project-card") && x.IsEffectivelyVisible);
        check(themed.IsApplied && card.Background is ISolidColorBrush brush && brush.Color == Color.Parse("#403050") && card.CornerRadius.TopLeft == 18, "public project-card Quick CSS overrides the standard theme");
        Save(window, directory, "mods-themed");
        model.OpenProjectCommand.Execute(model.Results[0]); await Idle(model); await Layout();
        check(view.FindControl<Border>("DetailsDescription")!.Background is ISolidColorBrush background && background.Color == Color.Parse("#203040"), "public project-description selector styles the full details page");
        model.BackCommand.Execute(null);
        shellModel.ToggleLogCommand.Execute(null); await Layout();
        check(window.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("qc-log-panel")).IsEffectivelyVisible, "launcher log remains available from full-page Modrinth");
        shellModel.ToggleLogCommand.Execute(null);
        model.Query = "empty"; await Idle(model);
        check(model.HasNoResults && !model.HasError && model.Results.Count == 0, "empty search state is distinct from errors and loading");
        model.Query = "waiting"; await Until(() => transport.Waiting);
        check(model.IsBusy && model.CancelRequestCommand.CanExecute(null), "in-flight search shows cancellable loading state");
        model.CancelRequestCommand.Execute(null); await Idle(model);
        check(!model.HasError && !model.IsBusy && model.Message.Contains("отменена"), "cancelled search settles without a network error or stale results");
        check(transport.UserAgents.All(x => x == LauncherHttp.UserAgent), "all new filters, search and details requests retain identifying User-Agent");
        shellModel.CurrentPage = "mods"; model.PacksModeCommand.Execute(null); await Idle(model);
        var published = new GameInstance { Name = "Published pack", VersionId = "1.21.1", Loader = ModLoader.Fabric, LoaderVersion = "0.16.10" };
        published.GameDirectory = Path.Combine(directory, published.Id, "game");
        shellModel.Instances.Add(published);
        check(model.TargetInstances.Any(x => x.Id == published.Id), "newly published instance immediately appears in global Mods target picker");
        shellModel.SelectedInstance = published;
        check(model.IsPacks, "publishing or selecting an instance does not force a Modpacks page back into Mods");
        model.ConfigureInstances([], null, x => x.GameDirectory); model.Open(false, null, ""); await Layout();
        check(model.NeedsInstance && !model.HasError && !model.SearchCommand.CanExecute(null), "Mods without an instance has a helpful empty state rather than an auth or Vanilla error");
        window.Close();
    }
    private static async Task Idle(ModrinthViewModel model) => await Until(() => !model.IsBusy && !model.IsLoadingDetails && model.FiltersMessage != "Загрузка фильтров…");
    public static async Task LiveAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "browser-live"); Directory.CreateDirectory(directory);
        var store = new ConfigurationStore(Path.Combine(directory, "data"));
        var target = new GameInstance { Name = "Fabric 1.21.1", VersionId = "1.21.1", Loader = ModLoader.Fabric, LoaderVersion = "0.16.10", GameDirectory = Path.Combine(directory, "game") };
        target.GameDirectory = Path.Combine(directory, target.Id, "game");
        await store.SaveAsync(new() { Instances = [target], SelectedInstanceId = target.Id });
        var vm = new MainWindowViewModel(store, new NoMinecraft(), new FakeAccounts()); await vm.InitializeAsync();
        var window = new MainWindow(vm) { Width = 1920, Height = 1080 }; window.Show();
        try
        {
            vm.CurrentPage = "modrinth"; var browser = vm.Catalog;
            await Until(() => !browser.IsBusy && browser.FiltersMessage != "Загрузка фильтров…", 90);
            check(!browser.HasError && browser.Filters.Categories.Count > 1 && browser.Filters.MinecraftVersions.Count > 1, "live official category/game version lists populate browser filters");
            foreach (var packs in new[] { true, false })
            {
                if (!packs) browser.ModsModeCommand.Execute(null);
                browser.Query = packs ? "Fabulously Optimized" : "Sodium";
                await Until(() => !browser.IsBusy, 90); await Layout();
                check(!browser.HasError && browser.Results.Count > 0 && browser.Results.All(x => x.Hit.ProjectType == (packs ? "modpack" : "mod")), "live " + (packs ? "Modpacks" : "Mods") + " returns separate official search results");
                Save(window, directory, packs ? "packs" : "mods");
                browser.OpenProjectCommand.Execute(browser.Results[0]);
                await Until(() => !browser.IsLoadingDetails, 90); await Layout();
                check(!browser.HasError && browser.HasDetails && browser.Details!.Description.Blocks.Count > 0 && browser.Versions.Count > 0, "live project description and concrete versions render: " + browser.Details?.Project.Title);
                if (!packs) check(browser.Versions.All(x => ModrinthService.IsCompatible(x, target)), "live Mods version chooser uses exact Minecraft + Fabric compatibility");
                Save(window, directory, packs ? "pack-details" : "mod-details");
                window.Width = 1060; window.Height = 760; await Layout(); Save(window, directory, packs ? "pack-details-small" : "mod-details-small");
                browser.BackCommand.Execute(null); await Layout();
                check(browser.IsBrowsing && browser.Query == (packs ? "Fabulously Optimized" : "Sodium") && browser.Results.Count > 0, "live details Back preserves search results and query");
                Save(window, directory, packs ? "packs-small" : "mods-small");
                window.Width = 1920; window.Height = 1080;
            }
        }
        finally { window.Close(); }
    }
    private static async Task Until(Func<bool> ready, int seconds = 8)
    { var deadline = DateTime.UtcNow.AddSeconds(seconds); while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Browser check timed out"); await Task.Delay(10); } }
    private static async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(30); Dispatcher.UIThread.RunJobs(); }
    private static void Save(Window window, string root, string name)
    { using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default); }
    private sealed class NoMinecraft : IMinecraftService
    {
        public Task<IReadOnlyList<MinecraftRelease>> GetVersionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MinecraftRelease>>([new("1.21.1", "release", DateTimeOffset.Parse("2024-08-08"))]);
        public bool IsInstalled(GameInstance instance) => false;
        public Task InstallAsync(GameInstance instance, IProgress<LaunchProgress> progress, CancellationToken token) => throw new NotSupportedException();
        public Task<int> LaunchAsync(GameInstance instance, MSession session, IProgress<LaunchProgress> progress, Action<string> log, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class BrowserHttp : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> UserAgents { get; } = [];
        public bool FailSearch, Waiting;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery[4..]); Paths.Add(path); UserAgents.Add(request.Headers.UserAgent.ToString());
            object data;
            if (path == "tag/category") data = new[] { new { name = "adventure", project_type = "modpack" }, new { name = "optimization", project_type = "mod" } };
            else if (path == "tag/game_version") data = new[] { new { version = "1.21.1", version_type = "release", date = "2024-08-08T00:00:00Z" }, new { version = "24w33a", version_type = "snapshot", date = "2024-08-15T00:00:00Z" } };
            else if (path.StartsWith("search?"))
            {
                if (FailSearch) return new(HttpStatusCode.BadGateway);
                if (path.Contains("query=waiting")) { Waiting = true; await Task.Delay(Timeout.Infinite, token); }
                var offset = path.Contains("offset=40") ? 40 : path.Contains("offset=20") ? 20 : 0;
                var type = path.Contains("project_type:modpack") ? "modpack" : "mod";
                var empty = path.Contains("query=empty");
                data = new { hits = Enumerable.Range(offset, empty ? 0 : 20).Select(i => new {
                    project_id = (type == "modpack" ? "pack" : "mod") + i.ToString("D4"), project_type = type,
                    title = i % 2 == 0 ? "Adventure Together — A long project name with exploration, new dimensions and a world to discover" : "Performance essentials",
                    description = string.Join(" ", Enumerable.Repeat("Исследуй мир вместе с друзьями. Compatible improvements for your next Minecraft adventure.", 3)),
                    author = "Example Author", organization = "Example Team", downloads = 1234567, versions = new[] { "1.20.1", "1.21", "1.21.1" },
                    categories = new[] { "fabric", "adventure", "multiplayer", "optimization", "worldgen", "technology" }, date_modified = "2026-09-01T10:00:00Z"
                }), total_hits = empty ? 0 : 60, limit = 20, offset };
            }
            else if (path.EndsWith("/members")) data = new[] { new { user = new { username = "example", name = "Example Author" }, role = "Owner", ordering = 0 } };
            else if (path.Contains("/version")) data = new[] { new { id = "version1", project_id = path.Split('/')[1], version_number = "1.0.0", game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" }, environment = "client_and_server", date_published = "2026-09-01T10:00:00Z", dependencies = Array.Empty<object>(), files = new[] { new { filename = "example.jar", url = "https://cdn.modrinth.com/example.jar", size = 123, hashes = new { sha512 = new string('0', 128) }, primary = true } } } };
            else if (path.StartsWith("project/")) data = new { id = path.Split('/')[1], project_type = path.Contains("pack") ? "modpack" : "mod", title = "Adventure Together — explore new worlds with friends", description = "An accessible adventure with carefully selected improvements.", body = "# Welcome to your next adventure\n\nRead the complete project description here. **Safe formatting** and [project links](https://example.org) work without a browser.\n\n## Features\n\n- World exploration\n- Improved performance\n- Multiplayer\n\n" + string.Join("\n\n", Enumerable.Repeat("A detailed paragraph describing compatibility, installation and the experience offered by this project.", 18)), client_side = "required", game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" }, categories = new[] { "adventure", "multiplayer" }, downloads = 1234567, published = "2025-01-01T00:00:00Z", updated = "2026-09-01T10:00:00Z", license = new { id = "MIT" } };
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json"), Headers = { CacheControl = new() { NoStore = true } } };
        }
    }
}
