using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexLauncher.Models;
using NexLauncher.Services.Modrinth;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;
using NexLauncher.Views;

internal static class ReadableDescriptionChecks
{
    internal const string MixedBody = """
        **A fast rendering engine.** Read the [installation guide](https://example.org/guide).

        [![Buy me a coffee](https://example.org/badge.svg)](https://example.org/donate)
        [![Join us on Discord](https://example.org/discord.svg)](https://example.org/discord)

        ---

        <center>
          <img src="https://cdn.modrinth.com/data/project/comparison.png" alt="Sodium comparison" onerror="alert('NO')">
          <p><!-- This paragraph is load bearing, not description text. --></p>
        </center>

        <h1 id="installation">Installation Guide</h1>

        This is <strong>important</strong> and <em>readable</em>, with <a href="https://example.org/wiki">a useful link</a>.

        1. Choose the matching loader.
        2. Install the compatible version.

        > Keep a backup before changing your pack.

        ```html
        <script>This code sample stays literal.</script>
        ```
        """;

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var document = ProjectDescriptionParser.Parse(MixedBody);
        var spans = document.Blocks.SelectMany(x => x.Spans).ToArray();
        var text = string.Concat(spans.Select(x => x.Text));
        check(!text.Contains("<center>") && !text.Contains("<img") && !text.Contains("load bearing"), "mixed project description hides HTML wrappers, image markup and comments");
        check(document.Blocks.Any(x => x.Kind == DescriptionBlockKind.Heading && x.Spans.Any(s => s.Text == "Installation Guide")), "HTML heading becomes a native semantic heading");
        check(spans.Single(x => x.Text == "Buy me a coffee").Link?.AbsoluteUri == "https://example.org/donate", "linked Markdown badge opens its outer destination without leaking image URL syntax");
        check(spans.Single(x => x.Text == "Join us on Discord").Link?.AbsoluteUri == "https://example.org/discord" && !text.Contains("]("), "adjacent linked badges retain clean readable labels");
        check(spans.Single(x => x.Text == "Изображение: Sodium comparison").Link?.Host == "cdn.modrinth.com", "HTML image becomes an explicit link, without downloading remote content");
        check(spans.Any(x => x.Kind == DescriptionSpanKind.Bold && x.Text == "important") && spans.Any(x => x.Kind == DescriptionSpanKind.Italic && x.Text == "readable"), "HTML inline emphasis shares the Markdown native render model");
        check(document.Blocks.Any(x => x.Kind == DescriptionBlockKind.Rule), "Markdown separator becomes a divider rather than visible dashes");
        check(document.Blocks.Count(x => x.Kind == DescriptionBlockKind.ListItem) == 2 && text.Contains("1. ") && text.Contains("2. "), "ordered list items retain their numbers");
        check(document.Blocks.Single(x => x.Kind == DescriptionBlockKind.Code).Spans.Single().Text == "<script>This code sample stays literal.</script>", "escaped HTML in a fenced code example remains literal");

        var reference = ProjectDescriptionParser.Parse("[**Guide**][docs]\n\n[docs]: https://example.org/guide_(new) \"Title\"");
        check(reference.Blocks.Single().Spans.Single() is { Kind: DescriptionSpanKind.Link, Strong: true, Link.AbsoluteUri: "https://example.org/guide_(new)" }, "reference links, nested emphasis, titles and balanced URL parentheses parse correctly");
        var splitLabel = ProjectDescriptionParser.Parse("[Read **the full** guide](https://example.org/guide)");
        check(splitLabel.Blocks.Single().Spans.Single() is { Text: "Read the full guide", Kind: DescriptionSpanKind.Link }, "one formatted link stays one clickable label instead of several arrow buttons");
        var looseList = ProjectDescriptionParser.Parse("- First paragraph.\n\n- Second paragraph.");
        check(looseList.Blocks.Count == 2 && looseList.Blocks.All(x => x.Kind == DescriptionBlockKind.ListItem && x.Spans.Count > 1), "loose Markdown list keeps bullet and paragraph in the same native block");
        check(string.Concat(ProjectDescriptionParser.Parse("Fish &amp; chips &lt;not a tag&gt;").Blocks.SelectMany(x => x.Spans).Select(x => x.Text)) == "Fish & chips <not a tag>", "HTML entities decode once into inert text");
        foreach (var tag in new[] { "script", "style", "iframe", "object", "svg", "math", "form", "template" })
        {
            var hostile = ProjectDescriptionParser.Parse($"Before\n\n<{tag}>secret-marker</{tag}>\n\nAfter");
            var safeText = string.Concat(hostile.Blocks.SelectMany(x => x.Spans).Select(x => x.Text));
            check(safeText.Contains("Before") && safeText.Contains("After") && !safeText.Contains("secret-marker"), "active subtree discarded with surrounding text preserved: " + tag);
        }
        var unsafeLinks = ProjectDescriptionParser.Parse("<a href='&#106;avascript:alert(1)'>bad</a> <img src='file:///C:/private.png'> <a href='https://user:secret@example.org'>credentials</a>");
        check(unsafeLinks.Blocks.SelectMany(x => x.Spans).All(x => x.Link is null), "entity-encoded and credential-bearing HTML links cannot bypass URL validation");
        check(ProjectDescriptionParser.Parse("<!-- only comment -->").Blocks.Count == 0, "comment-only description renders no markup or empty cards");
        var malformed = ProjectDescriptionParser.Parse("<div><h2>Title</h2><p>Readable <b>broken HTML");
        check(string.Concat(malformed.Blocks.SelectMany(x => x.Spans).Select(x => x.Text)).Contains("broken HTML"), "malformed formatting is recovered as readable text");
        try { ProjectDescriptionParser.Parse(string.Concat(Enumerable.Repeat("<div>", 100)) + "deep" + string.Concat(Enumerable.Repeat("</div>", 100))); throw new Exception("Expected complexity guard"); }
        catch (InvalidDataException) { check(true, "excessive HTML nesting is rejected within native render model bounds"); }

        var sourceVersions = new[] { "1.16.5", "1.20", "1.20.1", "1.21", "1.21.1", "1.21.4", "1.21.11", "26.1", "26.2-rc-1", "26.3-rc-2", "26.3", "1.21.1", " " };
        using var details = new ProjectDetailsViewModel(new ModrinthProject { Id = "project1", GameVersions = sourceVersions }, document, "", "", [], _ => { });
        check(details.MinecraftVersionPreview.Count == 6 && details.MinecraftVersionPreview.First() == "26.3" && !details.MinecraftVersionPreview.Contains("26.2-rc-1"), "compatibility preview shows at most six actual release values in numeric order");
        check(details.HasMoreMinecraftVersions && details.AllMinecraftVersionsLabel == "Все версии (11)", "complete compatibility list is explicitly counted and collapsible");
        check(details.MinecraftVersionGroups.Count == 2 && details.MinecraftVersionGroups[1].Versions.Contains("26.2-rc-1"), "release candidates remain available in a separate labelled group");
        check(details.MinecraftVersionGroups.SelectMany(x => x.Versions).Order().SequenceEqual(sourceVersions.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Order()) && !details.MinecraftVersionGroups.SelectMany(x => x.Versions).Contains("1.20.2"), "compatibility chips preserve exact discrete API values without inventing ranges");
        using var absent = new ProjectDetailsViewModel(new(), ProjectDescription.Empty, "", "", [], _ => { });
        check(!absent.HasMoreMinecraftVersions && absent.MinecraftVersionPreview.Count == 0 && absent.MinecraftVersionCountLabel.Contains("не указаны"), "missing compatibility metadata displays an honest empty state");
        using var shortList = new ProjectDetailsViewModel(new() { GameVersions = ["1.21.1"] }, ProjectDescription.Empty, "", "", [], _ => { });
        check(!shortList.HasMoreMinecraftVersions && shortList.MinecraftVersionPreview.SequenceEqual(new[] { "1.21.1" }), "short compatibility list needs no redundant expander");

        var folder = Path.Combine(root, "readable-description"); Directory.CreateDirectory(folder);
        var reader = new ProjectDescriptionView { Document = document, MaxWidth = 920, Margin = new Thickness(24) };
        var window = new Window { Classes = { "qc-app" }, Content = new ScrollViewer { Content = reader, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, Width = 1060, Height = 900 };
        window.Show();
        foreach (var width in new[] { 1060, 560 })
        {
            window.Width = width; Dispatcher.UIThread.RunJobs(); await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            check(reader.Bounds.Width <= window.ClientSize.Width && reader.GetVisualDescendants().OfType<Button>().All(x => x.Bounds.Width <= reader.Bounds.Width), "formatted description and link labels fit window width " + width);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(folder, "description-" + width + ".png"), PngBitmapEncoderOptions.Default);
        }
        window.Close();
    }
}
