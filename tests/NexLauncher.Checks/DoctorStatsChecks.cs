using System.Diagnostics;
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
using NexLauncher.Services.Diagnostics;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.Network;
using NexLauncher.Services.QuickCss;
using NexLauncher.Services.Sessions;
using NexLauncher.Services.Storage;
using NexLauncher.ViewModels;
using NexLauncher.Views;

internal static class DoctorStatsChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "insights-tests"); Directory.CreateDirectory(directory);
        await Storage(directory, check); Analytics(check); Performance(check); Crash(check);
        await Health(directory, check); await Lifecycle(directory, check); await Ui(directory, check);
    }
    private static GameSession Session(string instance, double seconds, SessionOutcome outcome = SessionOutcome.Completed, int day = 1) => new()
    {
        InstanceId = instance, InstanceName = "Мир", MinecraftVersion = "1.21.1", Loader = ModLoader.Fabric,
        AttemptedAt = new DateTimeOffset(2026, 1, day, 12, 0, 0, TimeSpan.Zero), StartedAt = new DateTimeOffset(2026, 1, day, 12, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 1, day, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds),
        LastObservedAt = new DateTimeOffset(2026, 1, day, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds), DurationSeconds = seconds, Outcome = outcome, ExitCode = outcome == SessionOutcome.Crashed ? 1 : 0
    };
    private static async Task Reject<T>(Func<Task> action, Action<bool, string> check, string name) where T : Exception
    { try { await action(); } catch (T) { check(true, name); return; } throw new Exception("Expected " + typeof(T).Name + ": " + name); }
    private static async Task Storage(string root, Action<bool, string> check)
    {
        var data = Path.Combine(root, "История с пробелами"); var store = new SessionStore(data);
        check((await store.SessionsAsync()).Length == 0, "NexStats never invents sessions on first start");
        var id = Guid.NewGuid().ToString("N"); var s = Session(id, 120);
        await store.SaveAsync(s); await store.SaveAsync(s);
        check((await store.SessionsAsync()).Length == 1, "session ID prevents double counting writes");
        var loaded = (await new SessionStore(data).SessionsAsync()).Single();
        check(loaded.InstanceId == id && loaded.DurationSeconds == 120 && loaded.Outcome == SessionOutcome.Completed, "session persistence survives a fresh store/restart");
        loaded.InstanceName = "Переименовано"; await store.SaveAsync(loaded);
        check((await store.SessionsAsync(id)).Length == 1, "instance rename retains stable ID history");
        var moved = loaded.Copy(); moved.InstanceId = Guid.NewGuid().ToString("N");
        await Reject<InvalidDataException>(() => store.SaveAsync(moved), check, "a session cannot be reassigned to another instance under the same ID");
        s.SchemaVersion = 0; await store.SaveAsync(s);
        check((await store.SessionsAsync())[0].SchemaVersion == 1, "version zero session migrates without fabricated metrics");
        s.SchemaVersion = 99; await Reject<InvalidDataException>(() => store.SaveAsync(s), check, "unknown session schema rejected"); s.SchemaVersion = 1;
        s.Id = "../escape"; await Reject<InvalidDataException>(() => store.SaveAsync(s), check, "history path traversal ID rejected"); s.Id = loaded.Id;
        s.DurationSeconds = double.NaN; await Reject<InvalidDataException>(() => store.SaveAsync(s), check, "non-finite duration cannot reach persisted statistics"); s.DurationSeconds = 120;
        var invalidTime = loaded.Copy(); invalidTime.StartedAt = DateTimeOffset.MaxValue;
        await Reject<InvalidDataException>(() => store.SaveAsync(invalidTime), check, "out-of-range session interval cannot break date aggregation");
        var invalidProcess = loaded.Copy(); invalidProcess.StartedAt = null; invalidProcess.ProcessStartedAt = DateTimeOffset.UtcNow; invalidProcess.ProcessId = 7;
        await Reject<InvalidDataException>(() => store.SaveAsync(invalidProcess), check, "recovery cannot attach a process without a recorded game start");
        s.Samples = Enumerable.Range(0, PerformanceAccumulator.MaximumSamples + 1).Select(x => new PerformanceSample(x, 1, null)).ToList();
        await Reject<InvalidDataException>(() => store.SaveAsync(s), check, "oversized performance history rejected"); s.Samples.Clear();
        var corruptId = Guid.NewGuid().ToString("N"); var corrupt = Path.Combine(data, "insights", "sessions", corruptId + ".json"); await File.WriteAllTextAsync(corrupt, "{broken");
        var tolerant = new SessionStore(data); check((await tolerant.SessionsAsync()).Length == 1 && tolerant.Warning.Length > 0, "corrupt record cannot erase healthy history");
        check(await File.ReadAllTextAsync(corrupt) == "{broken", "corrupt record retained for recovery");
        var sampled = loaded.Copy(); sampled.Samples = [new(1, 1048576, null)]; sampled.SampleCount = 1; await store.SaveAsync(sampled);
        check((await store.SessionsAsync()).Single().Samples.Count == 0 && (await store.SessionAsync(sampled.Id))!.Samples.Count == 1, "bulk analytics reads only summaries and loads samples lazily by session ID");
        var existing = Path.Combine(data, "insights", "sessions", sampled.Id + ".json"); var backup = await File.ReadAllTextAsync(existing); await File.WriteAllTextAsync(existing, "{broken");
        await Reject<InvalidDataException>(() => store.SaveAsync(sampled), check, "later corruption cannot be silently overwritten by a checkpoint");
        check(await File.ReadAllTextAsync(existing) == "{broken", "checkpoint leaves damaged original unchanged"); await File.WriteAllTextAsync(existing, backup);
        await Reject<OperationCanceledException>(() => tolerant.SessionsAsync(token: new(true)), check, "history queries honor cancellation");
        var report = new DoctorReport { InstanceId = id, SessionId = s.Id, Issues = CrashRules.Analyze("java.lang.OutOfMemoryError", true) };
        await store.SaveReportAsync(report); check((await new SessionStore(data).ReportsAsync()).Single().SessionId == s.Id, "diagnostic-session link survives restart");
        await tolerant.ClearAsync(default); check((await tolerant.SessionsAsync()).Length == 0 && File.Exists(corrupt), "clear deletes only validated history, keeps damaged originals");
        var retained = new SessionStore(Path.Combine(root, "retention"));
        for (var i = 0; i <= SessionStore.MaximumReports; i++)
            await retained.SaveReportAsync(new() { InstanceId = id, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i) });
        check((await retained.ReportsAsync()).Length == SessionStore.MaximumReports, "Doctor history retention bounds the number of saved reports");
        var cfg = new ConfigurationStore(Path.Combine(root, "preferences")); await cfg.SaveAsync(new() { MonitorPerformance = true });
        check((await cfg.LoadAsync()).MonitorPerformance, "optional sampling preference persists");
        await File.WriteAllTextAsync(cfg.ConfigurationPath, "{}"); check(!(await cfg.LoadAsync()).MonitorPerformance, "old settings default to sampling disabled");
    }
    private static void Analytics(Action<bool, string> check)
    {
        var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var rows = new[] { Session(a, 3600), Session(a, 1800, SessionOutcome.Crashed, 2), Session(b, 7200, day: 3), Session(b, 0, SessionOutcome.FailedToStart, 4) };
        rows[3].StartedAt = null;
        var result = SessionAnalytics.Calculate(rows, new(), new Dictionary<string, string> { [b] = "Новое имя" });
        check(result.PlaytimeSeconds == 12600, "playtime comes from actual process duration, not launcher lifetime");
        check(result.TotalLaunches == 4 && result.SuccessfulLaunches == 3 && result.FailedLaunches == 1, "attempt/start failure totals stay separate");
        check(result.AverageSeconds == 4200 && result.LongestSeconds == 7200, "average and longest ignore preparation-only attempts");
        check(Math.Abs(result.CrashRate - 100d / 3) < .001 && result.Crashes == 1, "crash rate denominator includes only known process outcomes");
        check(result.MostPlayedInstance == "Новое имя", "most played instance uses current name for a stable ID");
        check(result.Versions.Single().Label == "1.21.1" && result.Loaders.Single().Label == "Fabric", "version and loader distributions aggregate real snapshots");
        check(result.Playtime.Sum(x => x.Value) == 3.5 && result.Instances.Sum(x => x.Value) == 3.5, "timeline and instance graph totals match dashboard");
        var filtered = SessionAnalytics.Calculate(rows, new(a)); check(filtered.TotalLaunches == 2 && filtered.PlaytimeSeconds == 5400, "selected instance affects all aggregates");
        var since = rows[1].StartedAt!.Value; var dated = SessionAnalytics.Calculate(rows, new(From: since, Until: rows[2].StartedAt));
        check(dated.PlaytimeSeconds == 1800 && dated.Crashes == 1, "date filters apply to playtime and crashes");
        var partial = SessionAnalytics.Calculate(rows, new(a, rows[0].StartedAt!.Value.AddMinutes(30), rows[0].EndedAt));
        check(partial.PlaytimeSeconds == 1800, "sessions crossing a date boundary contribute only the included interval");
        check(SessionAnalytics.Calculate(rows, new(Bucket: "Weekly")).Playtime.Length <= result.Playtime.Length, "weekly buckets aggregate daily data");
        check(SessionAnalytics.Calculate(rows, new(Bucket: "Monthly")).Playtime.Length == 1, "monthly buckets use a calendar period");
        check(SessionAnalytics.Calculate([], new()).TotalLaunches == 0, "empty analytics has no invented past history");
        rows[3].Outcome = SessionOutcome.Interrupted; rows[3].ExitCode = null;
        check(SessionAnalytics.Calculate(rows, new()).Crashes == 1, "interrupted tracking does not become a crash");
        var streak = SessionAnalytics.Calculate(new[] { Session(a, 1), Session(a, 1, day: 2), Session(a, 1, SessionOutcome.Crashed, 3), Session(a, 1, day: 4) }, new());
        check(streak.CrashFreeStreak == 2 && streak.BusiestDay != "—", "Recap computes streak and busiest day from real completed sessions");
        rows[0].GameDirectory = "C:/private/account"; rows[0].ProcessId = 777;
        var json = StatsExport.Json(rows); using var document = JsonDocument.Parse(json);
        check(document.RootElement.GetProperty("sessions").GetArrayLength() == 4, "JSON export preserves selected session rows");
        check(!json.Contains("GameDirectory") && !json.Contains("C:/private") && !json.Contains("ProcessId"), "statistics export excludes local paths and process identities");
        rows[0].InstanceName = "=HYPERLINK(\"evil\")"; var csv = StatsExport.Csv(rows);
        check(csv.Contains("'=HYPERLINK") && csv.Contains("\"\"evil\"\""), "CSV escapes quotes and neutralizes spreadsheet formulas");
        check(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 5, "CSV has a header and one row per session");
        rows[0].InstanceName = " \t=HYPERLINK(\"evil\")";
        check(StatsExport.Csv(rows).Contains("' \t=HYPERLINK"), "leading whitespace cannot bypass CSV formula neutralization");
        var png = StatsExport.RecapPng(result, "All Time"); check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Recap PNG is rendered from data without screen capture");
    }
    private static void Performance(Action<bool, string> check)
    {
        var s = new GameSession(); var accumulator = new PerformanceAccumulator(s);
        accumulator.Add(5, 100, 1, 2); accumulator.Add(10, 300, 3, 2);
        check(s.AverageRamBytes == 200 && s.PeakRamBytes == 300, "RAM average and peak use observed samples");
        check(s.Samples[0].CpuPercent is null && s.Samples[1].CpuPercent == 20, "CPU uses delta processor time divided by logical processor count");
        for (var i = 3; i < 10000; i++) accumulator.Add(i * 5, 200, i, 2);
        check(s.SampleCount == 9999 && s.Samples.Count <= PerformanceAccumulator.MaximumSamples, "long sessions retain bounded graph samples with full aggregate count");
        check(s.Samples[0].Seconds == 5 && s.Samples[^1].Seconds == 49995, "sample compaction retains start and latest timeline coverage");
        var count = s.SampleCount; accumulator.Add(double.NaN, 4, 4, 2); accumulator.Add(1, -4, 4, 2);
        check(s.SampleCount == count, "invalid/backward performance samples ignored");
        var resumed = new PerformanceAccumulator(s); resumed.Add(50000, 200, 3000, 2); resumed.Add(50005, 200, 3001, 2);
        check(s.CpuSampleCount > 2 && s.AverageCpuPercent is >= 0 and <= 100, "recovered sampling retains accumulated CPU summary");
    }
    private static void Crash(Action<bool, string> check)
    {
        var cases = new Dictionary<string, string> { ["missing-dependency"] = "Missing mandatory dependencies: mod a requires b", ["fabric-loading"] = "Incompatible mods found!", ["forge-loading"] = "net.minecraftforge.fml.ModLoadingException", ["neoforge-loading"] = "net.neoforged.fml.LoadingFailedException", ["java-mismatch"] = "java.lang.UnsupportedClassVersionError", ["mixin"] = "MixinApplyError", ["memory"] = "java.lang.OutOfMemoryError", ["graphics"] = "GLFW error 65542", ["duplicate-mod"] = "Duplicate mods found", ["jar-corrupt"] = "Invalid or corrupt jarfile", ["class-missing"] = "java.lang.NoClassDefFoundError", ["mod-init"] = "Could not execute entrypoint stage" };
        foreach (var pair in cases) check(CrashRules.Analyze(pair.Value, true).Any(x => x.Code == pair.Key), "NexDoctor crash rule: " + pair.Key);
        check(CrashRules.Analyze("MixinApplyError", true).Single().Confidence == DiagnosticConfidence.Possible, "Mixin failure is not falsely assigned a confirmed root cause");
        check(CrashRules.Analyze("arbitrary truncated stack", true).Single().Confidence == DiagnosticConfidence.Unknown, "unknown/malformed crash produces an honest unknown diagnosis");
        check(CrashRules.Analyze("java.lang.OutOfMemoryError", false).Count == 0, "normal exit does not classify old exceptions as a crash");
        var sanitized = CrashRules.Analyze("java.lang.OutOfMemoryError Authorization: Bearer secret123", true).Single();
        check(!sanitized.Evidence.Contains("secret123"), "technical evidence never persists bearer secrets");
        check(!CrashRules.Analyze("Duplicate mods found", true).Single().HasSafeFix, "duplicate mod is not eligible for destructive automatic removal");
        check(CrashRules.Analyze("INFO Documentation: this mod depends on a library", true).Single().Code == "unknown-crash", "informational dependency text does not falsely confirm a missing dependency");
        check(ModDescriptors.TomlIds("[[mods]]\nmodId=\"real_mod\"\n[[dependencies.real_mod]]\nmodId=\"minecraft\"").SequenceEqual(["real_mod"]), "Forge/NeoForge descriptor reader excludes dependency IDs");
        check(!ModDescriptors.TomlIds("[[mods]]\nmodId=\"${mod_id}\"").Any(), "unresolved TOML placeholders cannot become fabricated mod identities");
    }
    private static async Task Health(string root, Action<bool, string> check)
    {
        var instance = new GameInstance { VersionId = "1.21.1", Loader = ModLoader.Fabric, LoaderVersion = "0.16.10" };
        instance.GameDirectory = Path.Combine(root, instance.Id, "game"); Directory.CreateDirectory(instance.GameDirectory);
        using var transport = new DownloadFixture(); using var client = new HttpClient(transport);
        var http = new LauncherHttp(client); var api = new ApiFixture(transport.Bytes); var manager = new ModManager(api, http);
        var health = new InstanceHealthService(manager, new NoGame());
        check((await health.ScanAsync(instance, instance.GameDirectory, default)).Count == 0, "healthy empty installed instance has no made-up errors");
        var mod = new InstalledMod { Title = "Fixture", Version = api.Version, FileName = "fixture.jar", Hashes = api.Version.Files[0].Hashes };
        mod.Version.Dependencies.Add(new() { ProjectId = "required", DependencyType = "required" });
        await AtomicJson.WriteAsync(ModManager.MetadataPath(instance.GameDirectory), new InstalledMods { Projects = [mod] }, default);
        var issues = await health.ScanAsync(instance, instance.GameDirectory, default);
        check(issues.Any(x => x.Code == "missing-managed" && x.HasSafeFix), "missing compatible managed file offers verified restoration");
        check(issues.Single(x => x.Code == "missing-dependency").Severity == DiagnosticSeverity.Warning && issues.Single(x => x.Code == "missing-dependency").Confidence == DiagnosticConfidence.Possible, "manual dependency prevents false confirmed missing-dependency error");
        await manager.RestoreMissingAsync(instance.GameDirectory, mod.Version.ProjectId, new Progress<LaunchProgress>(), default);
        var file = Path.Combine(instance.GameDirectory, "mods", mod.FileName);
        check(File.ReadAllBytes(file).SequenceEqual(transport.Bytes), "safe repair downloads exact recorded version and verifies hash");
        await File.WriteAllTextAsync(file, "user modified jar");
        await Reject<IOException>(() => manager.RestoreMissingAsync(instance.GameDirectory, mod.Version.ProjectId, new Progress<LaunchProgress>(), default), check, "safe repair refuses to overwrite existing manual replacement");
        check(await File.ReadAllTextAsync(file) == "user modified jar", "manual replacement survives repair refusal");
        issues = await health.ScanAsync(instance, instance.GameDirectory, default);
        check(issues.Any(x => x.Code == "managed-hash" && !x.HasSafeFix) && issues.Any(x => x.Code == "invalid-jar"), "corrupt JAR/hash is reported without destructive fix");
        var folder = Path.Combine(instance.GameDirectory, "mods"); Jar(Path.Combine(folder, "one.jar"), "duplicate"); Jar(Path.Combine(folder, "two.jar"), "duplicate");
        issues = await health.ScanAsync(instance, instance.GameDirectory, default);
        check(issues.Any(x => x.Code == "duplicate-mod" && x.Severity == DiagnosticSeverity.Warning), "duplicate detection uses declared mod ID rather than filename");
        instance.Loader = ModLoader.Forge;
        issues = await health.ScanAsync(instance, instance.GameDirectory, default); check(issues.Any(x => x.Code == "loader-mismatch"), "managed loader mismatch is confirmed from Modrinth metadata");
        instance.VersionId = "1.20.1";
        issues = await health.ScanAsync(instance, instance.GameDirectory, default); check(issues.Any(x => x.Code == "minecraft-mismatch"), "managed Minecraft mismatch uses version metadata");
        File.Delete(file); issues = await health.ScanAsync(instance, instance.GameDirectory, default);
        check(!issues.Single(x => x.Code == "missing-managed").HasSafeFix, "incompatible missing mod cannot be restored by Fix Safe Issues");
        var runtime = Path.Combine(root, "jdk", "bin"); Directory.CreateDirectory(runtime); instance.JavaPath = Path.Combine(runtime, "java.exe"); await File.WriteAllTextAsync(instance.JavaPath, "not executed");
        await File.WriteAllTextAsync(Path.Combine(root, "jdk", "release"), "JAVA_VERSION=\"17.0.1\"");
        var version = SafePaths.Resolve(instance.GameDirectory, "versions/1.20.1/1.20.1.json"); Directory.CreateDirectory(Path.GetDirectoryName(version)!);
        await File.WriteAllTextAsync(version, "{\"javaVersion\":{\"majorVersion\":21}}");
        issues = await health.ScanAsync(instance, instance.GameDirectory, default); check(issues.Any(x => x.Code == "java-mismatch" && x.Fix == DiagnosticFix.AutomaticJava), "Java mismatch compares runtime release with actual Minecraft profile requirement");
        await File.WriteAllTextAsync(version, "{broken");
        issues = await health.ScanAsync(instance, instance.GameDirectory, default);
        check(issues.Any(x => x.Code == "game-profile" && x.Fix == DiagnosticFix.RepairGame), "corrupt Minecraft profile offers repair instead of aborting the health scan");
        check(await File.ReadAllTextAsync(version) == "{broken", "health keeps the corrupted original profile unchanged");
        await File.WriteAllTextAsync(version, "{\"javaVersion\":{\"majorVersion\":\"21\"}}");
        check((await health.ScanAsync(instance, instance.GameDirectory, default)).Any(x => x.Code == "game-profile"), "invalid Java metadata type produces a diagnosis instead of aborting the scan");
        using (var archive = ZipFile.Open(Path.Combine(folder, "malformed.jar"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open())) writer.Write("[]");
        check((await health.ScanAsync(instance, instance.GameDirectory, default)).Any(x => x.Code == "invalid-jar" && x.Description.Contains("malformed.jar")), "malformed Fabric descriptor root cannot abort all health checks");
        await File.WriteAllTextAsync(version, "{\"javaVersion\":{\"majorVersion\":21}}");
        File.Delete(instance.JavaPath); check((await health.ScanAsync(instance, instance.GameDirectory, default)).Any(x => x.Code == "java-missing"), "unavailable manual Java detected without executing it");
        await Reject<OperationCanceledException>(() => health.ScanAsync(instance, instance.GameDirectory, new(true)), check, "health scan supports cancellation");
        check(File.Exists(Path.Combine(folder, "one.jar")) && File.Exists(Path.Combine(folder, "two.jar")), "health scan never removes unknown/manual JARs");
        var corrupt = ModManager.MetadataPath(instance.GameDirectory); await File.WriteAllTextAsync(corrupt, "{broken");
        check((await health.ScanAsync(instance, instance.GameDirectory, default)).Any(x => x.Code == "mod-metadata") && await File.ReadAllTextAsync(corrupt) == "{broken", "corrupt mod metadata produces a retained-source diagnosis instead of aborting all checks");
    }
    private static void Jar(string path, string id)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create); using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()); writer.Write("{\"id\":\"" + id + "\",\"version\":\"1\"}");
    }
    private static async Task Lifecycle(string root, Action<bool, string> check)
    {
        var data = Path.Combine(root, "lifecycle"); var store = new SessionStore(data);
        using var transport = new DownloadFixture(); using var client = new HttpClient(transport); var api = new ApiFixture(transport.Bytes);
        var doctor = new NexDoctorService(new InstanceHealthService(new ModManager(api, new LauncherHttp(client)), new NoGame()), store);
        using var tracker = new GameSessionTracker(store, doctor, new LauncherLog(data)); var instance = new GameInstance { VersionId = "1.21.1" };
        var game = Path.Combine(root, instance.Id, "game"); Directory.CreateDirectory(game);
        var attempt = await tracker.BeginAsync(instance, game); await attempt.FinishAsync(null, true);
        check((await store.SessionsAsync()).Single().Outcome == SessionOutcome.Cancelled, "cancelled pre-start attempt has no invented playtime/crash");
        var failed = await tracker.BeginAsync(instance, game); await failed.FinishAsync(null, false);
        check((await store.SessionsAsync()).Any(x => x.Outcome == SessionOutcome.FailedToStart && x.DurationSeconds == 0), "failed process preparation is tracked separately");
        using var own = Process.GetCurrentProcess(); var started = await tracker.BeginAsync(instance, game); await started.StartedAsync(own); await started.FinishAsync(0, false); await started.FinishAsync(1, false);
        check((await store.SessionsAsync()).Single(x => x.Id == started.Session.Id).Outcome == SessionOutcome.Completed, "normal completion and duplicate finalization preserve first outcome");
        var crash = await tracker.BeginAsync(instance, game); await crash.StartedAsync(own); await crash.FinishAsync(1, false);
        var persisted = (await store.SessionsAsync()).Single(x => x.Id == crash.Session.Id);
        check(persisted.Outcome == SessionOutcome.Crashed && persisted.DiagnosticId is not null, "abnormal exit links a saved session to NexDoctor");
        check((await store.ReportsAsync()).Single(x => x.Id == persisted.DiagnosticId).SessionId == crash.Session.Id, "NexDoctor report links back to the correct session");
        check((await store.ReportsAsync()).Any(x => x.SessionId == failed.Session.Id && x.Issues[0].Code == "launch-preparation"), "failed preparation has a diagnosis without a false Minecraft crash notification");
        var recovery = Session(instance.Id, 25, SessionOutcome.Running); recovery.ExitCode = null; recovery.ProcessId = int.MaxValue; await store.SaveAsync(recovery);
        await tracker.RecoverAsync(); check((await store.SessionsAsync()).Single(x => x.Id == recovery.Id).Outcome == SessionOutcome.Interrupted, "dead process recovery marks tracking interrupted instead of inventing an exit code");
        check((await store.SessionsAsync()).Single(x => x.Id == recovery.Id).DurationSeconds == 25, "recovery retains only observed duration when actual exit time is unknown");
        var mismatched = Session(instance.Id, 30, SessionOutcome.Running); mismatched.ProcessId = own.Id; mismatched.ProcessStartedAt = DateTimeOffset.UtcNow.AddYears(-1); await store.SaveAsync(mismatched);
        await tracker.RecoverAsync(); check((await store.SessionsAsync()).Single(x => x.Id == mismatched.Id).Outcome == SessionOutcome.Interrupted, "PID reuse cannot attach to a different process start time");
        var alive = Session(instance.Id, 1, SessionOutcome.Running); alive.StartedAt = new DateTimeOffset(own.StartTime.ToUniversalTime()); alive.ProcessStartedAt = alive.StartedAt; alive.ProcessId = own.Id; alive.Samples = [new(1, 1024, null)]; alive.SampleCount = 1;
        await store.SaveAsync(alive); using (var reattached = new GameSessionTracker(store, doctor, new LauncherLog(data)))
        {
            await reattached.RecoverAsync(); check(reattached.HasRunningProcess && !own.HasExited, "recovery reattaches matching PID/start time without starting or killing any process");
        }
        var logFolder = Path.Combine(game, "logs"); Directory.CreateDirectory(logFolder); await File.WriteAllTextAsync(Path.Combine(logFolder, "latest.log"), "Authorization: Bearer abc-secret\nINFO normal\nERROR example");
        var logs = await DoctorLogReader.ReadAsync(game, DateTimeOffset.UtcNow.AddMinutes(-1), default); check(!logs.Text.Contains("abc-secret") && logs.Path is not null, "source log excerpt is bounded and secret-redacted");
        var reports = Path.Combine(game, "crash-reports"); Directory.CreateDirectory(reports); var stale = Path.Combine(reports, "old.txt"); await File.WriteAllTextAsync(stale, "---- Minecraft Crash Report ----"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
        check(!(await DoctorLogReader.ReadAsync(game, DateTimeOffset.UtcNow.AddMinutes(-1), default)).HasFreshCrashReport, "old crash report cannot classify a new normal session as crashed");
        var badData = Path.Combine(root, "not-folder"); await File.WriteAllTextAsync(badData, "keep"); using var unavailable = new GameSessionTracker(new SessionStore(badData), doctor, new LauncherLog(data));
        var allowed = await unavailable.BeginAsync(instance, game); await allowed.FinishAsync(0, false);
        check(File.ReadAllText(badData) == "keep", "unavailable optional storage cannot throw into launch or destroy user file");
    }
    private static async Task Ui(string root, Action<bool, string> check)
    {
        var data = Path.Combine(root, "ui"); var config = new ConfigurationStore(data); var instance = new GameInstance { Name = "Очень длинное имя сборки для проверки переноса текста", VersionId = "1.21.1" };
        await config.SaveAsync(new() { Instances = [instance], SelectedInstanceId = instance.Id });
        var history = new SessionStore(data); var s = Session(instance.Id, 3600); await history.SaveAsync(s);
        var report = new DoctorReport { InstanceId = instance.Id, InstanceName = instance.Name, SessionId = s.Id, Issues = CrashRules.Analyze("MixinApplyError", true), LogExcerpt = "INFO line\nERROR detail" }; await history.SaveReportAsync(report); s.DiagnosticId = report.Id; await history.SaveAsync(s);
        var model = new MainWindowViewModel(config, new NoGame(), new NoAccounts()); await model.InitializeAsync();
        var window = new MainWindow(model) { Width = 1060, Height = 760 }; window.Show();
        model.NavigateCommand.Execute("stats"); model.Stats.Period = "All Time"; await model.Stats.RefreshAsync(); await Layout();
        check(model.IsStatsPage && !model.IsStandardPage && model.Stats.Summary.TotalLaunches == 1, "NexStats navigation loads existing session data into full content page");
        var stats = window.GetVisualDescendants().OfType<NexStatsView>().Single();
        check(stats.Bounds.Width > window.ClientSize.Width - 196 - 100, "NexStats uses main content area without a narrow fixed panel");
        using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "nexstats-normal.png"), PngBitmapEncoderOptions.Default);
        window.Width = 1920; window.Height = 1080; await Layout();
        check(stats.Bounds.Width > 1600, "NexStats expands with maximized-size layout");
        using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "nexstats-wide.png"), PngBitmapEncoderOptions.Default);
        model.Stats.IsRecap = true; model.Stats.RecapPeriod = "All Time"; await model.Stats.RefreshAsync(); check(model.Stats.Summary.PlaytimeSeconds == 3600, "Recap uses the same real session aggregation");
        model.Stats.IsRecap = false; await model.Stats.RefreshAsync(); model.Stats.OpenDiagnosisCommand.Execute(null); await Task.Delay(50); await Layout();
        check(model.IsDoctorPage && model.Doctor.SelectedReport?.Id == report.Id, "NexStats crash selection opens exact NexDoctor report ID");
        model.Doctor.OpenSessionCommand.Execute(null); await Task.Delay(50); await Layout(); check(model.IsStatsPage && model.Stats.SelectedSession?.Id == s.Id, "NexDoctor returns to exact NexStats session ID");
        model.CurrentPage = "doctor"; await model.Doctor.OpenAsync(report.Id); model.Doctor.Search = "detail";
        check(model.Doctor.LogText == "ERROR detail", "NexDoctor log search filters actual saved lines");
        string? copied = null; model.Doctor.CopyTextAsync = text => { copied = text; return Task.CompletedTask; };
        await model.Doctor.CopyLogCommand.ExecuteAsync(null);
        check(copied == "ERROR detail", "copy visible log cannot substitute the selected diagnostic evidence");
        check(model.Doctor.Issues.Single().Confidence == DiagnosticConfidence.Possible && model.Doctor.Errors == 1, "doctor dashboard preserves severity and confidence");
        check(!model.Doctor.FixSafeCommand.CanExecute(null), "unsafe Mixin/duplicate advice cannot enable Fix Safe Issues");
        await Layout(); using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "nexdoctor-wide.png"), PngBitmapEncoderOptions.Default);
        window.Width = 900; window.Height = 650; await Layout(); using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(root, "nexdoctor-small.png"), PngBitmapEncoderOptions.Default);
        using var css = new QuickCssService(window); var theme = Path.Combine(root, "insights.css"); await File.WriteAllTextAsync(theme, "#nexdoctor { background: #173022; } .diagnostic-issue { border-radius: 18px; } #nexstats { background: #172530; }");
        var applied = await css.ConfigureAsync(new() { Enabled = true, FilePath = theme }, default);
        check(applied.Diagnostics.Count == 0, "Quick CSS accepts stable NexDoctor/NexStats public selectors");
        await model.Doctor.OpenAsync(Guid.NewGuid().ToString("N")); await Task.Delay(40);
        check(model.Doctor.SelectedReport is null, "expired diagnostic ID never displays another report as a substitute");
        var selected = model.Instances.Single(); Directory.CreateDirectory(selected.GameDirectory); selected.JavaPath = Path.Combine(root, "absent-java", "java.exe");
        await model.Doctor.ScanCommand.ExecuteAsync(null);
        model.Doctor.SelectedIssue = model.Doctor.Issues.Single(x => x.Code == "java-missing"); await model.Doctor.FixSelectedCommand.ExecuteAsync(null);
        check(selected.JavaPath == "" && (await config.LoadAsync()).Instances.Single().JavaPath == "", "safe automatic Java fix persists through existing settings architecture");
        model.Stats.ConfirmClear = false; await model.Stats.ClearCommand.ExecuteAsync(null);
        check((await new SessionStore(data).SessionsAsync()).Length == 1, "statistics deletion requires explicit confirmation");
        model.Stats.ConfirmClear = true; await model.Stats.ClearCommand.ExecuteAsync(null);
        check((await new SessionStore(data).SessionsAsync()).Length == 0 && (await config.LoadAsync()).Instances.Count == 1, "confirmed history deletion preserves instances/settings");
        window.Close();
    }
    private static async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
    private sealed class DownloadFixture : HttpMessageHandler
    {
        public byte[] Bytes { get; } = CreateJar();
        private static byte[] CreateJar()
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            { using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()); writer.Write("{\"id\":\"fixture\",\"version\":\"1\"}"); }
            return output.ToArray();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
    }
    private sealed class ApiFixture : IModrinthService
    {
        public ModrinthVersion Version { get; }
        public ApiFixture(byte[] bytes) => Version = new() { Id = "version1", ProjectId = "project1", VersionNumber = "1", GameVersions = ["1.21.1"], Loaders = ["fabric"], Files = [new() { Filename = "fixture.jar", Url = "https://cdn.modrinth.com/fixture.jar", Primary = true, Size = bytes.Length, Hashes = new() { ["sha512"] = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant() } }] };
        public Task<ModrinthVersion> VersionAsync(string id, CancellationToken token) => Task.FromResult(Version);
        public Task<ModrinthProject> ProjectAsync(string id, CancellationToken token) => Task.FromResult(new ModrinthProject { Id = id, ProjectType = "mod" });
        public Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string id, GameInstance? instance, CancellationToken token) => Task.FromResult<IReadOnlyList<ModrinthVersion>>([Version]);
        public Task<IReadOnlyList<ModrinthTeamMember>> MembersAsync(string id, CancellationToken token) => Task.FromResult<IReadOnlyList<ModrinthTeamMember>>([]);
        public Task<ModrinthFilterCatalog> FilterCatalogAsync(CancellationToken token) => Task.FromResult(new ModrinthFilterCatalog([], []));
        public Task<ModrinthSearchResult> SearchAsync(string query, bool packs, GameInstance? instance, int offset, CancellationToken token, ModrinthSearchOptions? options = null) => Task.FromResult(new ModrinthSearchResult());
    }
    private sealed class NoGame : IMinecraftService
    {
        public bool IsInstalled(GameInstance instance) => true;
        public Task<IReadOnlyList<MinecraftRelease>> GetVersionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MinecraftRelease>>([new("1.21.1", "release", DateTimeOffset.UtcNow)]);
        public Task InstallAsync(GameInstance instance, IProgress<LaunchProgress> progress, CancellationToken token) => Task.CompletedTask;
        public Task<int> LaunchAsync(GameInstance instance, MSession session, IProgress<LaunchProgress> progress, Action<string> log, CancellationToken token) => throw new Exception("Tests must never launch Minecraft");
    }
    private sealed class NoAccounts : IAccountService
    {
        public IReadOnlyList<LauncherAccount> Accounts => [];
        public string? ActiveAccountId => null;
        public string? PlayerName => null;
        public string? MicrosoftAvailabilityWarning => null;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<MSession> SignInAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<MSession?> RestoreAsync(CancellationToken token) => Task.FromResult<MSession?>(null);
        public Task SelectAccountAsync(string id, CancellationToken token) => Task.CompletedTask;
        public Task RemoveAccountAsync(string id, CancellationToken token) => Task.CompletedTask;
        public Task SignOutAsync() => Task.CompletedTask;
        public Task<LauncherAccount> CreateLocalAccountAsync(string name, CancellationToken token) => throw new NotSupportedException();
    }
}
