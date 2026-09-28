using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using NexLauncher.Models;

namespace NexLauncher.ViewModels;

/// <summary>Session-only, independent filters for each browser mode; no account/game state.</summary>
public sealed partial class ModrinthFiltersViewModel : ObservableObject
{
    public static FilterChoice[] Sorts { get; } = [new("relevance", "Релевантность"), new("downloads", "Загрузки"), new("updated", "Недавно обновлены"), new("newest", "Новые проекты")];
    public static FilterChoice[] Loaders { get; } = [new("", "Все загрузчики"), new("fabric", "Fabric"), new("forge", "Forge"), new("neoforge", "NeoForge")];
    public static FilterChoice[] Environments { get; } = [new("", "Все окружения"), new("client_only", "Только клиент"), new("client_and_server", "Клиент и сервер"), new("singleplayer_only", "Одиночная игра")];
    public ObservableCollection<FilterChoice> MinecraftVersions { get; } = [new("", "Все версии")];
    public ObservableCollection<FilterChoice> Categories { get; } = [new("", "Все категории")];
    [ObservableProperty] private FilterChoice? _minecraft;
    [ObservableProperty] private FilterChoice? _loader = Loaders[0];
    [ObservableProperty] private FilterChoice? _category;
    [ObservableProperty] private FilterChoice? _environment = Environments[0];
    [ObservableProperty] private FilterChoice? _sort = Sorts[0];
    public ModrinthSearchOptions Options => new(Minecraft?.Value ?? "", Loader?.Value ?? "", Category?.Value ?? "", Environment?.Value ?? "", Sort?.Value ?? "relevance");
    public ModrinthFiltersViewModel() { Minecraft = MinecraftVersions[0]; Category = Categories[0]; }
    public void Populate(ModrinthFilterCatalog catalog, bool packs)
    {
        var mc = Minecraft?.Value; var category = Category?.Value;
        MinecraftVersions.Clear(); MinecraftVersions.Add(new("", "Все версии"));
        foreach (var version in catalog.Versions.Where(x => x.VersionType == "release").DistinctBy(x => x.Version)) MinecraftVersions.Add(new(version.Version, version.Version));
        Categories.Clear(); Categories.Add(new("", "Все категории"));
        foreach (var tag in catalog.Categories.Where(x => x.ProjectType == (packs ? "modpack" : "mod")).DistinctBy(x => x.Name).OrderBy(x => x.Name)) Categories.Add(new(tag.Name, tag.Name));
        Minecraft = MinecraftVersions.FirstOrDefault(x => x.Value == mc) ?? MinecraftVersions[0];
        Category = Categories.FirstOrDefault(x => x.Value == category) ?? Categories[0];
    }
    public void Reset() { Minecraft = MinecraftVersions[0]; Loader = Loaders[0]; Category = Categories[0]; Environment = Environments[0]; Sort = Sorts[0]; }
}
