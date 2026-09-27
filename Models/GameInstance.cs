using System;

namespace NexLauncher.Models;

public enum ModLoader { Vanilla, Fabric, Forge, NeoForge }

public sealed class GameInstance
{
    public int FormatVersion { get; set; } = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Моя сборка";
    public string VersionId { get; set; } = "";
    public ModLoader Loader { get; set; }
    public string LoaderVersion { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string? ModrinthProjectId { get; set; }
    public string? ModrinthVersionId { get; set; }
    public string Details => $"Minecraft {VersionId} · {Loader}" + (Loader == ModLoader.Vanilla ? "" : $" {LoaderVersion}");
    public int MemoryMb { get; set; } = 4096;
    public string JavaPath { get; set; } = "";
    public override string ToString() => Name;
}
