using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexLauncher;
using NexLauncher.Services;
using NexLauncher.Services.Network;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;

internal static class ApplicationReadinessChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var privacy = ApplicationInformation.PrivacyText;
        check(privacy.Contains("20 октября 2026") && privacy.Contains("Quick CSS") && privacy.Contains("Modrinth") && privacy.Contains("DPAPI"),
            "bundled privacy notice describes the actual authentication, theme and network data flows");
        check(privacy.Contains("нет подключённого CurseForge API") && privacy.Contains("не отправляются") && privacy.Contains("не принимаются"),
            "privacy notice does not claim CurseForge approval or automatic agreement acceptance");
        check(ApplicationInformation.Link("source") == ApplicationInformation.SourceUrl && ApplicationInformation.Link("issues") == ApplicationInformation.IssuesUrl,
            "About links resolve only to the declared source and issues pages");
        foreach (var input in new[] { "file:///C:/secret", "javascript:alert(1)", "https://example.org", "unknown" })
            check(ApplicationInformation.Link(input) is null, "About command cannot open an injected target: " + input);
        var network = new CountingHandler(); using var client = new HttpClient(network); var http = new LauncherHttp(client);
        try { await http.GetBytesAsync("https://api.curseforge.com/v1/mods/search", LauncherHttp.PackHosts, default); throw new Exception("Expected provider rejection"); }
        catch (InvalidDataException) { check(network.Requests == 0, "inactive CurseForge provider cannot receive a pack-triggered request or credentials"); }

        var store = new ConfigurationStore(Path.Combine(root, "application review Мир"));
        var model = new MainWindowViewModel(store, new FakeMinecraft(), new FakeAccounts()); await model.InitializeAsync();
        var window = new MainWindow(model) { Width = 1060, Height = 800 }; window.Show(); await Layout();
        var sections = new[] { ("modrinth", "Modrinth"), ("skins", "Скины"), ("doctor", "Исправление неполадок"), ("stats", "Статистика") };
        foreach (var (page, label) in sections)
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(x => x.Classes.Contains("nav") && x.CommandParameter as string == page);
            var glyph = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(x => x.Classes.Contains("nav-glyph"));
            check(glyph.Data is not null && glyph.Stroke is ISolidColorBrush && button.Command == model.NavigateCommand,
                "native vector navigation icon preserves its existing command: " + page);
            check(button.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == label), "sidebar section uses the visible category name: " + label);
        }
        using (var css = new QuickCssService(window))
        {
            var theme = Path.Combine(root, "navigation-icons.css");
            await File.WriteAllTextAsync(theme, ".nav { color: #b4c0ce; } .nav.active { color: #66ccbb; }");
            var applied = await css.ConfigureAsync(new() { Enabled = true, FilePath = theme }, default);
            model.CurrentPage = "stats"; await Layout();
            var active = window.GetVisualDescendants().OfType<Button>().Single(x => x.Classes.Contains("nav") && x.CommandParameter as string == "stats");
            var glyph = active.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(x => x.Classes.Contains("nav-glyph"));
            check(applied.Diagnostics.Count == 0 && glyph.Stroke is ISolidColorBrush brush && brush.Color == Color.Parse("#66ccbb"),
                "navigation icon follows active Quick CSS foreground instead of hardcoded color");
        }
        window.Width = 900; window.Height = 650; await Layout();
        var doctorButton = window.GetVisualDescendants().OfType<Button>().Single(x => x.Classes.Contains("nav") && x.CommandParameter as string == "doctor");
        var doctorLabel = doctorButton.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == "Исправление неполадок");
        check(doctorLabel.TextWrapping == TextWrapping.Wrap && doctorLabel.Bounds.Width > 70 && doctorLabel.Bounds.Height > 20 && doctorButton.Bounds.Height >= doctorLabel.Bounds.Height,
            "long troubleshooting category wraps within the sidebar at minimum window width");
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(root, "navigation-icons-small.png"), PngBitmapEncoderOptions.Default);
        window.Width = 1920; window.Height = 1080; await Layout();
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(root, "navigation-icons-wide.png"), PngBitmapEncoderOptions.Default);
        window.Width = 1060; window.Height = 800; await Layout();
        var visibleText = window.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).Where(x => x is not null).ToArray();
        foreach (var slogan in new[]
        {
            "Твой мир. Твои сборки.", "Всё нужное для комфортной игры.", "Твои миры. Твоё время.",
            "Локальная диагностика. Доказательства прежде предположений.",
            "Сохранённые PNG на этом устройстве. Автоматический онлайн-каталог пока не подключён.",
            "Готовые сборки · каждый modpack устанавливается как новый instance", "Один шаг до следующего приключения."
        }) check(!visibleText.Contains(slogan), "requested tagline is absent from the actual UI: " + slogan);
        check(model.HeroTitle == "Играть" && model.InstanceTitle == "Сборка не выбрана", "home headings are functional instead of promotional");
        model.CurrentPage = "settings"; await Layout();
        var information = window.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("qc-application-info"));
        var expander = information.GetVisualDescendants().OfType<Expander>().Single(); expander.IsExpanded = true; await Layout();
        check(information.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == privacy), "full privacy notice is readable inside native Settings without web navigation");
        check(information.GetVisualDescendants().OfType<Button>().Count(x => x.Command == model.OpenInformationLinkCommand) == 2,
            "project links require an explicit user click; checks do not execute or submit them");
        var scroller = information.GetVisualAncestors().OfType<ScrollViewer>().First();
        var origin = information.TranslatePoint(new Avalonia.Point(), (Avalonia.Visual)scroller.Content!)!.Value;
        scroller.Offset = new Avalonia.Vector(0, origin.Y); await Layout();
        check(information.Bounds.Width > 500 && expander.Bounds.Width <= information.Bounds.Width, "About and local privacy notice fit the Settings content area");
        var screenshots = Path.Combine(root, "application review screenshots"); Directory.CreateDirectory(screenshots);
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(screenshots, "about-privacy.png"), PngBitmapEncoderOptions.Default);
        var doc = QuickCssParser.Parse("#application-info { border-radius: 17px; }");
        check(QuickCssStyleApplier.Inspect(doc).Count == 0, "About exposes a typed public Quick CSS selector");
        window.Close();
    }
    private static async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(80); Dispatcher.UIThread.RunJobs(); }
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
