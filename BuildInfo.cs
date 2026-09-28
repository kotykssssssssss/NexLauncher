using System.Reflection;

namespace NexLauncher;

/// <summary>One release version for the UI, HTTP identification and Minecraft launch metadata.</summary>
public static class BuildInfo
{
    public static string Version { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static string DisplayName => "NexLauncher · " + Version;
}
