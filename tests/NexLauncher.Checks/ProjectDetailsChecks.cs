using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CmlLib.Core.Auth;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;
using NexLauncher.Views;

internal static class ProjectDetailsChecks
{
    private const string Body = "# About the project\n\nA **bold** idea, *emphasis* and `code`. [Read the wiki](https://example.org/wiki)\n\n- One feature\n- Another feature\n\n> A quotation\n\n```java\nSystem.out.println(\"text only\");\n```\n\n![Screenshot](https://example.org/picture.png)";
    private static readonly byte[] Jar = Encoding.UTF8.GetBytes("fixture bytes; never executed");
    private static Dictionary<string, string> Hashes(byte[] bytes) => new() { ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)), ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)) };

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "details"); Directory.CreateDirectory(directory);
        Parse(check);
        await Api(check);
        await Ui(directory, check);
    }
    private static async Task Reject<T>(Func<Task> action, Action<bool, string> check, string name) where T : Exception
    { try { await action(); } catch (T) { check(true, name); return; } throw new Exception("Expected " + typeof(T).Name + ": " + name); }

    private static void Parse(Action<bool, string> check)
    {
        var description = ProjectDescriptionParser.Parse(Body);
        check(description.Blocks.Select(x => x.Kind).Distinct().Count() == 5, "description parses headings, paragraphs, lists, quotes and fenced code");
        var spans = description.Blocks.SelectMany(x => x.Spans).ToArray();
        check(spans.Any(x => x.Kind == DescriptionSpanKind.Bold) && spans.Any(x => x.Kind == DescriptionSpanKind.Italic) && spans.Any(x => x.Kind == DescriptionSpanKind.Code), "description preserves supported emphasis and inline code");
        check(spans.Count(x => x.Link is not null) == 2 && spans.Any(x => x.Text == "Изображение: Screenshot"), "description images become explicit links without remote loading");
        var longBody = new string('x', 15000) + " FINAL PARAGRAPH";
        check(ProjectDescriptionParser.Parse(longBody).Blocks.Single().Spans.Single().Text == longBody, "complete description beyond old 10000-character truncation is preserved");
        const string hostile = "<script>alert('x')</script><iframe src='file:///x'></iframe>\n\n[bad](javascript:alert) [data](data:text/html,bad) [file](file:///C:/x) [shell](shell:AppsFolder)";
        var blocked = ProjectDescriptionParser.Parse(hostile);
        check(blocked.Blocks.SelectMany(x => x.Spans).All(x => x.Link is null) && blocked.Blocks[0].Spans[0].Text.Contains("<script>"), "HTML and script remain text; active link protocols never reach render model");
        foreach (var url in new[] { "javascript:alert(1)", "file:///C:/x", "data:text/html,x", "ms-settings:privacy", "https://user:password@example.org", "//example.org", "https://localhost/x", "https://example.org/\nfoo", "https:\\example.org" })
            check(!SafeProjectLink.TryCreate(url, out _), "unsafe external link rejected: " + url.Replace('\n', ' '));
        check(SafeProjectLink.TryCreate("https://example.org/wiki?q=test#section", out _), "explicit HTTPS project link is allowed");
        check(ProjectDescriptionParser.Parse(new string('[', 100000)).Blocks.Count == 1, "malformed bracket content is handled within bounded scans");
        try { ProjectDescriptionParser.Parse(new string('x', ProjectDescriptionParser.MaxCharacters + 1)); throw new Exception("Expected description size limit"); }
        catch (InvalidDataException) { check(true, "oversized description reports explicit limit instead of silent truncation"); }
        try { ProjectDescriptionParser.Parse(Body, new CancellationToken(true)); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "description parser honors cancellation"); }
        var opened = new List<string>();
        using var detail = new ProjectDetailsViewModel(new() { Id = "project1", ProjectType = "mod", SourceUrl = "javascript:bad", WikiUrl = "https://example.org/wiki" }, description, "", "Author", [], opened.Add);
        detail.OpenLinkCommand.Execute("file:///C:/x"); detail.OpenLinkCommand.Execute("https://example.org/wiki");
        check(opened.SequenceEqual(new[] { "https://example.org/wiki" }) && detail.Links.Count == 2, "project links and commands revalidate URL before handing it to browser");
    }
    private static async Task Api(Action<bool, string> check)
    {
        var http = new FixtureHttp(); var api = new ModrinthService(new LauncherHttp(new HttpClient(http)));
        http.Json("project/project1", """
            {"id":"project1","title":"Project","description":"Summary","body":"# Complete body","project_type":"mod","game_versions":["1.21.1"],"loaders":["fabric"],"downloads":4321,"organization":"org12345","source_url":"https://example.org/source","license":{"id":"MIT"}}
            """);
        http.Json("project/project1/members", """
            [{"user":{"username":"second","name":null},"role":"Member","ordering":2},{"user":{"username":"first","name":"First"},"role":"Owner","ordering":1}]
            """);
        var project = await api.ProjectAsync("project1", default); var members = await api.MembersAsync(project.Id, default);
        check(project.Body == "# Complete body" && project.Downloads == 4321 && project.Organization == "org12345" && project.SourceUrl is not null, "official project response maps full description, statistics, organization and links");
        check(members.Select(x => x.User.Username).SequenceEqual(new[] { "first", "second" }), "official public team response preserves authors and ordering without private fields");
        check(http.Requests.All(x => x.UserAgent == LauncherHttp.UserAgent), "new project/team requests use centralized identifying User-Agent");
        foreach (var loader in new[] { ModLoader.Fabric, ModLoader.Forge, ModLoader.NeoForge })
        {
            var instance = new GameInstance { Loader = loader, VersionId = "1.21.1" };
            var path = ModrinthService.SearchPath("shader & performance", false, instance, 0);
            http.Json(path, "{\"hits\":[],\"total_hits\":0,\"offset\":0,\"limit\":20}");
            await api.SearchAsync("shader & performance", false, instance, 0, default);
            check(Uri.UnescapeDataString(http.Requests.Last().Url).Contains("categories:" + loader.ToString().ToLowerInvariant()) && Uri.UnescapeDataString(http.Requests.Last().Url).Contains("versions:1.21.1"), "actual search request automatically filters existing " + loader + " instance and Minecraft");
        }
        http.Json("project/badproj1", "{\"id\":\"badproj1\",\"title\":\"X\",\"project_type\":\"mod\",\"body\":null}");
        await Reject<InvalidDataException>(() => api.ProjectAsync("badproj1", default), check, "missing API body fails with metadata error");
        http.Json("project/badteam1/members", "[{\"user\":null}]");
        await Reject<InvalidDataException>(() => api.MembersAsync("badteam1", default), check, "malformed public author data fails safely");
        http.Status = HttpStatusCode.NotFound;
        await Reject<IOException>(() => api.ProjectAsync("missing1", default), check, "project HTTP error remains actionable and nonfatal");
        await Reject<OperationCanceledException>(() => api.ProjectAsync("project1", new CancellationToken(true)), check, "project request respects cancellation even when cached");
        http.Status = HttpStatusCode.OK;
        foreach (var policy in new[] { "no-store", "no-cache", "private", "max-age=0" })
        {
            http.Policy = System.Net.Http.Headers.CacheControlHeaderValue.Parse(policy);
            var path = "project/cache" + policy.Replace("=", "").Replace("-", ""); http.Json(path, "{}");
            var cachedHttp = new LauncherHttp(new HttpClient(http)); var before = http.Requests.Count;
            await cachedHttp.GetBytesAsync(ModrinthService.Api + path, ["api.modrinth.com"], default);
            await cachedHttp.GetBytesAsync(ModrinthService.Api + path, ["api.modrinth.com"], default);
            check(http.Requests.Count == before + 2, "central HTTP cache respects server policy: " + policy);
        }
    }
    private static async Task Ui(string root, Action<bool, string> check)
    {
        var api = new FixtureApi(); var transport = new FixtureHttp(); var minecraft = new FixtureMinecraft();
        var http = new LauncherHttp(new HttpClient(transport)); var published = new List<GameInstance>();
        var errors = new List<Exception>(); var working = false; ModrinthViewModel? catalog = null;
        catalog = new(api, http, minecraft, async action =>
        {
            working = true; catalog!.RefreshCommands();
            try { await action(default); } catch (Exception ex) { errors.Add(ex); }
            finally { working = false; catalog.RefreshCommands(); }
        }, () => new Progress<LaunchProgress>(), () => !working, () => root,
        (instance, _) => { published.Add(instance); return Task.CompletedTask; }, _ => { });
        using var lifetime = catalog;
        var main = MakeVersion("project1", "version1"); var required = MakeVersion("require1", "requirev");
        main.Dependencies.Add(new() { ProjectId = required.ProjectId, DependencyType = "required" }); api.Add(main, required);
        var game = Path.Combine(root, "existing-game"); Directory.CreateDirectory(Path.Combine(game, "mods")); await File.WriteAllTextAsync(Path.Combine(game, "mods", "manual.jar"), "keep");
        var instance = new GameInstance { Loader = ModLoader.Fabric, VersionId = "1.21.1", GameDirectory = game };
        transport.Files[main.Files[0].Url] = Jar; transport.Files[required.Files[0].Url] = Jar;
        catalog.Open(false, instance, game); await Until(() => !catalog.IsBusy);
        catalog.SelectedResult = catalog.Results.First(x => x.Hit.ProjectId == main.ProjectId);
        await Until(() => !catalog.IsLoadingDetails);
        check(catalog.IsDetails && !catalog.IsBrowsing && catalog.Details?.Project.Id == main.ProjectId && catalog.Versions.Count == 1, "clicking a result opens dedicated project details with compatible version chooser");
        check(catalog.Details!.Description.Blocks.Count > 1 && catalog.Details.Credits.Contains("Fixture Author"), "details contains complete native description and API team attribution");
        var view = new ModrinthView { DataContext = catalog };
        var window = new Window { Classes = { "qc-app" }, Content = view, Width = 880, Height = 860, Title = "Details fixture" }; window.Show(); Dispatcher.UIThread.RunJobs();
        var scroll = view.FindControl<ScrollViewer>("DetailsScroll")!;
        using (var frame = window.CaptureRenderedFrame()) { check(frame is not null, "project details renders with existing NexLauncher theme"); frame!.Save(Path.Combine(root, "project-details.png"), PngBitmapEncoderOptions.Default); }
        var descriptionControl = view.GetVisualDescendants().OfType<ProjectDescriptionView>().Single();
        var nativeText = descriptionControl.Children.OfType<TextBlock>().ToArray();
        check(nativeText.Length == catalog.Details.Description.Blocks.Count && nativeText.SelectMany(x => x.Inlines!).OfType<InlineUIContainer>().Count() == 2, "safe render model becomes native TextBlocks and explicit link buttons");
        scroll.Offset = new Vector(0, 650); Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(root, "project-description.png"), PngBitmapEncoderOptions.Default);
        var css = Path.Combine(root, "theme.css"); await File.WriteAllTextAsync(css, ".card { background: #302040; border-radius: 19px; } .quiet { color: #cc88ff; }");
        using var theme = new QuickCssService(window);
        var result = await theme.ConfigureAsync(new() { Enabled = true, FilePath = css, AutoReload = false }); Dispatcher.UIThread.RunJobs();
        var cards = view.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("card") && x.IsEffectivelyVisible).ToArray();
        check(result.IsApplied && cards.Length == 3 && cards.All(x => x.Background is ISolidColorBrush brush && brush.Color == Color.Parse("#302040")), "Quick CSS existing public card selector applies to project details without engine changes");
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(root, "project-description-themed.png"), PngBitmapEncoderOptions.Default);
        window.Close();

        await catalog.PrimaryActionCommand.ExecuteAsync(null);
        check(catalog.InstallCommand.CanExecute(null) && catalog.PlanText.Contains("require1"), "existing-instance mod install requires reviewed dependency plan");
        check(catalog.InstallLabel == "Подтвердить установку", "contextual primary action exposes confirmation only after dependency review");
        main.Loaders = ["forge"]; await catalog.InstallCommand.ExecuteAsync(null);
        check(errors.LastOrDefault() is InvalidOperationException && !File.Exists(Path.Combine(game, "mods", main.Files[0].Filename)), "concrete version compatibility is rechecked at installation after details and planning");
        main.Loaders = ["fabric"]; errors.Clear(); await catalog.PrimaryActionCommand.ExecuteAsync(null);
        check(errors.Count == 0 && catalog.Installed.Count == 2 && catalog.Details.InstallationStatus.Contains("version1"), "details installs individual mod and required dependency into existing instance and refreshes installed version");
        check(await File.ReadAllTextAsync(Path.Combine(game, "mods", "manual.jar")) == "keep", "details installation leaves manual JAR untouched");
        check(catalog.IsSelectedInstalled && !catalog.PrimaryActionCommand.CanExecute(null), "installed selected version is labelled and cannot be installed again by primary action");
        var next = MakeVersion("project1", "version2"); next.DatePublished = main.DatePublished.AddDays(1); next.Dependencies.Add(main.Dependencies[0]); api.Add(next); transport.Files[next.Files[0].Url] = Jar;
        await catalog.RetryDetailsCommand.ExecuteAsync(null);
        check(catalog.Details!.InstallationStatus.Contains("Доступно обновление: version2") && catalog.SelectedVersion?.Id == next.Id, "details update availability compares newer compatible version against persisted installed version");
        next.Dependencies.Add(new() { ProjectId = required.ProjectId, DependencyType = "incompatible" });
        await catalog.PlanCommand.ExecuteAsync(null);
        check(errors.LastOrDefault() is InvalidOperationException && !catalog.InstallCommand.CanExecute(null), "incompatible dependency blocks details install plan");
        next.Dependencies.RemoveAt(next.Dependencies.Count - 1); errors.Clear();
        await catalog.PlanCommand.ExecuteAsync(null); await catalog.InstallCommand.ExecuteAsync(null);
        check(errors.Count == 0 && catalog.Installed.Single(x => x.Explicit).Version.Id == next.Id, "details update uses safe existing mod transaction pipeline");
        catalog.BackCommand.Execute(null); catalog.SelectedInstalled = catalog.Installed.Single(x => x.Explicit); catalog.ShowInstalledCommand.Execute(null); await Until(() => !catalog.IsLoadingDetails);
        check(catalog.IsDetails && catalog.Details?.Project.Id == main.ProjectId, "installed mod opens project details without another search");
        catalog.BackCommand.Execute(null); catalog.SelectedInstalled = catalog.Installed.Single(x => x.Explicit); await catalog.RemoveCommand.ExecuteAsync(null);
        check(catalog.Installed.Count == 1 && File.Exists(Path.Combine(game, "mods", "manual.jar")), "remove managed mod retains dependency and unknown manual files");

        var waiting = new TaskCompletionSource<ModrinthProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.ProjectOverride = (_, _) => waiting.Task;
        catalog.SelectedResult = catalog.Results.First(); check(catalog.IsLoadingDetails, "details exposes cancellable loading state");
        catalog.CancelRequestCommand.Execute(null); waiting.SetResult(api.Projects[main.ProjectId]); await Until(() => !catalog.IsLoadingDetails);
        check(catalog.Details is null && catalog.RetryDetailsCommand.CanExecute(null), "cancelled details response cannot restore a stale project and remains retryable");
        api.ProjectOverride = (_, _) => throw new IOException("Сеть недоступна.");
        await catalog.RetryDetailsCommand.ExecuteAsync(null);
        check(catalog.HasError && !catalog.InstallCommand.CanExecute(null), "network failure is shown without enabling installation or crashing UI");
        api.ProjectOverride = null;
        await catalog.RetryDetailsCommand.ExecuteAsync(null);
        check(catalog.HasDetails && !catalog.HasError, "details retry recovers after network failure");
        api.FailMembers = true; await catalog.RetryDetailsCommand.ExecuteAsync(null);
        check(catalog.HasDetails && !catalog.HasError && catalog.Versions.Count > 0, "unavailable team attribution does not block project description or versions");
        api.FailMembers = false;
        api.Versions.Values.Where(x => x.ProjectId == main.ProjectId).ToList().ForEach(x => x.Loaders = ["forge"]);
        await catalog.RetryDetailsCommand.ExecuteAsync(null);
        check(catalog.HasNoVersions && !catalog.PlanCommand.CanExecute(null), "empty compatible version list never enables incompatible mod install");

        var pack = MakeVersion("packproj", "packvers"); pack.Files[0].Filename = "fixture.mrpack";
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                writer.Write("{\"formatVersion\":1,\"game\":\"minecraft\",\"versionId\":\"1\",\"name\":\"Pack\",\"files\":[],\"dependencies\":{\"minecraft\":\"1.21.1\",\"fabric-loader\":\"0.16.10\"}}");
            var bytes = stream.ToArray(); pack.Files[0].Hashes = Hashes(bytes); pack.Files[0].Size = bytes.Length; transport.Files[pack.Files[0].Url] = bytes;
        }
        api.Add(pack); api.Projects[pack.ProjectId].ProjectType = "modpack";
        catalog.Open(true, null, ""); await Until(() => !catalog.IsBusy); catalog.SelectedResult = catalog.Results.Single(); await Until(() => !catalog.IsLoadingDetails);
        await catalog.InstallCommand.ExecuteAsync(null);
        check(errors.Count == 0 && published.Count == 1 && published[0].GameDirectory != game && minecraft.Installs == 1 && published[0].ModrinthProjectId == pack.ProjectId, "modpack details still stages and installs a separate instance using original pipeline");
        check(File.Exists(Path.Combine(game, "mods", "manual.jar")), "pack details cannot overlay the previously selected existing instance");
    }
    private static async Task Until(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(5); while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); } }
    private static ModrinthVersion MakeVersion(string project, string id) => new()
    {
        Id = id, ProjectId = project, VersionNumber = id, GameVersions = ["1.21.1"], Loaders = ["fabric"], Environment = "client_and_server", DatePublished = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Files = [new() { Filename = project + ".jar", Url = "https://cdn.modrinth.com/" + id, Size = Jar.Length, Hashes = Hashes(Jar), Primary = true }]
    };
    private sealed class FixtureApi : IModrinthService
    {
        public Dictionary<string, ModrinthProject> Projects { get; } = new();
        public Dictionary<string, ModrinthVersion> Versions { get; } = new();
        public Func<string, CancellationToken, Task<ModrinthProject>>? ProjectOverride;
        public bool FailMembers;
        public void Add(params ModrinthVersion[] versions)
        { foreach (var v in versions) { Versions[v.Id] = v; Projects[v.ProjectId] = new() { Id = v.ProjectId, Title = v.ProjectId, ProjectType = "mod", Body = Body, Description = "A useful project for your Minecraft instance", ClientSide = "required", GameVersions = ["1.21.1"], Loaders = ["fabric"], Downloads = 12345, License = new() { Id = "MIT" }, WikiUrl = "https://example.org/wiki" }; } }
        public Task<ModrinthFilterCatalog> FilterCatalogAsync(CancellationToken token) => Task.FromResult(new ModrinthFilterCatalog([], []));
        public Task<ModrinthSearchResult> SearchAsync(string query, bool packs, GameInstance? instance, int offset, CancellationToken token, ModrinthSearchOptions? options = null)
        { token.ThrowIfCancellationRequested(); var hits = Projects.Values.Where(x => (x.ProjectType == "modpack") == packs).Select(x => new ModrinthHit { ProjectId = x.Id, Title = x.Title, Author = "Fixture Author", ProjectType = x.ProjectType }).ToList(); return Task.FromResult(new ModrinthSearchResult { Hits = hits, TotalHits = hits.Count }); }
        public Task<ModrinthProject> ProjectAsync(string id, CancellationToken token) => ProjectOverride?.Invoke(id, token) ?? Task.FromResult(Projects[id]);
        public Task<IReadOnlyList<ModrinthTeamMember>> MembersAsync(string projectId, CancellationToken token) => FailMembers ? Task.FromException<IReadOnlyList<ModrinthTeamMember>>(new IOException("Team unavailable")) : Task.FromResult<IReadOnlyList<ModrinthTeamMember>>([new() { User = new() { Username = "fixture", Name = "Fixture Author" }, Role = "Owner" }]);
        public Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string id, GameInstance? instance, CancellationToken token) => Task.FromResult<IReadOnlyList<ModrinthVersion>>(Versions.Values.Where(x => x.ProjectId == id).OrderByDescending(x => x.DatePublished).ToArray());
        public Task<ModrinthVersion> VersionAsync(string id, CancellationToken token) => Task.FromResult(Versions[id]);
    }
    private sealed class FixtureHttp : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<(string Url, string UserAgent)> Requests { get; } = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public System.Net.Http.Headers.CacheControlHeaderValue? Policy;
        public void Json(string path, string json) => Files[ModrinthService.Api + path] = Encoding.UTF8.GetBytes(json);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.UserAgent.ToString()));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Files.GetValueOrDefault(request.RequestUri!.AbsoluteUri) ?? []), Headers = { CacheControl = Policy } });
        }
    }
    private sealed class FixtureMinecraft : IMinecraftService
    {
        public int Installs;
        public Task<IReadOnlyList<MinecraftRelease>> GetVersionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MinecraftRelease>>([]);
        public bool IsInstalled(GameInstance instance) => false;
        public Task InstallAsync(GameInstance instance, IProgress<LaunchProgress> progress, CancellationToken token) { token.ThrowIfCancellationRequested(); Installs++; return Task.CompletedTask; }
        public Task<int> LaunchAsync(GameInstance instance, MSession session, IProgress<LaunchProgress> progress, Action<string> log, CancellationToken token) => throw new NotSupportedException();
    }
}
