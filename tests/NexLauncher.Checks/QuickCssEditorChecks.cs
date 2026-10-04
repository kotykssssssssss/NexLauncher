using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.QuickCss;
using NexLauncher.ViewModels;
using NexLauncher.Views;
using SkiaSharp;

internal static class QuickCssEditorChecks
{
    private const string Valid = ":root { --accent: #78dcca; }\r\n#app { background: var(--accent); }\r\nbutton:hover { opacity: .9; }";
    private const string Invalid = """
        /* Quick CSS */
        #app {
            background: #zzzzzz;
            colorrr: white;
            padding 12px;
        }
        @import url("https://example.com/theme.css");
        """;
    public static async Task RunAsync(string root, Action<bool,string> check)
    {
        var folder=Path.Combine(root,"quickcss-editor space Мир");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"theme.css"); var files=new QuickCssEditorFiles();
        await File.WriteAllTextAsync(path,Valid,new UTF8Encoding(true));
        var snapshot=await files.LoadAsync(path,default);
        check(snapshot.Text==Valid && snapshot.Revision.Length==64,"CSS editor reads BOM/CRLF/Unicode paths as UTF-8 with a revision snapshot");
        var changed=await files.SaveAsync(path,Valid+"\r\n/* saved */",snapshot.Revision,default);
        check(await File.ReadAllTextAsync(path)==changed.Text && changed.Revision!=snapshot.Revision && Directory.GetFiles(folder,"*.tmp").Length==0,"CSS editor save atomically commits selected file and cleans temporary output");
        await File.WriteAllTextAsync(path,"/* external change */");
        await RejectAsync<InvalidDataException>(()=>files.SaveAsync(path,Valid,changed.Revision,default),check,"CSS editor rejects stale file revision rather than overwriting an external edit");
        check(await File.ReadAllTextAsync(path)=="/* external change */","conflict preserves the external file contents");
        snapshot=await files.LoadAsync(path,default);
        await RejectAsync<InvalidDataException>(()=>files.SaveAsync(path,new string('x',128*1024+1),snapshot.Revision,default),check,"oversized editor content cannot overwrite CSS file");
        await RejectAsync<OperationCanceledException>(()=>files.SaveAsync(path,Valid,snapshot.Revision,new CancellationToken(true)),check,"cancelled editor save does not publish partial content");
        check(await File.ReadAllTextAsync(path)==snapshot.Text && Directory.GetFiles(folder,"*.tmp").Length==0,"failed/cancelled saves preserve previous theme without temporary files");
        var invalidFile=Path.Combine(folder,"invalid.css");await File.WriteAllBytesAsync(invalidFile,[0xff,0xfe,0,1]);
        await RejectAsync<InvalidDataException>(()=>files.LoadAsync(invalidFile,default),check,"editor rejects invalid UTF-8 instead of replacing characters silently");
        await RejectAsync<InvalidDataException>(()=>files.LoadAsync("relative.css",default),check,"editor requires an explicit selected full CSS path");
        var originalAccent=Application.Current!.Resources["NxAccentColor"];
        var inspected=QuickCssStyleApplier.Inspect(QuickCssParser.Parse(Valid));
        check(inspected.Count==0 && Equals(Application.Current.Resources["NxAccentColor"],originalAccent),"editor inspection reuses runtime validation without applying theme or changing accent resources");
        inspected=QuickCssStyleApplier.Inspect(QuickCssParser.Parse(Invalid));
        check(inspected.Any(x=>x.Line==3)&&inspected.Any(x=>x.Line==4)&&inspected.Any(x=>x.Line==5)&&inspected.Any(x=>x.Line==7),"syntax/value/property/@import diagnostics point to the correct editor lines");
        check(QuickCssStyleApplier.Inspect(QuickCssParser.Parse("#unknown { color: red; } #app { color: var(--missing); }")).Count==2,"editor marks unknown public selectors and unresolved variables using the existing engine");
        await File.WriteAllTextAsync(path,Valid);
        var applies=0;
        using(var vm=new QuickCssEditorViewModel(path,files,async selected=>{check(selected==path && await File.ReadAllTextAsync(selected)==Valid,"editor apply uses the exact file it saved");applies++;}))
        {
            check(await vm.LoadAsync() && !vm.IsDirty,"editor load does not create a dirty draft");
            vm.Text=Invalid;await vm.ValidationTask;
            check(vm.IsDirty && vm.Diagnostics.Count==4 && applies==0,"live editing validates without saving or applying draft");
            check(await File.ReadAllTextAsync(path)==Valid,"typing an invalid CSS draft leaves the disk theme unchanged");
            vm.Text=Invalid;vm.Text=Valid;await vm.ValidationTask;
            check(vm.Diagnostics.Count==0 && !vm.IsDirty,"debounced latest validation removes obsolete red errors after correction");
            vm.Text=Valid+"\n/* draft */";check(await vm.SaveAsync(false)&&!vm.IsDirty&&applies==0,"Save persists draft without forcing theme enable/application");
            vm.Text=Valid;check(await vm.SaveAsync(true)&&applies==1,"Save and Apply explicitly invokes existing Quick CSS integration");
            vm.Text=Invalid;await File.WriteAllTextAsync(path,"/* external */");
            check(!await vm.SaveAsync(false)&&vm.IsDirty&&vm.Text==Invalid&&vm.Status.Contains("вне этого окна"),"external edit conflict keeps the unsaved editor draft available for recovery");
            check(await vm.LoadAsync()&&!vm.IsDirty&&vm.Text=="/* external */","explicit reload adopts latest external contents and clears old draft");
        }
        using(var failedApply=new QuickCssEditorViewModel(path,files,_=>throw new IOException("test apply failure")))
        {
            await failedApply.LoadAsync();failedApply.Text="/* saved but apply fails */";
            check(!await failedApply.SaveAsync(true)&&!failedApply.IsDirty&&await File.ReadAllTextAsync(path)==failedApply.Text&&failedApply.Status.StartsWith("Файл сохранён"),"failed theme application reports the already committed save honestly");
        }
        await Window(folder,path,check);
    }
    private static async Task Window(string folder,string path,Action<bool,string> check)
    {
        await File.WriteAllTextAsync(path,Valid);
        var store=new ConfigurationStore(Path.Combine(folder,"settings"));
        var shell=new MainWindowViewModel(store,new FakeMinecraft(),new FakeAccounts());await shell.InitializeAsync();
        var owner=new MainWindow(shell);owner.Show();Dispatcher.UIThread.RunJobs();
        shell.QuickCss.FilePath=path;
        await shell.QuickCss.OpenCommand.ExecuteAsync(null);Dispatcher.UIThread.RunJobs();
        var window=owner.OwnedWindows.OfType<QuickCssEditorWindow>().Single();
        var editor=window.FindControl<TextEditor>("Editor")!;var vm=(QuickCssEditorViewModel)window.DataContext!;
        await vm.ValidationTask;Dispatcher.UIThread.RunJobs();
        check(editor.Text==Valid&&editor.ShowLineNumbers&&editor.SyntaxHighlighting.Name=="NexLauncher Quick CSS","settings command opens a separate owned CSS editor with line numbers and native highlighting");
        check(editor.SyntaxHighlighting.MainRuleSet.Rules.All(x=>!x.Regex.IsMatch("")),"syntax highlighting rules cannot produce an empty match/endless highlighting loop");
        check(!shell.QuickCss.Enabled&&!editor.Options.EnableHyperlinks&&!editor.Options.EnableEmailHyperlinks,"opening editor does not enable theme or activate links from untrusted CSS");
        editor.Text=Invalid;await vm.ValidationTask;Dispatcher.UIThread.RunJobs();
        check(vm.Text==Invalid&&window.Errors.LineCount==4,"actual editor typing updates shared diagnostics and error underline renderer");
        using(var frame=window.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(folder,"editor-errors-1040.png"),PngBitmapEncoderOptions.Default);
            using var stream=new MemoryStream();frame.Save(stream,PngBitmapEncoderOptions.Default);using var pixels=SKBitmap.Decode(stream.ToArray());
            var origin=editor.TranslatePoint(new Point(0,0),window)!.Value;var red=0;
            for(var y=(int)origin.Y;y<(int)(origin.Y+editor.Bounds.Height);y++)for(var x=(int)origin.X;x<(int)(origin.X+editor.Bounds.Width);x++)
            {var color=pixels.GetPixel(x,y);if(color.Red>225&&color.Green is >60 and <160&&color.Blue is >80 and <180)red++;}
            check(red>20,"red squiggles are actually drawn inside editor text viewport");
        }
        var first=vm.Diagnostics.First(x=>x.Line==3);window.FindControl<ListBox>("Problems")!.SelectedItem=first;
        check(editor.TextArea.Caret.Line==3,"selecting an error navigates to the correct source line");
        window.Width=640;window.Height=560;Dispatcher.UIThread.RunJobs();
        using(var frame=window.CaptureRenderedFrame())frame!.Save(Path.Combine(folder,"editor-errors-640.png"),PngBitmapEncoderOptions.Default);
        check(editor.Bounds.Width>450&&editor.Bounds.Height>100,"CSS editor keeps a usable viewport when resized narrower");
        editor.Text="#app { "+new string('x',16000)+" }";await vm.ValidationTask;Dispatcher.UIThread.RunJobs();
        using(var longFrame=window.CaptureRenderedFrame())check(window.Errors.LineCount==1&&longFrame!.PixelSize.Width==640,"very long invalid line renders bounded underlines without breaking the editor viewport");
        editor.Text=Invalid;await vm.ValidationTask;Dispatcher.UIThread.RunJobs();
        window.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.S,KeyModifiers=KeyModifiers.Control});
        for(var attempt=0;attempt<100&&vm.IsBusy;attempt++){await Task.Delay(10);Dispatcher.UIThread.RunJobs();}
        check(!vm.IsDirty&&await File.ReadAllTextAsync(path)==Invalid,"Ctrl+S routed keyboard shortcut saves the current editor draft");
        editor.Text=Invalid+"\n/* unsaved */";
        window.Close();Dispatcher.UIThread.RunJobs();
        check(window.IsVisible&&window.FindControl<Border>("ConfirmPanel")!.IsVisible,"closing a dirty editor preserves draft and asks before discarding it");
        window.FindControl<Button>("DiscardButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        shell.QuickCss.FilePath="";await shell.QuickCss.OpenCommand.ExecuteAsync(null);Dispatcher.UIThread.RunJobs();
        window=owner.OwnedWindows.OfType<QuickCssEditorWindow>().Single();vm=(QuickCssEditorViewModel)window.DataContext!;
        check(File.Exists(shell.QuickCss.FilePath)&&!shell.QuickCss.Enabled&&vm.Text.Contains(":root"),"first editor open creates a managed example without applying it or requiring an external editor");
        vm.Text="#app { background: #234567; }";await vm.SaveApplyCommand.ExecuteAsync(null);Dispatcher.UIThread.RunJobs();
        check(shell.QuickCss.Enabled&&owner.Background is ISolidColorBrush brush&&brush.Color==Color.Parse("#234567"),"Save and Apply enables and updates the existing main-window Quick CSS runtime");
        vm.Text+="\n/* unsaved */";owner.Close();Dispatcher.UIThread.RunJobs();
        check(owner.IsVisible&&window.FindControl<Border>("ConfirmPanel")!.IsVisible,"launcher close cannot silently discard its CSS editor draft");
        window.FindControl<Button>("DiscardButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();owner.Close();
    }
    private static async Task RejectAsync<T>(Func<Task> action,Action<bool,string> check,string label) where T:Exception
    {try{await action();}catch(T){check(true,label);return;}throw new Exception("Expected "+typeof(T).Name+": "+label);}
}
