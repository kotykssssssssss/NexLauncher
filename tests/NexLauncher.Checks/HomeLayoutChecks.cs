using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;
using NexLauncher.Views;

internal static class HomeLayoutChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "home-layout"); Directory.CreateDirectory(folder);
        var missing = QuickCssParser.Parse(":root {\n --home-layout: tiles\n --background: #123;\n background: var(--background);\n}");
        check(missing.Diagnostics.Count == 1 && missing.Diagnostics[0].Line == 2 && missing.Diagnostics[0].Message.Contains("Пропущена «;»"), "missing semicolon is diagnosed at the actual layout declaration");
        check(missing.HomeLayout == HomeLayout.Tiles && missing.Rules.Single().Declarations.Single().Value == "#123", "missing separator recovery preserves the next variable and valid styles");
        foreach (var mode in Enum.GetValues<HomeLayout>())
        {
            var text = ":root { --home-layout: " + QuickCssHomeLayout.Token(mode) + "; }";
            check(QuickCssParser.Parse(text) is { Diagnostics.Count: 0 } doc && doc.HomeLayout == mode, "reserved home layout token works without ordinary style declarations: " + mode);
        }
        check(QuickCssParser.Parse(":root { --choice: list; --home-layout: var(--choice); }").HomeLayout == HomeLayout.List, "home layout can reference a regular CSS variable");
        check(QuickCssParser.Parse(":root { --home-layout: nonsense; }").Diagnostics.Single().Message.Contains("default, tiles или list"), "invalid layout values show the actual supported choices");
        check(QuickCssParser.Parse("button { --home-layout: tiles; }").HomeLayout is null, "a non-root variable cannot change the application layout");
        var original = "/* --home-layout: fake; */\r\n:root { --home-layout: tiles; --text: white; }\r\nbutton { font-family: '--home-layout: fake;'; }";
        var rewritten = QuickCssHomeLayout.Rewrite(original, HomeLayout.List);
        check(rewritten == original.Replace("--home-layout: tiles;", "--home-layout: list;"), "layout rewrite preserves comments, strings, other declarations and CRLF");
        check(QuickCssHomeLayout.Rewrite(rewritten, HomeLayout.List) == rewritten, "repeated selection does not grow or duplicate the CSS token");
        var recovered = QuickCssHomeLayout.Rewrite(":root {\r\n--home-layout: tiles\r\n--background: #123;\r\n}", HomeLayout.List);
        check(recovered.Contains("--home-layout: list;\r\n--background") && QuickCssParser.Parse(recovered).Diagnostics.Count == 0, "sync repairs a missing separator without swallowing the next property or newline");
        var duplicates = QuickCssHomeLayout.Rewrite(":root { --home-layout: tiles; } :root { --home-layout: default; }", HomeLayout.List);
        check(QuickCssParser.Parse(duplicates).HomeLayout == HomeLayout.List && !duplicates.Contains(": tiles") && !duplicates.Contains(": default"), "all repeated root tokens are synchronized without cascade surprises");
        var added = QuickCssHomeLayout.Rewrite("button { opacity: .7; }", HomeLayout.Tiles);
        check(added.StartsWith("button { opacity: .7; }") && QuickCssParser.Parse(added).HomeLayout == HomeLayout.Tiles, "themes without the token receive a separate root declaration");

        var css = Path.Combine(folder, "theme.css"); await File.WriteAllTextAsync(css, original);
        using (var editor = new QuickCssEditorViewModel(css, new(), _ => Task.CompletedTask))
        {
            await editor.LoadAsync(); editor.Text += "\r\n/* unsaved draft */";
            await editor.UpdateHomeLayoutAsync(HomeLayout.List);
            check(editor.IsDirty && editor.Text.Contains("unsaved draft") && editor.Text.Contains("--home-layout: list;"), "settings synchronization preserves the editor's unsaved draft");
            check(!(await File.ReadAllTextAsync(css)).Contains("unsaved draft") && (await File.ReadAllTextAsync(css)).Contains("--home-layout: list;"), "only the layout token is saved; unrelated draft edits are not applied");
            check(await editor.SaveAsync(false) && !editor.IsDirty, "synchronized draft retains a valid file revision and saves normally");
            var update = editor.UpdateHomeLayoutAsync(HomeLayout.Tiles);
            editor.Text += "\n/* typed while saving */";
            await update;
            check(editor.Text.Contains("typed while saving") && editor.IsDirty, "typing during layout file IO cannot be lost when the editor draft is merged");
            await File.AppendAllTextAsync(css, "\n/* external change */");
            try { await editor.UpdateHomeLayoutAsync(HomeLayout.Tiles); throw new Exception("Expected external-change protection"); }
            catch (InvalidDataException) { check((await File.ReadAllTextAsync(css)).Contains("external change"), "layout sync refuses to overwrite external editor changes"); }
        }

        var store = new ConfigurationStore(Path.Combine(folder, "user data Мир"));
        await store.SaveAsync(new() { Instances = Enumerable.Range(1, 8).Select(i => new GameInstance { Name = "Мир " + i + (i == 3 ? new string('x', 54) : ""), VersionId = "1.21.1", Loader = i % 2 == 0 ? ModLoader.Fabric : ModLoader.Vanilla, LoaderVersion = i % 2 == 0 ? "0.16.10" : "" }).ToList() });
        var vm = new MainWindowViewModel(store, new FakeMinecraft(), new FakeAccounts());
        await vm.InitializeAsync();
        check(vm.IsDefaultHomeLayout, "old settings without a layout retain the original default home screen");
        var window = new MainWindow(vm) { Width = 1060, Height = 800 }; window.Show(); await Layout();
        using var service = new QuickCssService(window);
        await vm.QuickCss.AttachAsync(service, () => Task.FromResult<string?>(null));
        vm.CurrentPage = "settings";
        var selector = window.FindControl<ComboBox>("HomeLayoutSelector")!;
        foreach (var mode in new[] { HomeLayout.Tiles, HomeLayout.List, HomeLayout.Default })
        {
            selector.SelectedItem = vm.HomeLayouts.Single(x => x.Layout == mode);
            await vm.HomeLayoutChangeTask; await Layout();
            check(vm.SelectedHomeLayout.Layout == mode && (await store.LoadAsync()).HomeLayout == mode, "settings selector immediately persists and selects " + mode);
            check(QuickCssParser.Parse(await File.ReadAllTextAsync(vm.QuickCss.FilePath)).HomeLayout == mode && !vm.QuickCss.Enabled, "settings updates CSS without implicitly enabling it: " + mode);
        }
        vm.QuickCss.Enabled = true; await vm.QuickCss.ApplyCommand.ExecuteAsync(null);
        foreach (var mode in new[] { HomeLayout.Tiles, HomeLayout.List, HomeLayout.Default })
        {
            await File.WriteAllTextAsync(vm.QuickCss.FilePath, ":root { --home-layout: " + QuickCssHomeLayout.Token(mode) + "; }");
            await vm.QuickCss.ReloadCommand.ExecuteAsync(null); await Layout();
            check(vm.SelectedHomeLayout.Layout == mode, "saved CSS changes the runtime layout and settings selector: " + mode);
            vm.CurrentPage = "play";
            foreach (var width in new[] { 1060, 1920 })
            {
                window.Width = width; await Layout();
                var view = window.GetVisualDescendants().OfType<HomeInstancesView>().Single();
                var list = view.FindControl<ListBox>(mode == HomeLayout.List ? "HomeList" : "HomeTiles")!;
                check(mode == HomeLayout.Default || list.IsEffectivelyVisible && list.ItemCount == 8 && list.Bounds.Width <= view.Bounds.Width, "real home template renders all selectable instances without overflow: " + mode + " at " + width);
                if (mode != HomeLayout.Default)
                {
                    list.SelectedItem = vm.Instances[2]; await Layout();
                    check(vm.SelectedInstance?.Id == vm.Instances[2].Id && view.GetVisualDescendants().OfType<Button>().Any(x => x.Classes.Contains("qc-play-button") && x.Command == vm.PrimaryCommand && x.IsEffectivelyVisible), "layout selection uses the existing selected instance and launch command: " + mode + " at " + width);
                }
                using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(folder, mode + "-" + width + ".png"), PngBitmapEncoderOptions.Default);
            }
        }
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, ":root { --home-layout: tiles; } .home-tile { background: #103428; border-radius: 17px; }");
        await vm.QuickCss.ReloadCommand.ExecuteAsync(null); await Layout();
        var themedTiles = window.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("home-tile")).ToArray();
        check(themedTiles.Length == 8 && themedTiles.All(x => x.Background is Avalonia.Media.ISolidColorBrush brush && brush.Color == Avalonia.Media.Color.Parse("#103428") && x.CornerRadius == new CornerRadius(17)),
            "Quick CSS overrides standard tile background and radius for every instance");
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, ":root { --home-layout: list; }");
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!vm.IsListHomeLayout && DateTime.UtcNow < deadline) await Layout();
        check(vm.IsListHomeLayout, "file watcher changes the home layout automatically after CSS save");
        var cssBackup = await File.ReadAllTextAsync(vm.QuickCss.FilePath);
        File.Delete(vm.QuickCss.FilePath); await vm.QuickCss.ReloadCommand.ExecuteAsync(null);
        check(vm.IsListHomeLayout, "temporary CSS read failure preserves both the previous styles and layout");
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, cssBackup);
        vm.QuickCss.Enabled = false; await vm.QuickCss.ApplyCommand.ExecuteAsync(null);
        check(vm.IsDefaultHomeLayout && vm.ShowDefaultHomeSelector, "disabling CSS restores the stored settings layout and original selector");
        await vm.QuickCss.OpenCommand.ExecuteAsync(null);
        var liveEditor = window.OwnedWindows.OfType<QuickCssEditorWindow>().Single();
        var editorModel = (QuickCssEditorViewModel)liveEditor.DataContext!;
        editorModel.Text += "\n/* live editor draft */";
        selector.SelectedItem = vm.HomeLayouts.Single(x => x.Layout == HomeLayout.Tiles);
        await vm.HomeLayoutChangeTask; await Layout();
        check(editorModel.IsDirty && editorModel.Text.Contains("live editor draft") && QuickCssParser.Parse(editorModel.Text).HomeLayout == HomeLayout.Tiles,
            "real Settings selection updates the already-open CSS editor without discarding its draft");
        check(!(await File.ReadAllTextAsync(vm.QuickCss.FilePath)).Contains("live editor draft"), "real editor synchronization does not silently save unrelated edits");
        await editorModel.SaveAsync(false); liveEditor.Close();
        selector.SelectedItem = vm.HomeLayouts.Single(x => x.Layout == HomeLayout.Default); await vm.HomeLayoutChangeTask;
        var iconWindow = new QuickCssEditorWindow(); iconWindow.Show(window); await Layout();
        check(window.Icon is not null && iconWindow.Icon is not null, "main window and separate Quick CSS editor use the branded icon");
        iconWindow.Close();
        using (var icon = AssetLoader.Open(new Uri("avares://NexLauncher/Assets/branding/NexLauncher.ico")))
        using (var input = new BinaryReader(icon))
            check(input.ReadUInt16() == 0 && input.ReadUInt16() == 1 && input.ReadUInt16() == 7, "bundled ICO provides seven sizes for Windows and DPI scaling");
        window.Close();
        var loaded = await store.LoadAsync();
        check(loaded.Instances.Count == 8 && loaded.HomeLayout == HomeLayout.Default, "layout changes preserve existing instances across restart");
    }
    private static async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(60); Dispatcher.UIThread.RunJobs(); }
}
