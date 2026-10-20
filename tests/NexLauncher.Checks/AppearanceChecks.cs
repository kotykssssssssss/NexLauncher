using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexLauncher;
using NexLauncher.Services;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;
using NexLauncher.Views;

internal static class AppearanceChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "appearance Мир"); Directory.CreateDirectory(folder);
        foreach (var option in QuickCssAppearance.Options)
        {
            check(QuickCssAppearance.TryNormalize(option, option.DefaultValue, out _), "valid appearance default: " + option.Token);
            var document = QuickCssParser.Parse(":root { " + option.Token + ": " + option.DefaultValue + "; }");
            check(document.Appearance?.ContainsKey(option.Token) == true && QuickCssStyleApplier.Inspect(document).Count == 0,
                "reserved appearance token maps to supported typed styles: " + option.Token);
        }
        check(QuickCssParser.Parse(":root { --nx-accent: rgba(1,2,3,0); --nx-radius: 999px; --nx-font-size: NaN; }").Diagnostics.Count == 3,
            "transparent colors and out-of-range or non-finite layout values are diagnosed");
        check(QuickCssParser.Parse(":root { --chosen: rgb(10,20,30); --nx-accent: var(--chosen); }").Appearance?["--nx-accent"] == "#0A141E",
            "appearance tokens resolve real CSS variables and normalize colors");
        check(QuickCssParser.Parse("button { --nx-radius: 20px; }").Appearance?.Count == 0, "non-root appearance declarations cannot change global settings");
        var css = "/* --nx-accent: bogus; */\r\n:root { --nx-accent: red; --unknown: 7; } :root { --nx-accent: blue; }\r\nbutton { font-family: '--nx-accent: text;'; }";
        var updated = QuickCssRootTokens.Rewrite(css, new Dictionary<string, string> { ["--nx-accent"] = "#ABCDEF" });
        check(updated == css.Replace("--nx-accent: red", "--nx-accent: #ABCDEF").Replace("--nx-accent: blue", "--nx-accent: #ABCDEF"),
            "appearance synchronization replaces repeated real declarations and preserves strings/comments/CRLF/unknown variables");
        check(QuickCssRootTokens.Rewrite(updated, new Dictionary<string, string> { ["--nx-accent"] = "#ABCDEF" }) == updated,
            "repeated appearance sync never accumulates duplicate root blocks");
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asynchronous = new AppearanceViewModel(_ => saving.Task, () => true);
        asynchronous.Fields[0].Value = "#123456";
        var saveTask = asynchronous.ApplyCommand.ExecuteAsync(null);
        asynchronous.Fields[0].Value = "#654321";
        saving.SetResult(); await saveTask;
        check(asynchronous.HasPendingChanges && asynchronous.Fields[0].Value == "#654321", "input arriving during settings IO is preserved as a new draft");

        var store = new ConfigurationStore(Path.Combine(folder, "data"));
        Directory.CreateDirectory(store.DataDirectory);
        await File.WriteAllTextAsync(store.ConfigurationPath, "{\"FormatVersion\":2,\"Instances\":[],\"QuickCss\":{\"Enabled\":false}}");
        check((await store.LoadAsync()).QuickCss.Appearance.Count == 0, "old settings retain unchanged application appearance");
        var vm = new MainWindowViewModel(store, new FakeMinecraft(), new FakeAccounts()); await vm.InitializeAsync();
        var window = new MainWindow(vm) { Width = 1060, Height = 800 }; window.Show(); await Layout();
        using var service = new QuickCssService(window); await vm.QuickCss.AttachAsync(service, () => Task.FromResult<string?>(null));
        vm.CurrentPage = "settings";
        var appearance = vm.QuickCss.Appearance;
        AppearanceFieldViewModel Field(string token) => appearance.Fields.Single(x => x.Token == token);
        appearance.SelectedPreset = appearance.Presets[1]; appearance.PresetCommand.Execute(null);
        check(appearance.HasPendingChanges && Field("--nx-accent").Value == "#C7ACF5", "preset fills the same editable settings fields");
        await appearance.ApplyCommand.ExecuteAsync(null); await Layout();
        check(!vm.QuickCss.Enabled && !appearance.HasPendingChanges && window.Background is ISolidColorBrush bg && bg.Color == Color.Parse("#201C2C"),
            "appearance settings apply immediately without enabling Quick CSS");
        var stored = await store.LoadAsync();
        check(stored.QuickCss.Appearance["--nx-accent"] == "#C7ACF5", "appearance settings persist through the existing atomic config store");
        check(QuickCssParser.Parse(await File.ReadAllTextAsync(vm.QuickCss.FilePath)).Appearance?["--nx-accent"] == "#C7ACF5",
            "settings write the same tokens into the managed CSS file");
        var fieldText = await File.ReadAllTextAsync(vm.QuickCss.FilePath);
        Field("--nx-accent").Value = "not a color";
        await appearance.ApplyCommand.ExecuteAsync(null);
        check(appearance.Status.Contains("Акцент:") && await File.ReadAllTextAsync(vm.QuickCss.FilePath) == fieldText,
            "invalid appearance input never writes CSS or settings");
        appearance.RevertCommand.Execute(null);
        check(!appearance.HasPendingChanges && Field("--nx-accent").Value == "#C7ACF5", "revert restores the active values without disk writes");
        var validPath = vm.QuickCss.FilePath;
        vm.QuickCss.FilePath = "relative-appearance.css";
        Field("--nx-accent").Value = "#123456"; await appearance.ApplyCommand.ExecuteAsync(null);
        check(appearance.Status.StartsWith("Оформление не сохранено:") && (await store.LoadAsync()).QuickCss.FilePath == validPath,
            "relative CSS paths are rejected before writing appearance or settings");
        vm.QuickCss.FilePath = validPath; appearance.RevertCommand.Execute(null);

        await vm.QuickCss.CreateExampleCommand.ExecuteAsync(null); await Layout();
        check(!vm.QuickCss.HasDiagnostics && Field("--nx-background").Value == "#211C2F", "bundled example uses shared tokens and populates the same Settings controls");
        Field("--nx-background").Value = "#202C38";

        Field("--nx-radius").Number = 19; Field("--nx-card-padding").Number = 18; Field("--nx-font-size").Number = 16; Field("--nx-content-gap").Number = 20;
        await appearance.ApplyCommand.ExecuteAsync(null); await Layout();
        var panel = window.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("qc-appearance"));
        check(panel.CornerRadius == new CornerRadius(19) && panel.Padding == new Thickness(18) && window.FontSize == 16,
            "real settings change card rounding, spacing and inherited body font size");
        check(window.Background is ISolidColorBrush exampleBackground && exampleBackground.Color == Color.Parse("#202C38"),
            "Settings controls also change the actual bundled theme instead of being overridden by legacy variables");
        check(window.GetVisualDescendants().OfType<Grid>().Single(x => x.Classes.Contains("qc-content")).Margin == new Thickness(20),
            "content spacing token changes the real shell layout");
        check(window.GetVisualDescendants().OfType<AppearanceView>().Single().IsEffectivelyVisible, "appearance is integrated in the actual Settings page");
        foreach (var width in new[] { 1060, 1920 })
        {
            window.Width = width; await Layout();
            var scroller = panel.GetVisualAncestors().OfType<ScrollViewer>().First();
            var origin = panel.TranslatePoint(new Point(), (Visual)scroller.Content!)!.Value;
            scroller.Offset = new Vector(0, origin.Y); await Layout();
            check(panel.Bounds.Width <= window.ClientSize.Width && panel.Bounds.Width > 500, "appearance fields wrap inside available settings width at " + width);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(folder, "settings-" + width + ".png"), PngBitmapEncoderOptions.Default);
        }
        vm.QuickCss.Enabled = true; await vm.QuickCss.ApplyCommand.ExecuteAsync(null);
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, ":root { --nx-accent: #EFBA70; --nx-background: #172533; --nx-radius: 7px; } .card { padding: 13px; }");
        await vm.QuickCss.ReloadCommand.ExecuteAsync(null); await Layout();
        check(Field("--nx-accent").Value == "#EFBA70" && Field("--nx-radius").Value == "7px" && panel.CornerRadius == new CornerRadius(7),
            "saved CSS synchronizes settings and typed styles in the other direction");
        check(panel.Padding == new Thickness(13), "explicit CSS rules retain priority over common appearance tokens");
        var primary = panel.GetVisualDescendants().OfType<Button>().Single(x => x.Command == appearance.ApplyCommand);
        primary.IsEnabled = false; await Layout();
        var presenter = primary.GetVisualDescendants().OfType<ContentPresenter>().Single(x => x.Name == "PART_ContentPresenter");
        check(presenter.Background is ISolidColorBrush disabledBrush && disabledBrush.Color != Color.Parse(Field("--nx-accent").Value),
            "disabled primary action retains a distinct subdued background under custom accents");
        primary.ClearValue(InputElement.IsEnabledProperty); await Layout();
        Field("--nx-accent").Value = "#123456";
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, ":root { --nx-accent: #ABCDEF; }"); await vm.QuickCss.ReloadCommand.ExecuteAsync(null);
        check(Field("--nx-accent").Value == "#123456" && appearance.HasPendingChanges, "theme reload never discards unsaved settings edits");
        appearance.RevertCommand.Execute(null);
        check(Field("--nx-accent").Value == "#ABCDEF", "explicit re-read retrieves the latest CSS values");
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, ":root { --nx-accent: #ABCDE0; }");
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (Field("--nx-accent").Value != "#ABCDE0" && DateTime.UtcNow < deadline) await Layout();
        check(Field("--nx-accent").Value == "#ABCDE0", "real file watcher synchronizes appearance fields without clicking reload");
        var lastTheme = await File.ReadAllTextAsync(vm.QuickCss.FilePath);
        File.Delete(vm.QuickCss.FilePath); await vm.QuickCss.ReloadCommand.ExecuteAsync(null);
        check(Field("--nx-accent").Value == "#ABCDE0", "temporary CSS read failure preserves the last effective appearance snapshot");
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, lastTheme);
        await vm.QuickCss.OpenCommand.ExecuteAsync(null);
        var editorWindow = window.OwnedWindows.OfType<QuickCssEditorWindow>().Single();
        var editor = (QuickCssEditorViewModel)editorWindow.DataContext!; editor.Text += "\n/* unrelated unsaved CSS */";
        Field("--nx-accent").Value = "#F0C080"; await appearance.ApplyCommand.ExecuteAsync(null);
        check(editor.IsDirty && editor.Text.Contains("unrelated unsaved CSS") && QuickCssParser.Parse(editor.Text).Appearance?["--nx-accent"] == "#F0C080",
            "appearance applies through the real open editor without discarding its draft");
        check(!(await File.ReadAllTextAsync(vm.QuickCss.FilePath)).Contains("unrelated unsaved CSS"), "appearance synchronization does not silently save unrelated editor changes");
        var baseline = await File.ReadAllTextAsync(vm.QuickCss.FilePath);
        await File.AppendAllTextAsync(vm.QuickCss.FilePath, "\n/* external edit */");
        Field("--nx-accent").Value = "#AABBCC"; await appearance.ApplyCommand.ExecuteAsync(null);
        check(appearance.Status.StartsWith("Оформление не сохранено:") && (await File.ReadAllTextAsync(vm.QuickCss.FilePath)).Contains("external edit"),
            "conflicting external edits are preserved with an actionable error");
        // Explicitly reload/discard the fixture's draft before closing (normal UI protects dirty editors).
        await editor.LoadAsync(); editorWindow.Close();
        await File.WriteAllTextAsync(vm.QuickCss.FilePath, baseline + "\nbutton { opacity: .9; }");
        await appearance.ResetCommand.ExecuteAsync(null); await Layout();
        var reset = QuickCssParser.Parse(await File.ReadAllTextAsync(vm.QuickCss.FilePath));
        check(reset.Appearance?.All(x => x.Value == QuickCssAppearance.Options.Single(y => y.Token == x.Key).DefaultValue) == true &&
            reset.Rules.Any(x => x.Declarations.Any(y => y.Property == "opacity" && y.Value == ".9")), "reset restores common tokens and preserves unrelated CSS rules");
        vm.QuickCss.Enabled = false; await vm.QuickCss.ApplyCommand.ExecuteAsync(null); await Layout();
        check(window.Background is ISolidColorBrush restored && restored.Color == Color.Parse("#181A1D"), "reset and disable restore original application colors");
        check(panel.CornerRadius == new CornerRadius(12) && primary.CornerRadius == new CornerRadius(12) && window.FontSize == 14,
            "explicit default values retain identical semantics with CSS enabled or disabled");
        window.Close();
        var restart = new MainWindowViewModel(store, new FakeMinecraft(), new FakeAccounts()); await restart.InitializeAsync();
        var restartedWindow = new MainWindow(restart); restartedWindow.Show(); await Layout();
        check(restart.QuickCss.Appearance.Fields.Single(x => x.Token == "--nx-accent").Value == "#78D9B3", "restart loads the persisted common appearance through normal initialization");
        restartedWindow.Close();
        var beforeInvalidSave = await File.ReadAllTextAsync(store.ConfigurationPath);
        var invalid = await store.LoadAsync(); invalid.QuickCss.Appearance["--nx-accent"] = "url(file:///secret)";
        try { await store.SaveAsync(invalid); throw new Exception("Expected invalid appearance rejection"); }
        catch (InvalidDataException) { check(await File.ReadAllTextAsync(store.ConfigurationPath) == beforeInvalidSave, "invalid persisted appearance cannot replace recoverable settings"); }
    }
    private static async Task Layout() { Dispatcher.UIThread.RunJobs(); await Task.Delay(70); Dispatcher.UIThread.RunJobs(); }
}
