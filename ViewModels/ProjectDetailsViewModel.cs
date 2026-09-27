using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexLauncher.Models;
using NexLauncher.Services.Modrinth;

namespace NexLauncher.ViewModels;

public sealed record ProjectLinkViewModel(string Label, Uri Url, IRelayCommand Command)
{
    public string DisplayLabel => Label + " ↗ · " + Url.Host;
}

public sealed partial class ProjectDetailsViewModel : ObservableObject, IDisposable
{
    public ModrinthProject Project { get; }
    public ProjectDescription Description { get; }
    public string DescriptionNotice { get; }
    public bool HasDescriptionNotice => DescriptionNotice.Length > 0;
    public string Credits { get; }
    public string Statistics => (Project.ProjectType == "modpack" ? "Modpack" : "Мод") + " · " + Project.Downloads.ToString("N0") + " загрузок";
    public string MinecraftVersions => "Minecraft: " + string.Join(", ", Project.GameVersions);
    public string Loaders => "Загрузчики: " + string.Join(", ", Project.Loaders);
    public string License => "Лицензия: " + (Project.License?.Id ?? "не указана");
    public IReadOnlyList<ProjectLinkViewModel> Links { get; }
    public IRelayCommand<string> OpenLinkCommand { get; }
    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private string _installationStatus = "";

    public ProjectDetailsViewModel(ModrinthProject project, ProjectDescription description, string descriptionNotice,
        string searchAuthor, IReadOnlyList<ModrinthTeamMember> members, Action<string> openLink)
    {
        Project = project; Description = description; DescriptionNotice = descriptionNotice;
        var names = members.Select(x => x.User.Name is { Length: > 0 } name ? name + " (@" + x.User.Username + ")" : x.User.Username).Distinct().ToArray();
        Credits = (searchAuthor.Length == 0 ? "" : "Автор: " + searchAuthor + "\n") +
            (names.Length == 0 ? (searchAuthor.Length == 0 ? "Авторы не указаны в доступных данных." : "") : "Команда: " + string.Join(", ", names));
        var links = new List<ProjectLinkViewModel>();
        OpenLinkCommand = new RelayCommand<string>(url => { if (SafeProjectLink.TryCreate(url, out var uri)) openLink(uri!.AbsoluteUri); });
        void Add(string label, string? url)
        {
            if (SafeProjectLink.TryCreate(url, out var uri)) links.Add(new(label, uri!, new RelayCommand(() => OpenLinkCommand.Execute(uri!.AbsoluteUri))));
        }
        Add("Страница проекта и поддержка автора", "https://modrinth.com/" + (project.ProjectType == "modpack" ? "modpack/" : "mod/") + project.Id);
        Add("Исходный код", project.SourceUrl); Add("Ошибки", project.IssuesUrl); Add("Wiki", project.WikiUrl); Add("Discord", project.DiscordUrl);
        Add("Лицензия", project.License?.Url);
        foreach (var donation in project.DonationUrls?.Take(16) ?? []) if (donation is not null) Add("Поддержать · " + donation.Platform, donation.Url);
        // v2 exposes an organization ID, but no documented organization-name endpoint. Link by stable ID.
        if (project.Organization is { Length: > 0 } organization && organization.Length <= 64 && organization.All(char.IsAsciiLetterOrDigit))
            Add("Организация проекта", "https://modrinth.com/organization/" + organization);
        Links = links;
    }

    public void UpdateInstalled(IEnumerable<InstalledMod> installed, IReadOnlyList<ModrinthVersion> compatible)
    {
        if (Project.ProjectType == "modpack") { InstallationStatus = "Установка создаст новую независимую сборку."; return; }
        var current = installed.FirstOrDefault(x => x.Version.ProjectId == Project.Id);
        if (current is null) { InstallationStatus = "Не установлен через NexLauncher в этой сборке. Ручные JAR здесь не учитываются."; return; }
        var newer = compatible.FirstOrDefault(x => x.DatePublished > current.Version.DatePublished && x.Id != current.Version.Id);
        InstallationStatus = "Установлена версия: " + current.Version.VersionNumber + (newer is null ? " · Более новых совместимых версий нет." : " · Доступно обновление: " + newer.VersionNumber);
    }
    public void Dispose() { Icon?.Dispose(); Icon = null; }
}
