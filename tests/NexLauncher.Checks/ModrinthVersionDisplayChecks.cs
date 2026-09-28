using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexLauncher.Models;
using NexLauncher.Services.Network;
using NexLauncher.Views;

internal static class ModrinthVersionDisplayChecks
{
    public static void Run(Action<bool, string> check)
    {
        var version = JsonSerializer.Deserialize<ModrinthVersion>("""
            {"id":"chosen42","project_id":"project1","version_number":"15.0.0-alpha.4",
             "game_versions":["1.21.11"],"loaders":["fabric"],"version_type":"alpha",
             "date_published":"2026-09-27T12:34:00Z"}
            """, LauncherHttp.JsonOptions)!;
        check(version.DisplayNumber == "15.0.0-alpha.4" && version.CompatibilityLabel == "Minecraft 1.21.11 · Fabric", "version dropdown maps version_number, game_versions and loaders from API metadata");
        check(version.SelectionSummary == "15.0.0-alpha.4 · Minecraft 1.21.11 · Fabric · Alpha · 27.09.2026", "selected version summary includes project version, Minecraft, loader, channel and publication date");
        version.VersionNumber = "Release for Minecraft 9.99";
        check(version.CompatibilityLabel.Contains("1.21.11") && !version.CompatibilityLabel.Contains("9.99"), "compatibility is never inferred from the project version name");
        version.GameVersions = ["1.20.1", "1.21", "1.21.1", "1.21.4", "1.21.5"];
        version.Loaders = ["fabric", "forge", "neoforge"];
        check(version.CompatibilityLabel == "Minecraft 1.20.1, 1.21, 1.21.1 (+2) · Fabric, Forge, NeoForge", "multiple supported Minecraft versions and loaders have bounded, truthful dropdown text");
        check(version.GameVersions.All(version.SelectionSummary.Contains) && version.SelectionSummary.Contains("Fabric, Forge, NeoForge"), "selected summary retains every advertised version and loader without inventing a range");
        version.Loaders = ["fabric", "forge", "neoforge", "quilt", "future-loader"];
        check(version.CompatibilityLabel.Contains("Fabric, Forge, NeoForge (+2)") && version.SelectionSummary.Contains("Quilt, future-loader"), "many and unknown loaders remain visible in full metadata without guessing aliases");
        version.GameVersions = ["1.21.1", "", " ", "1.21.1"];
        check(version.CompatibilityLabel.StartsWith("Minecraft 1.21.1 ·"), "empty duplicate display values are skipped without changing source metadata");
        var missing = JsonSerializer.Deserialize<ModrinthVersion>("{}", LauncherHttp.JsonOptions)!;
        check(missing.SelectionSummary == "Версия не указана · Minecraft не указан · загрузчик не указан · Тип не указан · Дата не указана", "missing fields display explicit unknown values instead of fabricated release, date or compatibility");
        missing.GameVersions = null!; missing.Loaders = null!; missing.VersionNumber = null!; missing.VersionType = null!;
        check(missing.SelectionSummary.Contains("Minecraft не указан") && missing.CompatibilityLabel.Contains("загрузчик не указан"), "null metadata is safe for presentation; API validation remains separate");
        version.VersionNumber = new string('v', 400); version.VersionType = "beta";
        check(version.DisplayNumber.Length == 400 && version.SelectionSummary.Contains("Beta"), "long project version remains available in full summary and tooltip");
        var json = JsonSerializer.Serialize(version, LauncherHttp.JsonOptions);
        check(!json.Contains("display_number") && !json.Contains("compatibility_label") && !json.Contains("selection_summary"), "presentation fields never alter persisted Modrinth metadata format");
        check(version.Id == "chosen42" && JsonSerializer.Deserialize<ModrinthVersion>(json, LauncherHttp.JsonOptions)!.Id == version.Id, "display formatting and persistence preserve the exact Modrinth version ID");
    }

    public static async Task VerifySelectorAsync(Window window, ModrinthView view, string directory, string name, Action<bool, string> check)
    {
        var selector = view.FindControl<ComboBox>("VersionSelector")!;
        selector.BringIntoView(); await Layout();
        var selected = (ModrinthVersion)selector.SelectedItem!;
        check(selector.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == selected.CompatibilityLabel && x.Bounds.Height > 0) &&
            selector.Bounds.Width <= view.Bounds.Width, name + ": selected ComboBox renders Minecraft and loader within content width");
        selector.IsDropDownOpen = true; await Layout();
        try
        {
            var rows = selector.GetRealizedContainers().ToArray();
            check(rows.Length > 0 && rows.All(row => row.DataContext is ModrinthVersion version &&
                row.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == version.CompatibilityLabel && x.Bounds.Height > 0)),
                name + ": every realized dropdown row renders its own version compatibility metadata");
            var popup = TopLevel.GetTopLevel(rows[0])!;
            var dropdown = rows[0].GetVisualAncestors().OfType<ScrollViewer>().First();
            check(dropdown.Bounds.Width <= window.ClientSize.Width && dropdown.Bounds.Height <= selector.MaxDropDownHeight,
                name + ": large dropdown remains bounded and scrollable");
            using (var frame = popup.CaptureRenderedFrame()) frame!.Save(Path.Combine(directory, name + "-dropdown.png"), PngBitmapEncoderOptions.Default);
            selector.ScrollIntoView(selector.ItemCount - 1); await Layout();
            check(selector.ContainerFromIndex(selector.ItemCount - 1) is { Bounds.Height: > 0 }, name + ": final release is reachable by scrolling");
        }
        finally { selector.IsDropDownOpen = false; await Layout(); }
        using var selectedFrame = window.CaptureRenderedFrame();
        selectedFrame!.Save(Path.Combine(directory, name + "-selected.png"), PngBitmapEncoderOptions.Default);
    }
    private static async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(40); Dispatcher.UIThread.RunJobs(); }
}
