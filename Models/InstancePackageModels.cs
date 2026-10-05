using System;
using System.Collections.Generic;

namespace NexLauncher.Models;

public sealed class InstanceManifest
{
    public int ManifestVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public ModLoader Loader { get; set; }
    public string LoaderVersion { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public string LauncherVersion { get; set; } = "";
    public DateTimeOffset ExportedUtc { get; set; }
    public string? OriginalModpackProjectId { get; set; }
    public string? OriginalModpackVersionId { get; set; }
    public List<PortableMod> Mods { get; set; } = new();
    public List<PortableInstanceFile> Files { get; set; } = new();
    public List<string> OmittedFiles { get; set; } = new();
}

public sealed record PortableMod(string ProjectId, string VersionId, string Filename,
    Dictionary<string, string> Hashes, long Size, bool Explicit);
public sealed record PortableInstanceFile(string Path, long Size, string Sha256);
public sealed record InstanceExportOptions(bool LocalMods = false, bool Configs = false,
    bool ResourcePacks = false, bool ShaderPacks = false);
public sealed record InstanceExportPlan(InstanceManifest Manifest, string SourceDirectory,
    IReadOnlyList<string> Notices, IReadOnlyDictionary<string, bool> ManagedIntegrity);
public sealed record InstanceImportPreview(InstanceManifest Manifest, string PackageSha256);
