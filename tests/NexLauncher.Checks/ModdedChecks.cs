using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CmlLib.Core.Auth;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Loaders;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.Storage;
using NexLauncher.ViewModels;

internal static class ModdedChecks
{
    private static readonly byte[] Jar = Encoding.UTF8.GetBytes("fixture jar bytes; never executed");
    private static Dictionary<string, string> Hashes(byte[] bytes) => new() { ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)), ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)) };
    private static GameInstance Instance(ModLoader loader = ModLoader.Fabric) => new() { VersionId = "1.21.1", Loader = loader, LoaderVersion = loader == ModLoader.Vanilla ? "" : "0.16.10", Name = "Тест" };
    private static ModrinthVersion Version(string project, string id, string[]? loaders = null, params ModrinthDependency[] deps) => new()
    {
        Id = id, ProjectId = project, Name = id, VersionNumber = id, GameVersions = ["1.21.1"], Loaders = loaders ?? ["fabric"], Environment = "client_and_server", Dependencies = deps.ToList(),
        DatePublished = DateTimeOffset.UtcNow, Files = [new() { Filename = project + ".jar", Url = "https://cdn.modrinth.com/" + id, Primary = true, Size = Jar.Length, Hashes = Hashes(Jar) }]
    };
    private static ModrinthDependency Dep(string? project, string? version = null, string type = "required") => new() { ProjectId = project, VersionId = version, DependencyType = type };
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "modded"); Directory.CreateDirectory(directory);
        await Storage(directory, check);
        Loaders(check);
        await Http(directory, check);
        await Dependencies(directory, check);
        await Packs(directory, check);
        await Ui(directory, check);
    }
    private static async Task Reject<T>(Func<Task> action, Action<bool, string> check, string name) where T : Exception
    { try { await action(); } catch (T) { check(true, name); return; } throw new Exception("Expected " + typeof(T).Name + ": " + name); }
    private static Task Do(Action action) { action(); return Task.CompletedTask; }

    private static async Task Storage(string root, Action<bool, string> check)
    {
        var store = new ConfigurationStore(Path.Combine(root, "settings"));
        Directory.CreateDirectory(store.DataDirectory);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(store.ConfigurationPath, "{\"Instances\":[{\"Id\":\"" + id + "\",\"Name\":\"Legacy\",\"VersionId\":\"1.21.1\"}]}");
        var old = await store.LoadAsync();
        var originalPath = Path.Combine(store.DataDirectory, "instances", id, "game");
        check(old.Instances.Single().Loader == ModLoader.Vanilla && old.Instances[0].GameDirectory == originalPath, "legacy Vanilla migration pins original game directory");
        var custom = await SafePaths.CheckWritableRootAsync(Path.Combine(root, "Другой диск с пробелами"), default);
        old.InstancesDirectory = custom;
        foreach (var type in Enum.GetValues<ModLoader>())
        {
            var instance = Instance(type); instance.GameDirectory = SafePaths.Resolve(custom, instance.Id + "/game"); old.Instances.Add(instance);
        }
        await store.SaveAsync(old); var restored = await store.LoadAsync();
        check(restored.Instances.Count == 5 && restored.Instances.Skip(1).Select(x => x.Loader).SequenceEqual(Enum.GetValues<ModLoader>()), "all four loader kinds persist without encoding them in folder names");
        check(restored.Instances.Skip(2).All(x => x.LoaderVersion == "0.16.10"), "loader version survives settings round trip");
        check(restored.InstancesDirectory == custom && restored.Instances[0].GameDirectory == originalPath && restored.Instances.Skip(1).All(x => x.GameDirectory.StartsWith(custom)), "root change retains old path and directs new instances to Unicode/spaced root");
        foreach (var bad in new[] { "../escape", "/absolute", "C:/absolute", "a/../../outside", "mods/CON.jar", "mods/x.jar:stream", "mods/x. ", "mods\\bad.jar" })
            await Reject<InvalidDataException>(() => Do(() => SafePaths.Resolve(custom, bad)), check, "path rejected: " + bad);
        await Reject<InvalidDataException>(() => SafePaths.CheckWritableRootAsync("relative/path", default), check, "relative instances root rejected");
        await Reject<InvalidDataException>(() => Do(() => SafePaths.LocalRoot("//localhost/share")), check, "forward-slash UNC cannot bypass local-root restriction");
        await Reject<InvalidDataException>(() => Do(() => SafePaths.Resolve(custom, "mods/COM¹.jar")), check, "Windows superscript device names cannot become download paths");
        var filePath = Path.Combine(root, "not-directory"); await File.WriteAllTextAsync(filePath, "keep");
        await Reject<IOException>(() => SafePaths.CheckWritableRootAsync(filePath, default), check, "unavailable root reports an error and preserves file");
        var unavailable = Enumerable.Range('D', 23).Select(x => (char)x + ":\\").FirstOrDefault(x => !Directory.Exists(x));
        if (unavailable is not null) await Reject<IOException>(() => SafePaths.CheckWritableRootAsync(unavailable + "NexLauncher", default), check, "unavailable drive rejected before creation");
        var incompatible = restored.Instances[1]; incompatible.FormatVersion = 999;
        await Reject<InvalidDataException>(() => store.SaveAsync(restored), check, "unknown instance format cannot overwrite settings");
        check(File.ReadAllText(filePath) == "keep", "root validation never deletes user files");
        await SafePaths.CheckTreeAsync(root, default);
        check(File.ReadAllText(filePath) == "keep", "preflight of existing game tree is read-only before CmlLib writes");
        await Reject<OperationCanceledException>(() => SafePaths.CheckTreeAsync(root, new CancellationToken(true)), check, "existing tree preflight supports cancellation");
    }
    private static void Loaders(Action<bool, string> check)
    {
        const string fabric = "[{\"loader\":{\"version\":\"0.16.10\",\"stable\":true}},{\"loader\":{\"version\":\"0.17.0-beta\",\"stable\":false}}]";
        var versions = LoaderCatalog.ParseVersions(ModLoader.Fabric, "1.21.1", fabric);
        check(versions.Count == 2 && versions[0].MinecraftVersion == "1.21.1" && !versions[1].Stable, "Fabric metadata resolves exact versions and stable flag");
        const string forge = "<metadata><versioning><versions><version>1.21.1-52.0.1</version><version>1.20.1-47.0.0</version><version>1.21.1-52.0.2</version></versions></versioning></metadata>";
        check(LoaderCatalog.ParseVersions(ModLoader.Forge, "1.21.1", forge).Select(x => x.Version).SequenceEqual(new[] { "52.0.2", "52.0.1" }), "Forge official Maven metadata filters Minecraft and preserves concrete loader versions");
        const string neo = "<metadata><versioning><versions><version>21.1.172</version><version>21.2.1-beta</version><version>26.1.0.10-beta</version><version>26.1.2.10</version></versions></versioning></metadata>";
        check(LoaderCatalog.ParseVersions(ModLoader.NeoForge, "1.21.1", neo).Single().Version == "21.1.172", "NeoForge metadata resolves legacy semver mapping");
        check(LoaderCatalog.ParseVersions(ModLoader.NeoForge, "26.1", neo).Single().Version == "26.1.0.10-beta" && LoaderCatalog.ParseVersions(ModLoader.NeoForge, "26.1.2", neo).Count == 1, "NeoForge metadata supports current four-part versioning");
        check(LoaderCatalog.ParseVersions(ModLoader.Forge, "1.12.2", forge).Count == 0 && LoaderCatalog.NeoMinecraftVersion("26.1.0.0-alpha.2+snapshot-1") is null, "unsupported legacy installer and unknown NeoForge version schemes fail closed");
        using var profile = JsonDocument.Parse("{\"id\":\"fabric-loader-0.16.10-1.21.1\",\"inheritsFrom\":\"1.21.1\"}");
        check(LoaderInstaller.ValidateProfile(profile.RootElement, "1.21.1").StartsWith("fabric-loader"), "loader profile preserves vanilla inheritance");
    }
    private static async Task Http(string root, Action<bool, string> check)
    {
        var transport = new FakeHttp(); var client = new LauncherHttp(new HttpClient(transport));
        transport.Bytes["https://cdn.modrinth.com/file"] = Jar;
        var target = Path.Combine(root, "download", "mod.jar");
        await client.DownloadAsync("https://cdn.modrinth.com/file", target, Hashes(Jar), Jar.Length, ["cdn.modrinth.com"], new Progress<LaunchProgress>(), default);
        check(File.ReadAllBytes(target).SequenceEqual(Jar) && !Directory.GetFiles(Path.GetDirectoryName(target)!, "*.part").Any(), "download hashes verified before atomic publication");
        await Reject<InvalidDataException>(() => client.DownloadAsync("https://cdn.modrinth.com/file", target + ".bad", Hashes([1, 2]), Jar.Length, ["cdn.modrinth.com"], new Progress<LaunchProgress>(), default), check, "corrupted download rejected");
        check(!File.Exists(target + ".bad") && !Directory.GetFiles(Path.GetDirectoryName(target)!, "*.part").Any(), "failed download leaves neither installed JAR nor partial file");
        await Reject<OperationCanceledException>(() => client.DownloadAsync("https://cdn.modrinth.com/file", target + ".cancel", Hashes(Jar), Jar.Length, ["cdn.modrinth.com"], new Progress<LaunchProgress>(), new CancellationToken(true)), check, "download cancellation propagates");
        check(transport.Agents.All(x => x == LauncherHttp.UserAgent), "every HTTP request sends uniquely identifying NexLauncher User-Agent");
        foreach (var loader in new[] { ModLoader.Fabric, ModLoader.Forge, ModLoader.NeoForge })
        {
            var path = Uri.UnescapeDataString(ModrinthService.SearchPath("hello & world", false, Instance(loader), 20));
            check(path.Contains("versions:1.21.1") && path.Contains("categories:" + loader.ToString().ToLowerInvariant()) && path.Contains("offset=20") && path.Contains("project_type:mod"), "Modrinth search facets/pagination for " + loader);
        }
        check(Uri.UnescapeDataString(ModrinthService.SearchPath("", true, null, 0)).Contains("project_type:modpack"), "modpack search is independent of an instance");
        await Reject<InvalidOperationException>(() => Do(() => ModrinthService.SearchPath("", false, Instance(ModLoader.Vanilla), 0)), check, "Vanilla cannot silently receive loader mods");
        foreach (var url in new[] { "http://cdn.modrinth.com/a", "https://127.0.0.1/a", "https://evil.test/a", "https://user@cdn.modrinth.com/a", "https://cdn.modrinth.com:444/a" })
            await Reject<InvalidDataException>(() => Do(() => LauncherHttp.ValidateUrl(url, LauncherHttp.PackHosts)), check, "download URL rejected: " + url);
        transport.Reply = _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.test/file") } };
        await Reject<InvalidDataException>(() => client.GetBytesAsync("https://cdn.modrinth.com/redirect", ["cdn.modrinth.com"], default), check, "redirect cannot escape download host allowlist");
        var rateCalls = 0;
        transport.Reply = _ => { rateCalls++; var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new(System.TimeSpan.FromSeconds(120)); return r; };
        await Reject<IOException>(() => client.GetBytesAsync("https://api.modrinth.com/v2/search", ["api.modrinth.com"], default), check, "429 retry-after is respected without unbounded retries");
        check(rateCalls == 1, "long rate-limit backoff fails with actionable retry-later message");
        var resetCalls = 0;
        transport.Reply = _ => { resetCalls++; var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }; r.Headers.Add("X-Ratelimit-Remaining", "0"); r.Headers.Add("X-Ratelimit-Reset", "120"); return r; };
        await client.GetBytesAsync("https://api.modrinth.com/v2/test-header", ["api.modrinth.com"], default);
        await Reject<IOException>(() => client.GetBytesAsync("https://api.modrinth.com/v2/next-header", ["api.modrinth.com"], default), check, "API remaining/reset headers throttle later requests");
        check(resetCalls == 1, "exhausted rate-limit window issues no extra request");
        client = new LauncherHttp(new HttpClient(transport));
        var cacheCalls = 0;
        transport.Reply = _ => { cacheCalls++; return new(HttpStatusCode.OK) { Content = new StringContent("{}") }; };
        await client.GetBytesAsync("https://api.modrinth.com/v2/cached", ["api.modrinth.com"], default);
        await client.GetBytesAsync("https://api.modrinth.com/v2/cached", ["api.modrinth.com"], default);
        check(cacheCalls == 1, "short metadata cache avoids duplicate requests");
        var failedCalls = 0;
        transport.Reply = _ => { failedCalls++; var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); r.Headers.RetryAfter = new(TimeSpan.Zero); return r; };
        await Reject<IOException>(() => client.GetBytesAsync("https://api.modrinth.com/v2/unavailable", ["api.modrinth.com"], default), check, "5xx retries end in a clear network error");
        check(failedCalls == 3, "GET retries are bounded to three attempts");
        transport.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("broken json") };
        await Reject<InvalidDataException>(() => new ModrinthService(client).VersionAsync("version1", default), check, "invalid API JSON handled safely");
    }
    private static async Task Dependencies(string root, Action<bool, string> check)
    {
        var api = new FakeApi(); var main = Version("mainproj", "mainver1", null, Dep("required"), Dep("optional", type: "optional"), Dep("embedded", type: "embedded"));
        var dependency = Version("required", "reqver01"); api.Add(main, dependency);
        var instance = Instance(); var resolver = new DependencyResolver(api);
        var plan = await resolver.ResolveAsync(instance, main.Id, new(), default);
        check(plan.Projects.Count == 2 && plan.Notices.Count == 2 && !api.Requested.Contains("optional") && !api.Requested.Contains("embedded"), "required installs; optional is visible and embedded is not downloaded twice");
        dependency.Dependencies.Add(Dep("mainproj", main.Id)); main.Dependencies.Add(Dep("required"));
        plan = await resolver.ResolveAsync(instance, main.Id, new(), default);
        check(plan.Projects.Count == 2, "dependency cycles terminate and duplicate dependencies install once");
        dependency.Dependencies.Clear(); main.Dependencies.Add(Dep("required", type: "incompatible"));
        await Reject<InvalidOperationException>(() => resolver.ResolveAsync(instance, main.Id, new(), default), check, "incompatible dependency blocks installation before mutation");
        main.Dependencies.RemoveAt(main.Dependencies.Count - 1);
        dependency.Loaders = ["forge"];
        await Reject<InvalidOperationException>(() => resolver.ResolveAsync(instance, main.Id, new(), default), check, "required dependency must match selected loader");
        dependency.Loaders = ["fabric"]; dependency.GameVersions = ["1.20.1"];
        await Reject<InvalidOperationException>(() => resolver.ResolveAsync(instance, main.Id, new(), default), check, "required dependency must match Minecraft version");
        dependency.GameVersions = ["1.21.1"]; dependency.Environment = "dedicated_server_only";
        await Reject<InvalidOperationException>(() => resolver.ResolveAsync(instance, main.Id, new(), default), check, "server-only version cannot install into a client instance");
        dependency.Environment = "client_and_server";
        foreach (var loader in new[] { ModLoader.Fabric, ModLoader.Forge, ModLoader.NeoForge })
        {
            var matching = Version("compat", "compat01", [loader.ToString().ToLowerInvariant()]);
            check(ModrinthService.IsCompatible(matching, Instance(loader)) && !ModrinthService.IsCompatible(matching, Instance(ModLoader.Vanilla)), "concrete version validation supports " + loader + " while refusing Vanilla");
        }
        var wrongPinned = Version("different", "wrongpin"); api.Add(wrongPinned);
        main.Dependencies.Add(Dep("required", wrongPinned.Id));
        await Reject<InvalidDataException>(() => resolver.ResolveAsync(instance, main.Id, new(), default), check, "pinned dependency project/version mismatch rejected");
        main.Dependencies.RemoveAt(main.Dependencies.Count - 1);
        var req2 = Version("required", "reqver02"); api.Add(req2);
        main.Dependencies.Add(Dep("required", req2.Id)); main.Dependencies.Add(Dep("required", dependency.Id));
        await Reject<InvalidOperationException>(() => resolver.ResolveAsync(instance, main.Id, new(), default), check, "conflicting pinned dependency versions fail before writes");
        main.Dependencies.RemoveRange(main.Dependencies.Count - 2, 2);
        var transport = new FakeHttp(); transport.Bytes[main.Files[0].Url] = Jar; transport.Bytes[dependency.Files[0].Url] = Jar;
        var manager = new ModManager(api, new LauncherHttp(new HttpClient(transport)));
        var game = Path.Combine(root, "mods-game"); Directory.CreateDirectory(Path.Combine(game, "mods"));
        await File.WriteAllTextAsync(Path.Combine(game, "mods", "manual.jar"), "user mod");
        await manager.InstallAsync(instance, game, main.Id, new Progress<LaunchProgress>(), default);
        var saved = await manager.LoadAsync(game, default);
        check(saved.Projects.Count == 2 && saved.Projects.Any(x => x.Explicit) && File.ReadAllText(Path.Combine(game, "mods", "manual.jar")) == "user mod", "install persists stable project/version IDs and preserves unknown manual JAR");
        await Reject<InvalidOperationException>(() => manager.RemoveAsync(game, dependency.ProjectId, default), check, "shared required dependency cannot be removed");
        var next = Version("mainproj", "mainver2", null, Dep("required")); api.Add(next); transport.Bytes[next.Files[0].Url] = Jar;
        await manager.InstallAsync(instance, game, next.Id, new Progress<LaunchProgress>(), default);
        check((await manager.LoadAsync(game, default)).Projects.Single(x => x.Explicit).Version.Id == next.Id, "explicit update replaces owned mod and atomically records new version");
        await manager.RemoveAsync(game, main.ProjectId, default);
        check((await manager.LoadAsync(game, default)).Projects.Count == 1 && File.Exists(Path.Combine(game, "mods", dependency.Files[0].Filename)), "removal retains dependencies for other projects or explicit later removal");
        await File.WriteAllTextAsync(Path.Combine(game, "mods", dependency.Files[0].Filename), "user modified");
        await Reject<IOException>(() => manager.RemoveAsync(game, dependency.ProjectId, default), check, "manually changed managed JAR is not destroyed");
        var failedGame = Path.Combine(root, "failed-mod");
        transport.Bytes[dependency.Files[0].Url] = [1, 2, 3];
        await Reject<InvalidDataException>(() => manager.InstallAsync(instance, failedGame, main.Id, new Progress<LaunchProgress>(), default), check, "failed dependency download rolls back staged install");
        check(!File.Exists(ModManager.MetadataPath(failedGame)) && !Directory.Exists(Path.Combine(failedGame, "mods")), "failed install does not publish any mod or success metadata");
        transport.Bytes[dependency.Files[0].Url] = Jar;
        var collisionGame = Path.Combine(root, "collision-mod"); Directory.CreateDirectory(Path.Combine(collisionGame, "mods"));
        await File.WriteAllTextAsync(Path.Combine(collisionGame, "mods", main.Files[0].Filename), "manual collision");
        await Reject<IOException>(() => manager.InstallAsync(instance, collisionGame, main.Id, new Progress<LaunchProgress>(), default), check, "managed download never overwrites colliding manual filename");
        check(File.ReadAllText(Path.Combine(collisionGame, "mods", main.Files[0].Filename)) == "manual collision", "manual filename collision keeps original bytes");
        var interruptedGame = Path.Combine(root, "journal-game"); Directory.CreateDirectory(Path.Combine(interruptedGame, ".nexlauncher"));
        await File.WriteAllTextAsync(Path.Combine(interruptedGame, ".nexlauncher", "transaction.json"), "{\"pending\":true}");
        await Reject<InvalidDataException>(() => manager.LoadAsync(interruptedGame, default), check, "crash journal blocks further writes until recovery");
        var corrupt = ModManager.MetadataPath(game); await File.WriteAllTextAsync(corrupt, "{bad");
        await Reject<InvalidDataException>(() => manager.RemoveAsync(game, dependency.ProjectId, default), check, "corrupt mod metadata is never silently replaced");
        check(File.ReadAllText(corrupt) == "{bad", "corrupt original metadata preserved");
    }

    private static byte[] Pack(string? malicious = null, string loader = "fabric-loader")
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("modrinth.index.json");
            using (var stream = entry.Open()) JsonSerializer.Serialize(stream, new { formatVersion = 1, game = "minecraft", versionId = "packver", name = "Тест pack", dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1", [loader] = "0.16.10" }, files = new[] {
                new { path = "mods/main.jar", hashes = Hashes(Jar), downloads = new[] { "https://cdn.modrinth.com/packmod" }, fileSize = Jar.Length, env = new { client = "required", server = "required" } },
                new { path = "mods/optional.jar", hashes = Hashes(Jar), downloads = new[] { "https://cdn.modrinth.com/packmod" }, fileSize = Jar.Length, env = new { client = "optional", server = "required" } },
                new { path = "mods/server.jar", hashes = Hashes(Jar), downloads = new[] { "https://cdn.modrinth.com/packmod" }, fileSize = Jar.Length, env = new { client = "unsupported", server = "required" } }
            } });
            using (var writer = new StreamWriter(zip.CreateEntry("overrides/config/test.txt").Open())) writer.Write("base");
            using (var writer = new StreamWriter(zip.CreateEntry("client-overrides/config/test.txt").Open())) writer.Write("client");
            using (var writer = new StreamWriter(zip.CreateEntry("server-overrides/config/test.txt").Open())) writer.Write("server");
            if (malicious is not null) using (var writer = new StreamWriter(zip.CreateEntry(malicious).Open())) writer.Write("unsafe");
        }
        return memory.ToArray();
    }
    private static async Task Packs(string root, Action<bool, string> check)
    {
        using (var memory = new MemoryStream(Pack())) using (var zip = new ZipArchive(memory))
        {
            var plan = MrPackPlanner.Read(zip, root);
            check(plan.Instance.Loader == ModLoader.Fabric && plan.Instance.VersionId == "1.21.1" && plan.Files.Count == 1 && plan.OptionalFiles.Count == 1, "mrpack parser resolves loader and honors required/optional/unsupported client environment");
            check(plan.Overrides.Single().Entry.StartsWith("client-overrides/"), "client overrides replace common overrides; server layer excluded");
        }
        foreach (var path in new[] { "../outside", "overrides/../../escape", "overrides/C:/outside", "overrides/.nexlauncher/mods.json", "overrides/versions/evil.json", "overrides/start.cmd" })
            await Reject<InvalidDataException>(() => Do(() => { using var memory = new MemoryStream(Pack(path)); using var zip = new ZipArchive(memory); MrPackPlanner.Read(zip, root); }), check, "malicious archive entry rejected: " + path);
        await Reject<InvalidDataException>(() => Do(() => { using var memory = new MemoryStream(Pack(loader: "quilt-loader")); using var zip = new ZipArchive(memory); MrPackPlanner.Read(zip, root); }), check, "unsupported pack loader fails explicitly");
        var api = new FakeApi(); var version = Version("packproj", "packver1"); var bytes = Pack();
        version.Files = [new() { Filename = "test.mrpack", Url = "https://cdn.modrinth.com/pack", Hashes = Hashes(bytes), Size = bytes.Length }];
        api.Add(version); api.Projects["packproj"].ProjectType = "modpack";
        var transport = new FakeHttp(); transport.Bytes[version.Files[0].Url] = bytes; transport.Bytes["https://cdn.modrinth.com/packmod"] = Jar;
        var minecraft = new FakeMinecraft(); var installer = new ModpackInstaller(api, new LauncherHttp(new HttpClient(transport)), minecraft);
        var destination = Path.Combine(root, "pack-root"); GameInstance? published = null;
        await installer.InstallAsync("packproj", "packver1", "Проверенная сборка", destination, false, (instance, _) => { published = instance; return Task.CompletedTask; }, new Progress<LaunchProgress>(), default);
        check(published is { Loader: ModLoader.Fabric, ModrinthProjectId: "packproj", ModrinthVersionId: "packver1" } && minecraft.Installs == 1, "pack installs through same loader pipeline then publishes normal instance");
        check(File.ReadAllText(Path.Combine(published!.GameDirectory, "config", "test.txt")) == "client" && !File.Exists(Path.Combine(published.GameDirectory, "mods", "server.jar")) && !File.Exists(Path.Combine(published.GameDirectory, "mods", "optional.jar")), "pack client overrides and explicit optional choice reach installed files");
        var before = Directory.GetDirectories(destination).Length; minecraft.Fail = true;
        await Reject<IOException>(() => installer.InstallAsync("packproj", "packver1", "Failed", destination, true, (_, _) => throw new Exception("must not publish"), new Progress<LaunchProgress>(), default), check, "failed loader install prevents modpack publication");
        check(Directory.GetDirectories(destination).Length == before, "failed pack staging is removed without touching existing instance");
        minecraft.Fail = false;
        await Reject<IOException>(() => installer.InstallAsync("packproj", "packver1", "Failed save", destination, false, (_, _) => throw new IOException("disk full"), new Progress<LaunchProgress>(), default), check, "failed config publication rolls back completed pack directory");
        check(Directory.GetDirectories(destination).Length == before, "pack publication failure does not leave installed orphan");
        await Reject<OperationCanceledException>(() => installer.InstallAsync("packproj", "packver1", "Cancelled", destination, false, (_, _) => Task.CompletedTask, new Progress<LaunchProgress>(), new CancellationToken(true)), check, "pack cancellation prevents publication");
    }
    private static async Task Ui(string root, Action<bool, string> check)
    {
        var api = new FakeApi(); api.Add(Version("mainproj", "mainver1", ["neoforge"]));
        var store = new ConfigurationStore(Path.Combine(root, "ui"));
        using var account = new DisposableAccount();
        var vm = new MainWindowViewModel(store, new FakeMinecraft(), account.Service, new FakeCatalog(), api);
        await vm.InitializeAsync();
        var newRoot = Path.Combine(root, "UI custom root сборки");
        vm.PickInstancesFolderAsync = () => Task.FromResult<string?>(newRoot);
        await vm.ChooseInstancesFolderCommand.ExecuteAsync(null);
        check((await store.LoadAsync()).InstancesDirectory == newRoot, "folder picker selection validates and persists custom root");
        foreach (var loader in Enum.GetValues<ModLoader>())
        {
            vm.LoaderOptions.Loader = loader;
            await Until(() => vm.LoaderOptions.CanCreate);
            vm.NewInstanceName = loader.ToString();
            await vm.CreateInstanceCommand.ExecuteAsync(null);
            check(vm.SelectedInstance?.Loader == loader && vm.ErrorMessage.Length == 0, "UI creates independent " + loader + " instance");
            check(vm.SelectedInstance!.GameDirectory.StartsWith(newRoot), "new " + loader + " UI instance uses selected root");
        }
        vm.CurrentPage = "mods";
        await Until(() => vm.Catalog.Results.Count > 0 && !vm.Catalog.IsBusy);
        var queries = api.SearchCalls;
        vm.Catalog.Query = "a"; vm.Catalog.Query = "ab"; vm.Catalog.Query = "abc";
        await Until(() => !vm.Catalog.IsBusy);
        check(api.SearchCalls == queries + 1, "search input debounce sends only the final query");
        vm.Catalog.SelectedResult = vm.Catalog.Results[0];
        await Until(() => vm.Catalog.SelectedVersion is not null);
        check(api.LastSearchInstance?.Loader == ModLoader.NeoForge && vm.Catalog.IsMods, "instance Mods UI automatically supplies Minecraft and loader context");
        var window = new MainWindow(vm); window.Show(); Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame()) { check(frame is not null, "Modrinth UI renders with existing Avalonia theme"); frame!.Save(Path.Combine(root, "modrinth.png"), PngBitmapEncoderOptions.Default); }
        vm.CurrentPage = "instances"; Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(root, "loader-creation.png"), PngBitmapEncoderOptions.Default);
        window.Close();
    }
    private static async Task Until(Func<bool> condition)
    { var until = DateTime.UtcNow.AddSeconds(5); while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(10); } }

    public static async Task LiveAsync(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var catalog = new LoaderCatalog(LauncherHttp.Shared);
        foreach (var loader in new[] { ModLoader.Fabric, ModLoader.Forge, ModLoader.NeoForge })
            check((await catalog.GetVersionsAsync(loader, "1.21.1", timeout.Token)).Count > 0, "live official loader metadata: " + loader);
        var api = new ModrinthService(LauncherHttp.Shared);
        var result = await api.SearchAsync("fabric api", false, Instance(), 0, timeout.Token);
        check(result.Hits.Count > 0, "live Modrinth public API search with NexLauncher User-Agent");
        var project = await api.ProjectAsync(result.Hits[0].ProjectId, timeout.Token);
        var members = await api.MembersAsync(project.Id, timeout.Token);
        check(members.Count > 0 && members.All(x => !string.IsNullOrWhiteSpace(x.User.Username)), "live official Modrinth team attribution is available without authentication");
        var description = await Task.Run(() => ProjectDescriptionParser.Parse(project.Body, timeout.Token), timeout.Token);
        check(description.Blocks.Count > 0 && project.Downloads > 0, "live project full description and details metadata parse into safe render model");
        var versions = await api.VersionsAsync(project.Id, Instance(), timeout.Token);
        check(versions.Count > 0, "live Modrinth versions filtered for Minecraft and Fabric");
        var version = await api.VersionAsync(versions[0].Id, timeout.Token);
        ModrinthService.ValidateCompatibility(version, project, Instance());
        check(ModrinthService.PrimaryFile(version, ".jar").Hashes.Count > 0, "live version environment and file hashes satisfy installation policy");
    }

    public static async Task InstallLiveAsync(Action<bool, string> check)
    {
        // Explicit opt-in: official client installers execute only in this isolated .artifacts directory.
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "loader-smoke"));
        var seed = Environment.GetEnvironmentVariable("NEXLAUNCHER_SMOKE_SEED");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        foreach (var (loader, loaderVersion, id) in new[] {
            (ModLoader.Fabric, "0.16.10", "11111111111111111111111111111111"),
            (ModLoader.Forge, "52.0.28", "22222222222222222222222222222222"),
            (ModLoader.NeoForge, "21.1.172", "33333333333333333333333333333333") })
        {
            var instance = Instance(loader); instance.Id = id; instance.LoaderVersion = loaderVersion;
            instance.GameDirectory = SafePaths.Resolve(Path.Combine(directory, "instances"), id + "/game");
            if (!Directory.Exists(instance.GameDirectory) && !string.IsNullOrEmpty(seed))
            {
                await Task.Run(() =>
                {
                    SafePaths.NoLinks(seed); Directory.CreateDirectory(instance.GameDirectory);
                    foreach (var file in Directory.EnumerateFiles(seed, "*", SearchOption.AllDirectories))
                    {
                        timeout.Token.ThrowIfCancellationRequested(); SafePaths.NoLinks(file);
                        var target = SafePaths.Resolve(instance.GameDirectory, Path.GetRelativePath(seed, file).Replace('\\', '/'));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
                    }
                }, timeout.Token);
            }
            var service = new MinecraftService(directory);
            var last = DateTime.UtcNow.AddDays(-1);
            var progress = new Progress<LaunchProgress>(value => { if ((DateTime.UtcNow - last).TotalSeconds > 10) { Console.WriteLine("LOADER INSTALL " + loader + ": " + value.Message); last = DateTime.UtcNow; } });
            await service.InstallAsync(instance, progress, timeout.Token);
            check(service.IsInstalled(instance), "real " + loader + " official installation completes and writes success marker");
            var marker = await File.ReadAllTextAsync(Path.Combine(instance.GameDirectory, ".nexlauncher-installed"));
            var launchId = marker.Split('|')[3];
            var launcher = new CmlLib.Core.MinecraftLauncher(new CmlLib.Core.MinecraftPath(instance.GameDirectory));
            await launcher.GetAllVersionsAsync(timeout.Token);
            var java = launcher.GetJavaPath(await launcher.GetVersionAsync(instance.VersionId, timeout.Token));
            using var process = await launcher.BuildProcessAsync(launchId, new CmlLib.Core.ProcessBuilder.MLaunchOption
            { Session = LocalAccountIdentity.CreateSession("LoaderCheck"), MaximumRamMb = 2048, JavaPath = java }, timeout.Token);
            check(File.Exists(process.StartInfo.FileName) && process.StartInfo.Arguments.Contains("LoaderCheck"), "real " + loader + " CmlLib launch process resolves loader profile, Java and existing session");
            check(java is not null && java.Contains("java-runtime-delta"), "Minecraft 1.21.1 " + loader + " uses its Mojang Java 21 runtime");
            Console.WriteLine("LOADER PROFILE " + loader + ": " + launchId);
        }
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Bytes { get; } = new();
        public List<string> Agents { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Agents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(Reply?.Invoke(request) ?? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes[request.RequestUri!.AbsoluteUri]) });
        }
    }
    private sealed class FakeApi : IModrinthService
    {
        public Dictionary<string, ModrinthProject> Projects { get; } = new();
        public Dictionary<string, ModrinthVersion> Versions { get; } = new();
        public List<string> Requested { get; } = new();
        public GameInstance? LastSearchInstance;
        public int SearchCalls;
        public void Add(params ModrinthVersion[] versions)
        { foreach (var v in versions) { Versions[v.Id] = v; Projects[v.ProjectId] = new() { Id = v.ProjectId, Title = v.ProjectId, ProjectType = "mod", ClientSide = "required", Description = "Fixture project", Body = "A safe fixture description." }; } }
        public Task<ModrinthProject> ProjectAsync(string id, CancellationToken token) { Requested.Add(id); return Task.FromResult(Projects[id]); }
        public Task<IReadOnlyList<ModrinthTeamMember>> MembersAsync(string projectId, CancellationToken token) => Task.FromResult<IReadOnlyList<ModrinthTeamMember>>([]);
        public Task<ModrinthVersion> VersionAsync(string id, CancellationToken token) { Requested.Add(id); return Task.FromResult(Versions[id]); }
        public Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string projectId, GameInstance? instance, CancellationToken token) => Task.FromResult<IReadOnlyList<ModrinthVersion>>(Versions.Values.Where(x => x.ProjectId == projectId).ToArray());
        public Task<ModrinthFilterCatalog> FilterCatalogAsync(CancellationToken token) => Task.FromResult(new ModrinthFilterCatalog([], []));
        public Task<ModrinthSearchResult> SearchAsync(string query, bool packs, GameInstance? instance, int offset, CancellationToken token, ModrinthSearchOptions? options = null)
        { SearchCalls++; LastSearchInstance = instance; return Task.FromResult(new ModrinthSearchResult { Hits = Projects.Values.Select(x => new ModrinthHit { ProjectId = x.Id, Title = x.Title, Description = x.Description, Author = "Fixture author", ProjectType = x.ProjectType, Versions = ["1.21.1"], Categories = ["neoforge"], Downloads = 1200 }).ToList(), TotalHits = Projects.Count }); }
    }
    private sealed class FakeCatalog : ILoaderCatalog
    {
        public Task<IReadOnlyList<LoaderRelease>> GetVersionsAsync(ModLoader loader, string minecraft, CancellationToken token) => Task.FromResult<IReadOnlyList<LoaderRelease>>([new("0.16.10", minecraft)]);
    }
    private sealed class FakeMinecraft : IMinecraftService
    {
        public bool Fail;
        public int Installs;
        public Task<IReadOnlyList<MinecraftRelease>> GetVersionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MinecraftRelease>>([new("1.21.1", "release", DateTimeOffset.UtcNow)]);
        public bool IsInstalled(GameInstance instance) => false;
        public Task InstallAsync(GameInstance instance, IProgress<LaunchProgress> progress, CancellationToken cancellationToken) { Installs++; cancellationToken.ThrowIfCancellationRequested(); if (Fail) throw new IOException("fixture install failure"); return Task.CompletedTask; }
        public Task<int> LaunchAsync(GameInstance instance, MSession session, IProgress<LaunchProgress> progress, Action<string> log, CancellationToken cancellationToken) => Task.FromResult(0);
    }
    private sealed class DisposableAccount : IDisposable
    {
        public AccountService Service { get; } = new(Path.Combine(Path.GetTempPath(), "NexLauncherChecks", Guid.NewGuid().ToString("N")));
        public void Dispose() { }
    }
}
