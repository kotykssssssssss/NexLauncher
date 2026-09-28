using System.Reflection;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Network;
using NexLauncher.ViewModels;

internal static class ReleaseChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var assembly = typeof(BuildInfo).Assembly;
        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        check(product == "NexLauncher" && BuildInfo.Version == "0.1.1-alpha", "release product and informational version are set");
        check(LauncherHttp.UserAgent.StartsWith("NexLauncher/" + BuildInfo.Version + " "), "HTTP identification follows the release version");
        using var resource = assembly.GetManifestResourceStream("NexLauncher.QuickCssExample");
        check(resource is { Length: > 100 }, "Quick CSS example is embedded, independent of publish directory");
        var data = Path.Combine(root, "first run Релиз", "user data");
        var store = new ConfigurationStore(data);
        check(!Directory.Exists(data) && (await store.LoadAsync()).Instances.Count == 0,
            "first run tolerates entirely absent user data directory");
        using var vm = new QuickCssViewModel(data, settings => store.SaveAsync(new LauncherConfiguration { QuickCss = settings }), () => true);
        await vm.CreateExampleCommand.ExecuteAsync(null);
        var first = vm.FilePath;
        check(File.Exists(first) && first.StartsWith(data) && (await store.LoadAsync()).QuickCss.FilePath == first,
            "embedded theme creates and persists user directories with spaces and Unicode");
        var content = await File.ReadAllTextAsync(first);
        await vm.CreateExampleCommand.ExecuteAsync(null);
        check(first != vm.FilePath && await File.ReadAllTextAsync(first) == content,
            "creating another bundled theme preserves the existing user theme");
        check(!new ConfigurationStore().DataDirectory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "default user data is outside application installation directory");
    }
}
