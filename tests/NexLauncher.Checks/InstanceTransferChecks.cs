using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CmlLib.Core.Auth;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Instances;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.QuickCss;
using NexLauncher.Services.Storage;
using NexLauncher.ViewModels;
using NexLauncher.Views;

internal static class InstanceTransferChecks
{
    private static readonly byte[] Jar = MakeJar();
    private static readonly IProgress<LaunchProgress> Progress = new ImmediateProgress(_ => { });
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        root = Path.Combine(root, "instance transfer Мир"); Directory.CreateDirectory(root);
        await RoundTrip(root, check);
        await UnsafeArchives(root, check);
        await Ui(root, check);
    }
    private static async Task RoundTrip(string root, Action<bool, string> check)
    {
        var api = new FixtureApi(); var main = Version("main", "mainOld"); var dep = Version("dep", "depOld");
        main.Dependencies.Add(new() { ProjectId = dep.ProjectId, DependencyType = "required" });
        main.Dependencies.Add(new() { ProjectId = "optional", DependencyType = "optional" });
        main.Dependencies.Add(new() { ProjectId = "embedded", DependencyType = "embedded" });
        api.Add(main, dep, Version("main", "mainLatest"));
        var network = new FixtureHttp(); foreach (var version in api.Versions.Values) network.Files[version.Files[0].Url] = Jar;
        var http = new LauncherHttp(new HttpClient(network)); var manager = new ModManager(api, http);
        var source = new GameInstance { Name = "Тест export", VersionId = "1.21.1", Loader = ModLoader.Fabric, LoaderVersion = "0.16.10", MemoryMb = 6144, JavaPath = "C:\\Private\\Java\\java.exe" };
        source.GameDirectory = SafePaths.Resolve(Path.Combine(root, "old root"), source.Id + "/game");
        await manager.InstallAsync(source, source.GameDirectory, main.Id, Progress, default);
        api.LatestCalls = 0;
        foreach (var (relativePath, contents) in new[] {
            ("mods/local.jar", Jar), ("config/mod.json", Encoding.UTF8.GetBytes("{\"setting\":true}")),
            ("defaultconfigs/mod.toml", Encoding.UTF8.GetBytes("enabled=true")), ("resourcepacks/user.zip", Jar), ("shaderpacks/user.zip", Jar),
            ("config/tokens.json", Encoding.UTF8.GetBytes("private fixture")), ("config/execute.ps1", Encoding.UTF8.GetBytes("not executable")),
            ("saves/world/level.dat", Jar), ("logs/latest.log", Jar), ("crash-reports/private.txt", Jar),
            ("screenshots/private.png", Jar), ("servers.dat", Jar), ("usercache.json", Jar), (".nexlauncher/accounts.json", Jar) })
        { var target = SafePaths.Resolve(source.GameDirectory, relativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target, contents); }
        var exporter = new InstanceExportService(manager);
        var lean = await exporter.PlanAsync(source, source.GameDirectory, new(), Progress, default);
        check(lean.Manifest.Mods.Count == 2 && lean.Manifest.Files.Count == 0 && lean.Manifest.OmittedFiles.Contains("mods/local.jar") && lean.Notices.Any(x => x.Contains("неполной")), "export defaults to exact Modrinth references and warns about omitted local files");
        var plan = await exporter.PlanAsync(source, source.GameDirectory, new(true, true, true, true), Progress, default);
        check(plan.Manifest.Files.Count == 5 && plan.Manifest.Files.Any(x => x.Path == "mods/local.jar") && plan.Manifest.OmittedFiles.Contains("config/tokens.json"), "explicit export includes local mods/configs/resourcepacks/shaders but excludes known secrets and scripts");
        var path = Path.Combine(root, "Сборка export.nexpack"); await exporter.ExportAsync(plan, path, Progress, default);
        using (var archive = ZipFile.OpenRead(path))
        {
            check(archive.Entries.Count == 6 && !archive.Entries.Any(x => x.FullName.Contains(main.Files[0].Filename)), "managed JAR is referenced, never silently bundled");
            using var reader = new StreamReader(archive.GetEntry(InstanceManifestService.EntryName)!.Open()); var json = await reader.ReadToEndAsync();
            check(!json.Contains("Private") && !json.Contains(source.GameDirectory) && !json.Contains("accounts.json") && !json.Contains("saves/") && !json.Contains("servers.dat"), "portable manifest excludes Java/game absolute paths, accounts, worlds, logs and server lists");
        }
        var package = new InstancePackageReader(); var preview = await package.PreviewAsync(path, default);
        check(preview.Manifest.Mods.Single(x => x.ProjectId == main.ProjectId).VersionId == main.Id && preview.Manifest.ManifestVersion == 1 && preview.PackageSha256.Length == 64, "import preview retains exact version IDs and versioned manifest identity");
        var minecraft = new FixtureMinecraft(); var importer = new InstanceImportService(api, http, minecraft);
        var importedRoot = Path.Combine(root, "new root Unicode Мир"); var published = new List<GameInstance>();
        var imported = await importer.ImportAsync(path, preview, "Новое имя", importedRoot, [source.Name], (instance, _) => { published.Add(instance); return Task.CompletedTask; }, Progress, default);
        var importedMods = await manager.LoadAsync(imported.GameDirectory, default);
        check(published.Count == 1 && imported.Id != source.Id && imported.MemoryMb == 6144 && imported.JavaPath == "" && imported.Loader == source.Loader && imported.LoaderVersion == source.LoaderVersion, "transactional import creates a new identity and preserves Minecraft/loader/RAM with automatic Java");
        check(importedMods.Projects.Select(x => x.Version.Id).Order().SequenceEqual(new[] { main.Id, dep.Id }.Order()) && api.LatestCalls == 0, "exact import never substitutes latest project/dependency versions");
        check(File.ReadAllBytes(SafePaths.Resolve(imported.GameDirectory, "mods/local.jar")).SequenceEqual(Jar) && File.ReadAllText(SafePaths.Resolve(imported.GameDirectory, "config/mod.json")) == "{\"setting\":true}", "local files and configs survive export/import byte-for-byte");
        check(!Directory.Exists(SafePaths.Resolve(imported.GameDirectory, "saves")) && File.Exists(SafePaths.Resolve(source.GameDirectory, "saves/world/level.dat")) && source.GameDirectory.StartsWith(Path.Combine(root, "old root")), "import into a new custom root preserves all old instance paths and private worlds");
        check(network.Agents.All(x => x == LauncherHttp.UserAgent) && importedMods.Projects.Count == 2, "import reuses identifying HTTP layer and does not install optional/embedded dependencies");
        check(!Directory.EnumerateDirectories(importedRoot, ".nex-stage-*").Any() && File.Exists(SafePaths.Resolve(imported.GameDirectory, ".nexlauncher/import.json")), "successful import removes staging and records credential-free provenance");
        await Reject<InvalidOperationException>(() => importer.ImportAsync(path, preview, imported.Name.ToUpperInvariant(), importedRoot, [imported.Name], PublishNothing, Progress, default), check, "duplicate instance name is rejected without silently overwriting an instance");
        foreach (var mode in new[] { "installer", "network", "hash", "publish", "cancel" })
        {
            var failureRoot = Path.Combine(root, "failure " + mode); var game = new FixtureMinecraft { Fail = mode == "installer" };
            api.Fail = mode == "network"; network.Corrupt = mode == "hash";
            using var cancellation = new CancellationTokenSource(); network.Cancel = mode == "cancel" ? cancellation : null;
            var failed = new InstanceImportService(api, http, game);
            Func<GameInstance, CancellationToken, Task> publish = mode == "publish" ? (_, _) => throw new IOException("fixture registration failed") : PublishNothing;
            if (mode == "hash") await Reject<InvalidDataException>(() => failed.ImportAsync(path, preview, "Fail", failureRoot, [], publish, Progress, cancellation.Token), check, "failed managed download hash does not replace/create an instance");
            else if (mode == "cancel") await Reject<OperationCanceledException>(() => failed.ImportAsync(path, preview, "Fail", failureRoot, [], publish, Progress, cancellation.Token), check, "import cancellation propagates through download and staging cleanup");
            else await Reject<IOException>(() => failed.ImportAsync(path, preview, "Fail", failureRoot, [], publish, Progress, default), check, "failed " + mode + " rolls back import");
            check(!Directory.Exists(failureRoot) || Directory.GetFileSystemEntries(failureRoot).Length == 0, "failed " + mode + " leaves no registered/final/staging instance");
            api.Fail = false; network.Corrupt = false; network.Cancel = null;
        }
        await Reject<OperationCanceledException>(() => importer.ImportAsync(path, preview, "Cancel", Path.Combine(root, "pre-cancel"), [], PublishNothing, Progress, new(true)), check, "pre-cancelled import performs no installation");
        foreach (var mode in new[] { "minecraft", "loader", "file", "id", "conflict" })
        {
            var original = api.Versions[main.Id]; var altered = Clone(original);
            if (mode == "minecraft") altered.GameVersions = ["1.20.1"];
            if (mode == "loader") altered.Loaders = ["forge"];
            if (mode == "file") altered.Files[0].Filename = "different.jar";
            if (mode == "id") altered.Id = "anotherVersion";
            if (mode == "conflict") altered.Dependencies.Add(new() { ProjectId = dep.ProjectId, DependencyType = "incompatible" });
            api.Versions[main.Id] = altered;
            var installs = minecraft.Installs;
            if (mode is "minecraft" or "loader" or "conflict") await Reject<InvalidOperationException>(() => importer.ImportAsync(path, preview, "Wrong", importedRoot, [], PublishNothing, Progress, default), check, "exact import rejects incompatible " + mode + " before installer");
            else await Reject<InvalidDataException>(() => importer.ImportAsync(path, preview, "Wrong", importedRoot, [], PublishNothing, Progress, default), check, "exact import rejects mismatched " + mode + " before installer");
            check(minecraft.Installs == installs, "no official installer starts for mismatched " + mode + " metadata"); api.Versions[main.Id] = original;
        }
        var incomplete = Clone(plan.Manifest); incomplete.Mods.RemoveAll(x => x.ProjectId == dep.ProjectId);
        var incompletePath = Path.Combine(root, "missing dependency.nexpack"); await WritePackage(incompletePath, incomplete, plan.Manifest.Files.ToDictionary(x => "files/" + x.Path, x => File.ReadAllBytes(SafePaths.Resolve(source.GameDirectory, x.Path))));
        var incompletePreview = await package.PreviewAsync(incompletePath, default);
        await Reject<InvalidDataException>(() => importer.ImportAsync(incompletePath, incompletePreview, "Incomplete", importedRoot, [], PublishNothing, Progress, default), check, "missing exact required dependency fails without guessing a latest version");
        // Export preview is a content snapshot. Failed re-export must keep an existing package.
        var originalExport = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(SafePaths.Resolve(source.GameDirectory, "config/mod.json"), "changed after preview");
        await Reject<InvalidDataException>(() => exporter.ExportAsync(plan, path, Progress, default), check, "changed local file rejects stale export preview");
        check((await File.ReadAllBytesAsync(path)).SequenceEqual(originalExport) && !Directory.GetFiles(root, "*.tmp").Any(), "failed export preserves previous archive and removes temporary partial output");
        await Reject<OperationCanceledException>(() => exporter.ExportAsync(plan, path, Progress, new(true)), check, "pre-cancelled export leaves existing package intact");
        await File.WriteAllTextAsync(SafePaths.Resolve(source.GameDirectory, "mods/" + main.Files[0].Filename), "manual mutation");
        await Reject<IOException>(() => exporter.ExportAsync(plan, path, Progress, default), check, "managed integrity changed after preview requires a fresh export preview");
        var changedPlan = await exporter.PlanAsync(source, source.GameDirectory, new(), Progress, default);
        check(changedPlan.Notices.Any(x => x.Contains("отсутствует/изменён")) && File.ReadAllText(SafePaths.Resolve(source.GameDirectory, "mods/" + main.Files[0].Filename)) == "manual mutation", "export reports damaged managed file without repairing/deleting it or bundling mutation");
        foreach (var (loader, loaderVersion) in new[] { (ModLoader.Forge, "52.0.28"), (ModLoader.NeoForge, "21.1.172") })
        {
            var mod = Version(loader + "Project", loader + "Version"); mod.Loaders = [loader.ToString().ToLowerInvariant()];
            api.Add(mod); network.Files[mod.Files[0].Url] = Jar;
            var profile = new GameInstance { Name = loader + " transfer", VersionId = "1.21.1", Loader = loader, LoaderVersion = loaderVersion };
            profile.GameDirectory = SafePaths.Resolve(Path.Combine(root, "source loaders"), profile.Id + "/game");
            await manager.InstallAsync(profile, profile.GameDirectory, mod.Id, Progress, default);
            var loaderPlan = await exporter.PlanAsync(profile, profile.GameDirectory, new(), Progress, default);
            var loaderPath = Path.Combine(root, loader + ".nexpack"); await exporter.ExportAsync(loaderPlan, loaderPath, Progress, default);
            var loaderPreview = await package.PreviewAsync(loaderPath, default);
            var restored = await importer.ImportAsync(loaderPath, loaderPreview, profile.Name, importedRoot, [], PublishNothing, Progress, default);
            check(restored.Loader == loader && restored.LoaderVersion == loaderVersion && (await manager.LoadAsync(restored.GameDirectory, default)).Projects.Single().Version.Id == mod.Id,
                loader + " instance transfer persists exact loader and compatible managed version independently of Fabric");
        }
        var oldVanilla = new GameInstance { FormatVersion = 1, Name = "Legacy Vanilla", VersionId = "1.21.1" };
        var oldPlan = await exporter.PlanAsync(oldVanilla, Path.Combine(root, "absent legacy game"), new(), Progress, default);
        check(oldPlan.Manifest.Loader == ModLoader.Vanilla && oldPlan.Manifest.Mods.Count == 0 && oldPlan.Notices.Count > 0, "old Vanilla metadata and an absent game directory can export a supported installation recipe");
        var changedArchive = Path.Combine(root, "changed package.nexpack"); var changedManifest = Clone(preview.Manifest); changedManifest.Name = "Different";
        await WritePackage(changedArchive, changedManifest, plan.Manifest.Files.ToDictionary(x => "files/" + x.Path, x => File.ReadAllBytes(SafePaths.Resolve(imported.GameDirectory, x.Path))));
        await Reject<InvalidDataException>(() => importer.ImportAsync(changedArchive, preview, "Changed", importedRoot, [], PublishNothing, Progress, default), check, "replaced archive cannot be imported using a different package's preview");
    }

    private static async Task UnsafeArchives(string root, Action<bool, string> check)
    {
        var reader = new InstancePackageReader(); var path = Path.Combine(root, "unsafe.nexpack");
        var good = new InstanceManifest { Name = "Vanilla", MinecraftVersion = "1.21.1", LauncherVersion = "0.1.1-alpha", ExportedUtc = DateTimeOffset.UtcNow };
        foreach (var mode in new[] { "future", "traversal", "absolute", "backslash", "reserved", "secret", "world", "script", "duplicate", "prefix", "hash", "missing", "extra", "symlink", "bomb", "oversize", "null" })
        {
            var manifest = Clone(good); var files = new Dictionary<string, byte[]>(); var data = Encoding.UTF8.GetBytes("fixture");
            var relative = mode switch { "traversal" => "config/../../escape", "absolute" => "C:/escape", "backslash" => "config\\escape", "reserved" => "config/CON", "secret" => "config/credentials.json", "world" => "saves/world.dat", "script" => "config/start.ps1", _ => "config/safe.txt" };
            if (mode is "future") manifest.ManifestVersion = 2;
            else if (mode is "null") manifest.Mods = null!;
            else if (mode is "extra") files["files/config/extra.txt"] = data;
            else
            {
                if (mode == "bomb") data = new byte[2 * 1024 * 1024];
                manifest.Files.Add(new(relative, mode == "oversize" ? InstanceManifestService.MaxFileBytes + 1 : data.Length, Sha(data)));
                if (mode != "missing") files["files/" + relative] = mode == "hash" ? Encoding.UTF8.GetBytes("changed") : data;
                if (mode == "duplicate") manifest.Files.Add(new("config/SAFE.txt", data.Length, Sha(data)));
                if (mode == "prefix") { manifest.Files.Add(new("config/safe.txt/child", data.Length, Sha(data))); files["files/config/safe.txt/child"] = data; }
            }
            await WritePackage(path, manifest, files, mode == "symlink");
            await Reject<InvalidDataException>(() => reader.PreviewAsync(path, default), check, "package reader rejects " + mode + " without filesystem extraction/execution");
        }
        await File.WriteAllTextAsync(path, "not zip");
        await Reject<InvalidDataException>(() => reader.PreviewAsync(path, default), check, "corrupted/non-ZIP package is rejected");
        foreach (var json in new[] { "[]", "{}", "{bad", "{\"manifestVersion\":1,\"manifestVersion\":2}", JsonSerializer.Serialize(good, InstanceManifestService.Json).Replace("\"name\":", "\"unrecognized\":") })
        {
            await WriteRaw(path, json);
            await Reject<InvalidDataException>(() => reader.PreviewAsync(path, default), check, "malformed, duplicate-property or unknown-field JSON is rejected");
        }
        await WritePackage(path, good, []);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.CreateEntry(InstanceManifestService.EntryName.ToUpperInvariant());
        await Reject<InvalidDataException>(() => reader.PreviewAsync(path, default), check, "case-insensitive duplicate archive entry is rejected");
        await Reject<OperationCanceledException>(() => reader.PreviewAsync(path, new(true)), check, "cancelled archive validation returns cancellation");
        check(!File.Exists(Path.Combine(root, "escape")), "malicious packages never extract outside a new instance");
        check(InstanceManifestService.IsAllowedContent("mods/SecretRooms.jar") && InstanceManifestService.IsAllowedContent("config/secretrooms-common.toml"),
            "privacy denylist does not reject legitimate mod/config names merely containing secret");
    }

    private static async Task Ui(string root, Action<bool, string> check)
    {
        var store = new ConfigurationStore(Path.Combine(root, "UI data")); var instance = new GameInstance { Name = "UI Vanilla", VersionId = "1.21.1" };
        instance.GameDirectory = SafePaths.Resolve(Path.Combine(store.DataDirectory, "instances"), instance.Id + "/game");
        Directory.CreateDirectory(SafePaths.Resolve(instance.GameDirectory, "config")); await File.WriteAllTextAsync(SafePaths.Resolve(instance.GameDirectory, "config/test.json"), "{}");
        await store.SaveAsync(new() { Instances = [instance], SelectedInstanceId = instance.Id });
        var accounts = new FakeAccounts(); await accounts.CreateLocalAccountAsync("Transfer_Player", default); var active = accounts.ActiveAccountId;
        var minecraft = new FixtureMinecraft(); var shell = new MainWindowViewModel(store, minecraft, accounts, modrinth: new FixtureApi());
        await shell.InitializeAsync(); var window = new MainWindow(shell) { Width = 1060, Height = 760 }; window.Show();
        var model = shell.Transfer; var path = Path.Combine(root, "UI export.nexpack");
        model.SavePackageAsync = () => Task.FromResult<string?>(path); model.PickPackageAsync = () => Task.FromResult<string?>(path);
        shell.ExportInstanceCommand.Execute(null); model.Configs = true; await model.PreviewExportCommand.ExecuteAsync(null);
        check(shell.IsTransferPage && !shell.IsStandardPage && model.HasPreview && model.Summary.Contains("Vanilla") && model.FileList.Any(x => x.Contains("config/test.json")), "instance Export command opens native preview with visible file inventory");
        check(shell.IsInstancesSection && window.GetVisualDescendants().OfType<Button>().Any(x => x.Content as string == "← Сборки" && x.Command == shell.NavigateCommand),
            "transfer keeps Instances navigation active and provides an explicit return action");
        var view = window.GetVisualDescendants().OfType<InstanceTransferView>().Single();
        foreach (var width in new[] { 900, 1060, 1920 })
        {
            window.Width = width; Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            check(view.Bounds.Width > window.Bounds.Width - 280 && view.GetVisualDescendants().OfType<ScrollViewer>().First().Extent.Width <= view.Bounds.Width + 1, "transfer page uses available content area without horizontal overflow at " + width);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root, "instance-export-" + width + ".png"), PngBitmapEncoderOptions.Default);
        }
        var css = QuickCssParser.Parse("#instance-transfer { background: #102030; }");
        check(css.Diagnostics.Count == 0, "transfer public Quick CSS selector is accepted");
        var themePath = Path.Combine(root, "transfer.css"); await File.WriteAllTextAsync(themePath, "#instance-transfer { background: #102030; }");
        using var theme = new QuickCssService(window); await theme.ConfigureAsync(new() { Enabled = true, FilePath = themePath, AutoReload = false }); Dispatcher.UIThread.RunJobs();
        check(view.GetVisualDescendants().OfType<Border>().Any(x => x.Classes.Contains("qc-instance-transfer") && x.Background?.ToString() == "#ff102030"), "transfer public Quick CSS selector overrides the existing dark card theme");
        await theme.ConfigureAsync(new() { Enabled = false, FilePath = themePath, AutoReload = false });
        model.Configs = false; check(!model.HasPreview && !model.ExportCommand.CanExecute(null), "changing export options invalidates stale preview and export action");
        model.Configs = true; await model.PreviewExportCommand.ExecuteAsync(null); await model.ExportCommand.ExecuteAsync(null);
        check(File.Exists(path) && !shell.HasError, "UI export creates a real validated package using the provided Save dialog result");
        shell.ImportInstanceCommand.Execute(null); await model.ChoosePackageCommand.ExecuteAsync(null);
        check(model.IsImport && model.HasPreview && !model.ImportCommand.CanExecute(null), "UI import preview blocks duplicate instance name until explicitly renamed");
        model.NewName = "UI imported"; await model.ImportCommand.ExecuteAsync(null);
        var configuration = await store.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        check(!model.HasPreview && !view.GetVisualDescendants().OfType<Expander>().Single().IsVisible,
            "completed import invalidates ready-to-install preview in the actual UI");
        check(!shell.HasError && shell.Instances.Count == 2 && configuration.Instances.Count == 2 && configuration.Instances.Any(x => x.Name == "UI imported"), "UI import transaction persists and selects a new instance through existing ConfigurationStore");
        check(accounts.ActiveAccountId == active && shell.SelectedInstance!.Id != instance.Id && instance.GameDirectory == configuration.Instances.Single(x => x.Id == instance.Id).GameDirectory, "instance transfer preserves active account and existing instance directories");
        shell.NavigateCommand.Execute("instances"); minecraft.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.ImportInstanceCommand.Execute(null); await model.ChoosePackageCommand.ExecuteAsync(null); model.NewName = "Cancelled UI import";
        var task = model.ImportCommand.ExecuteAsync(null); await Until(() => shell.IsWorking);
        check(shell.ShowProgress && shell.CanCancel && !shell.PrimaryCommand.CanExecute(null) && !model.ExportCommand.CanExecute(null), "transfer uses existing cancellable global operation gate and disables concurrent launch/export");
        shell.CancelCommand.Execute(null); await task;
        check(shell.Instances.Count == 2 && !shell.IsWorking && !Directory.EnumerateDirectories(shell.InstancesDirectory, ".nex-stage-*").Any(), "UI cancellation cleans staging and keeps saved instances intact");
        window.Close();
    }
    private static Task PublishNothing(GameInstance instance, CancellationToken token) => Task.CompletedTask;
    private static ModrinthVersion Version(string project, string id) => new()
    {
        Id = id, ProjectId = project, VersionNumber = id, VersionType = "release", GameVersions = ["1.21.1"], Loaders = ["fabric"], Environment = "client_and_server",
        Files = [new() { Filename = project + ".jar", Url = "https://cdn.modrinth.com/" + id, Hashes = Hashes(Jar), Size = Jar.Length, Primary = true }]
    };
    private static Dictionary<string, string> Hashes(byte[] data) => new() { ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(data)), ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(data)) };
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static byte[] MakeJar() { using var memory = new MemoryStream(); using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) using (var writer = new StreamWriter(zip.CreateEntry("fixture.txt").Open())) writer.Write("Fixture mod"); return memory.ToArray(); }
    private static async Task WritePackage(string path, InstanceManifest manifest, Dictionary<string, byte[]> files, bool link = false)
    {
        using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        await using (var entry = zip.CreateEntry(InstanceManifestService.EntryName).Open()) await JsonSerializer.SerializeAsync(entry, manifest, InstanceManifestService.Json);
        foreach (var (name, bytes) in files) { var entry = zip.CreateEntry(name); if (link) entry.ExternalAttributes = unchecked((int)0xA1FF0000u); await using var output = entry.Open(); await output.WriteAsync(bytes); }
    }
    private static async Task WriteRaw(string path, string json)
    { using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create); await using var entry = zip.CreateEntry(InstanceManifestService.EntryName).Open(); await entry.WriteAsync(Encoding.UTF8.GetBytes(json)); }
    private static async Task Reject<T>(Func<Task> action, Action<bool, string> check, string title) where T : Exception
    { try { await action(); } catch (T) { check(true, title); return; } throw new Exception("Expected " + typeof(T).Name + ": " + title); }
    private static async Task Until(Func<bool> predicate) { var until = DateTime.UtcNow.AddSeconds(10); while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(10); } }
    private sealed class ImmediateProgress(Action<LaunchProgress> action) : IProgress<LaunchProgress> { public void Report(LaunchProgress value) => action(value); }
    private sealed class FixtureApi : IModrinthService
    {
        public Dictionary<string, ModrinthVersion> Versions = new(); public Dictionary<string, ModrinthProject> Projects = new(); public bool Fail; public int LatestCalls;
        public void Add(params ModrinthVersion[] versions) { foreach (var v in versions) { Versions[v.Id] = v; Projects[v.ProjectId] = new() { Id = v.ProjectId, Title = v.ProjectId, ProjectType = "mod", ClientSide = "required" }; } }
        public Task<ModrinthVersion> VersionAsync(string id, CancellationToken token) { token.ThrowIfCancellationRequested(); return Fail ? Task.FromException<ModrinthVersion>(new IOException("Fixture network unavailable")) : Task.FromResult(Versions[id]); }
        public Task<ModrinthProject> ProjectAsync(string id, CancellationToken token) => Task.FromResult(Projects[id]);
        public Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string id, GameInstance? instance, CancellationToken token) { LatestCalls++; return Task.FromResult<IReadOnlyList<ModrinthVersion>>(Versions.Values.Where(x => x.ProjectId == id).Reverse().ToArray()); }
        public Task<ModrinthSearchResult> SearchAsync(string query, bool packs, GameInstance? instance, int offset, CancellationToken token, ModrinthSearchOptions? options = null) => Task.FromResult(new ModrinthSearchResult());
        public Task<ModrinthFilterCatalog> FilterCatalogAsync(CancellationToken token) => Task.FromResult(new ModrinthFilterCatalog([], []));
        public Task<IReadOnlyList<ModrinthTeamMember>> MembersAsync(string id, CancellationToken token) => Task.FromResult<IReadOnlyList<ModrinthTeamMember>>([]);
    }
    private sealed class FixtureHttp : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files = new(); public List<string> Agents = new(); public bool Corrupt; public CancellationTokenSource? Cancel;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Agents.Add(request.Headers.UserAgent.ToString()); Cancel?.Cancel(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? new byte[Jar.Length] : Files[request.RequestUri!.AbsoluteUri]) }); }
    }
    private sealed class FixtureMinecraft : IMinecraftService
    {
        public bool Fail; public int Installs; public TaskCompletionSource? Hold;
        public Task<IReadOnlyList<MinecraftRelease>> GetVersionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MinecraftRelease>>([new("1.21.1", "release", DateTimeOffset.UtcNow)]);
        public bool IsInstalled(GameInstance instance) => File.Exists(SafePaths.Resolve(instance.GameDirectory, ".fixture-installed"));
        public async Task InstallAsync(GameInstance instance, IProgress<LaunchProgress> progress, CancellationToken token) { Installs++; token.ThrowIfCancellationRequested(); if (Hold is not null) await Hold.Task.WaitAsync(token); await File.WriteAllTextAsync(SafePaths.Resolve(instance.GameDirectory, ".fixture-installed"), "verified", token); if (Fail) throw new IOException("Fixture installation failure"); }
        public Task<int> LaunchAsync(GameInstance instance, MSession session, IProgress<LaunchProgress> progress, Action<string> log, CancellationToken token) => throw new NotSupportedException();
    }
}
