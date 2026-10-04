using System.Net;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CmlLib.Core.Auth;
using NexLauncher;
using NexLauncher.Models;
using NexLauncher.Services;
using NexLauncher.Services.Network;
using NexLauncher.Services.QuickCss;
using NexLauncher.Services.Skins;
using NexLauncher.Services.Storage;
using NexLauncher.ViewModels;
using NexLauncher.Views;
using SkiaSharp;

internal static class SkinChecks
{
    private static readonly LauncherAccount Online = new("11111111111111111111111111111111", "Licensed", "11111111111111111111111111111111");
    private static readonly LauncherAccount Local = LocalAccountIdentity.CreateProfile("Skin_Player");
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "skins"); Directory.CreateDirectory(directory);
        var validator = new SkinValidator(); var bytes = Png(); var image = validator.Validate(bytes);
        check(image.Pixels.Length == 64*64*4 && !image.Legacy && image.Png.AsSpan(0,8).SequenceEqual(bytes.AsSpan(0,8)), "valid PNG skin decodes and sanitizes to canonical 64×64");
        check(image.Pixels[(8*64+40)*4+3] == 128, "modern outer skin layer retains transparency");
        var translucentBase = Png(baseAlpha: 70); var normalized = validator.Validate(translucentBase);
        check(normalized.NormalizedTransparency && normalized.Pixels[(8*64+8)*4+3] == 255, "base alpha follows Minecraft opacity semantics while reporting normalization");
        foreach (var pair in new[] { ("not PNG", Encoding.UTF8.GetBytes("<script>not a PNG</script>")), ("HD/wrong dimensions", Png(128,128)),
            ("wrong height", Png(64,63)), ("truncated", bytes[..^12]), ("oversize", new byte[SkinValidator.MaxBytes+1]), ("transparent", Png(baseAlpha:0, overlay:false)) })
            Reject(() => validator.Validate(pair.Item2), check, "skin validator rejects " + pair.Item1);
        var corrupt = bytes.ToArray(); corrupt[^1] ^= 1;
        Reject(() => validator.Validate(corrupt), check, "PNG CRC corruption is rejected before decode");
        Reject(() => validator.Validate(Chunk(bytes, "acTL", new byte[8])), check, "animated PNG is rejected even when chunk CRC is valid");
        var ancillary = Chunk(bytes, "tEXt", Encoding.ASCII.GetBytes("Comment\0not retained"));
        check(!Encoding.ASCII.GetString(validator.Validate(ancillary).Png).Contains("not retained"), "import strips ancillary PNG metadata rather than copying untrusted chunks");
        var legacy = validator.Validate(Png(64,32, overlay:false));
        check(legacy.Legacy && legacy.Pixels[(52*64+20)*4] == legacy.Pixels[(20*64+7)*4], "legacy 64×32 is converted to mirrored modern left limbs");
        Reject(() => SkinValidator.ValidateModel(legacy, SkinModel.Slim), check, "legacy skin cannot silently use incorrect Slim arm layout");
        SkinValidator.ValidateModel(image, SkinModel.Classic); SkinValidator.ValidateModel(image, SkinModel.Slim);
        check(true, "modern skin supports explicit Classic and Slim model choices");
        await RejectAsync<OperationCanceledException>(() => validator.ImportAsync("unused", new CancellationToken(true)), check, "skin import honors cancellation before reading a file");
        await Storage(directory, validator, image, check);
        await Api(validator, image, check);
        await Ui(directory, validator, image, check);
        var renderer = new SkinPreviewRenderer();
        var front = renderer.Render(image, SkinModel.Classic, 0, 1, default); var back = renderer.Render(image, SkinModel.Classic, Math.PI, 1, default);
        var slim = renderer.Render(image, SkinModel.Slim, 0, 1, default);
        check(!front.SequenceEqual(back) && !front.SequenceEqual(slim), "preview renders actual front/back textures and distinct 4px/3px arms");
        var withoutOuter = validator.Validate(Png(overlay:false));
        check(!front.SequenceEqual(renderer.Render(withoutOuter, SkinModel.Classic, 0, 1, default)), "preview includes the outer hat/jacket/limb layers");
        using var decoded = SKBitmap.Decode(front);
        check(decoded.Width == 512 && decoded.Height == 512, "preview produces a bounded full-body rendering");
        await File.WriteAllBytesAsync(Path.Combine(directory, "preview-front.png"), front); await File.WriteAllBytesAsync(Path.Combine(directory, "preview-back.png"), back);
        await File.WriteAllBytesAsync(Path.Combine(directory, "preview-slim.png"), slim);
    }
    private static async Task Storage(string root, SkinValidator validator, SkinImage image, Action<bool,string> check)
    {
        var data = Path.Combine(root,"storage space Мир"); var storage = new SkinStorage(data,validator);
        check((await storage.LoadAsync(Local,default)).Image is null, "old account data without skins loads as default without migration or writes");
        check(!Directory.Exists(Path.Combine(data,"skins")), "reading a missing skin does not create data or rewrite old profiles");
        var forgedLegacy=new SkinImage(Png(64,32),image.Pixels,false,false);
        await RejectAsync<SkinException>(()=>storage.SaveAsync(Local,forgedLegacy,SkinModel.Slim,default),check,"service boundary revalidates legacy PNG geometry instead of trusting caller metadata");
        await storage.SaveAsync(Local,image,SkinModel.Slim,default);
        var fresh = new SkinStorage(data,new SkinValidator()); var loaded = await fresh.LoadAsync(Local,default);
        check(loaded.Model == SkinModel.Slim && loaded.LocalOnly && loaded.Image!.Png.SequenceEqual(image.Png), "local imported skin/model survive a fresh storage instance and Unicode path");
        var renamed = Local with { Username="Renamed_Player" };
        check((await fresh.LoadAsync(renamed,default)).Image is not null && fresh.AccountDirectory(renamed) == fresh.AccountDirectory(Local), "skin identity survives nickname rename when account ID is unchanged");
        var other = LocalAccountIdentity.CreateProfile("Other_Player");
        check((await fresh.LoadAsync(other,default)).Image is null && fresh.AccountDirectory(other) != fresh.AccountDirectory(Local), "skins map to separate account IDs");
        var sameIdOnline = Local with { Type=AccountType.Microsoft };
        check(fresh.AccountDirectory(sameIdOnline) != fresh.AccountDirectory(Local), "same ID across account types cannot share a skin storage slot");
        var traversal = Local with { Id="../../outside/skin.png" };
        check(fresh.AccountDirectory(traversal).StartsWith(Path.Combine(data,"skins") + Path.DirectorySeparatorChar) && !fresh.AccountDirectory(traversal).Contains(".."), "untrusted account ID is hashed into a safe managed directory");
        var folder = fresh.AccountDirectory(Local); var metadata = await AtomicJson.ReadAsync<StoredSkin>(Path.Combine(folder,"skin.json"),default);
        check(metadata!.FormatVersion == 1 && !JsonSerializer.Serialize(metadata).Contains("source") && Directory.GetFiles(folder,"*.png").Length == 1, "versioned metadata stores managed filename/hash without imported source path");
        await RejectAsync<OperationCanceledException>(() => fresh.SaveAsync(Local,image,SkinModel.Classic,new CancellationToken(true)),check,"cancelled skin save leaves committed selection intact");
        check((await fresh.LoadAsync(Local,default)).Model == SkinModel.Slim, "cancelled write preserves the previous skin/model");
        await fresh.SaveAsync(Local,image,SkinModel.Classic,default);
        check((await fresh.LoadAsync(Local,default)).Model == SkinModel.Classic && Directory.GetFiles(folder,"*.png").Length==1, "changing model commits atomically and removes only previous managed PNG");
        metadata = await AtomicJson.ReadAsync<StoredSkin>(Path.Combine(folder,"skin.json"),default);
        var pngPath = Path.Combine(folder,metadata!.FileName); var original = await File.ReadAllBytesAsync(pngPath);
        await File.WriteAllBytesAsync(pngPath,[0,1,2]);
        await RejectAsync<SkinException>(() => fresh.LoadAsync(Local,default),check,"corrupted stored PNG is rejected by hash without overwriting it");
        check((await File.ReadAllBytesAsync(pngPath)).SequenceEqual(new byte[]{0,1,2}),"corrupted stored skin remains available for diagnosis");
        await File.WriteAllBytesAsync(pngPath,original);
        var metadataPath = Path.Combine(folder,"skin.json"); var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
        await AtomicJson.WriteAsync(metadataPath,metadata with { FileName="../outside.png" },default);
        await RejectAsync<SkinException>(()=>fresh.LoadAsync(Local,default),check,"metadata path traversal is rejected");
        await File.WriteAllBytesAsync(metadataPath,metadataBytes); await File.WriteAllTextAsync(metadataPath,"{broken");
        await RejectAsync<InvalidDataException>(()=>fresh.SaveAsync(Local,image,SkinModel.Classic,default),check,"corrupt skin metadata is never silently overwritten by a new import");
        check(await File.ReadAllTextAsync(metadataPath)=="{broken","corrupt metadata original preserved");
        await File.WriteAllBytesAsync(metadataPath,metadataBytes);
        await AtomicJson.WriteAsync(metadataPath,metadata with { FormatVersion=2 },default);
        await RejectAsync<SkinException>(()=>fresh.SaveAsync(Local,image,SkinModel.Classic,default),check,"unknown future skin format is preserved rather than migrated silently");
        await File.WriteAllBytesAsync(metadataPath,metadataBytes);
        await AtomicJson.WriteAsync(metadataPath,metadata with { Model=(SkinModel)42 },default);
        await RejectAsync<SkinException>(()=>fresh.LoadAsync(Local,default),check,"invalid persisted model cannot escape Classic/Slim validation");
        await File.WriteAllBytesAsync(metadataPath,metadataBytes);
        await fresh.ResetAsync(Local,default);
        check((await fresh.LoadAsync(Local,default)).Image is null && Directory.GetFiles(folder,"*.png").Length==0, "local reset deletes only managed metadata and its validated PNG");
        var untouched = Path.Combine(folder,"manual.png"); await File.WriteAllTextAsync(untouched,"keep"); await fresh.ResetAsync(Local,default);
        check(await File.ReadAllTextAsync(untouched)=="keep","skin reset preserves unknown user files");
        var localService = new OfflineSkinService(fresh); var forbidden = new ForbiddenSkinService(); var router = new SkinService(forbidden,localService);
        await router.ApplyAsync(Local,image,SkinModel.Slim,default); await router.GetAsync(Local,default); await router.ResetAsync(Local,default);
        check(forbidden.Calls==0,"local skin load/save/reset never access Microsoft API, credentials or network");
    }
    private static async Task Api(SkinValidator validator,SkinImage image,Action<bool,string> check)
    {
        using var transport = new SkinHttp(image.Png); using var client = new HttpClient(transport);
        var api = new MinecraftSkinApi(client);
        await RejectAsync<SkinException>(()=>api.ProfileAsync("secret\r\ninvalid",default),check,"malformed bearer credential never reaches HTTP or a raw exception message");
        var profile = await api.ProfileAsync("test-credential",default);
        check(profile.Id==Online.Uuid && profile.Skins.Single().Variant=="SLIM", "Minecraft profile maps actual skins state/variant/url from Services JSON");
        await api.UploadAsync("test-credential",image,SkinModel.Slim,default);
        check(transport.LastMethod==HttpMethod.Post && transport.LastPath=="/minecraft/profile/skins" && transport.Variant=="slim" && transport.FileName=="skin.png" && transport.FileBytes!.SequenceEqual(image.Png), "upload constructs PNG multipart with slim variant on the current Minecraft Services endpoint");
        await api.UploadAsync("test-credential",image,SkinModel.Classic,default);
        check(transport.Variant=="classic" && transport.FileType=="image/png", "Classic uses lowercase classic variant and explicit PNG content type");
        await api.ResetAsync("test-credential",default);
        check(transport.LastMethod==HttpMethod.Delete && transport.LastPath=="/minecraft/profile/skins/active", "reset uses active skin DELETE endpoint rather than obsolete Mojang API");
        check(transport.AuthPresent && transport.AllUserAgents && transport.SecretsOnTextures==0, "bearer is request-local on fixed Services host and identifying User-Agent is set");
        foreach (var status in new[] { HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,HttpStatusCode.NotFound,HttpStatusCode.BadRequest,HttpStatusCode.TooManyRequests,HttpStatusCode.BadGateway,HttpStatusCode.Redirect })
        {
            transport.Status=status;
            await RejectAsync<SkinException>(()=>api.ProfileAsync("test-credential",default),check,"skin API handles status " + (int)status + " without exposing raw server credentials");
        }
        transport.Status=HttpStatusCode.OK; transport.InvalidJson=true;
        await RejectAsync<SkinException>(()=>api.ProfileAsync("test-credential",default),check,"invalid Minecraft Services JSON is handled safely");
        transport.InvalidJson=false; transport.FailNetwork=true;
        await RejectAsync<SkinException>(()=>api.ResetAsync("test-credential",default),check,"skin network failure maps to safe user error");
        transport.FailNetwork=false;
        await RejectAsync<OperationCanceledException>(()=>api.ProfileAsync("test-credential",new CancellationToken(true)),check,"skin API propagates caller cancellation without claiming successful mutation");
        foreach (var url in new[] { "file:///skin.png", "https://localhost/texture/x", "https://textures.minecraft.net.evil/texture/x", "https://user:password@textures.minecraft.net/texture/x", "https://textures.minecraft.net/texture/x?secret=x" })
            Reject(()=>MinecraftSkinService.OfficialTexture(url),check,"unsafe texture URL rejected: " + new Uri(url).Host);
        check(MinecraftSkinService.OfficialTexture("http://textures.minecraft.net/texture/x").Scheme=="https","official legacy HTTP texture URL is upgraded to HTTPS");
        var sessions = new SkinAccounts(); var service = new MinecraftSkinService(sessions,api,new LauncherHttp(client),validator);
        var current = await service.GetAsync(Online,default);
        check(current.Image is not null && current.Model==SkinModel.Slim && sessions.RequestedId==Online.Id && sessions.ActiveAccountId=="other-active", "Microsoft skin refresh uses requested saved account without changing active launch identity");
        await service.ApplyAsync(Online,image,SkinModel.Classic,default);
        check(transport.Uploads==3 && transport.Profiles>=2 && transport.SecretsOnTextures==0,"successful skin upload refreshes profile/texture without leaking bearer to texture CDN");
        await service.ResetAsync(Online,default);
        check(transport.Resets==2,"Microsoft reset invokes account-level Services reset and refresh");
        transport.ProfileJson="{\"id\":\""+Online.Uuid+"\",\"name\":\"Licensed\",\"skins\":[]}";
        var fallback=await service.GetAsync(Online,default);
        check(fallback.Image is null && fallback.Message.Contains("манекен"),"missing active skin is honestly represented as a neutral default placeholder");
        transport.ProfileJson="{\"id\":\""+Online.Uuid+"\",\"name\":\"Licensed\",\"skins\":[{\"state\":\"ACTIVE\",\"variant\":null}]}";
        await RejectAsync<SkinException>(()=>service.GetAsync(Online,default),check,"missing skin variant fails safely instead of crashing on null metadata");
        transport.ProfileJson="{\"id\":\""+Online.Uuid+"\",\"name\":\"Licensed\",\"skins\":null}";
        await RejectAsync<SkinException>(()=>api.ProfileAsync("test-credential",default),check,"null skins collection is rejected as an incomplete Services profile");
        transport.ProfileJson=new string('x',70*1024);
        try { await api.ProfileAsync("test-credential",default); throw new Exception("Expected API size rejection"); }
        catch(SkinException ex) { check(ex.Message.Contains("Ответ сервиса"),"oversized API response reports service metadata error rather than incorrect PNG size limit"); }
        transport.ProfileJson=null;
        sessions.Expired=true; var before=transport.Uploads;
        await RejectAsync<InvalidOperationException>(()=>service.ApplyAsync(Online,image,SkinModel.Slim,default),check,"expired saved authorization fails before sending a skin mutation");
        check(transport.Uploads==before,"expired auth never uploads using missing/stale credentials"); sessions.Expired=false;
        sessions.WrongSession=true;
        await RejectAsync<SkinException>(()=>service.ResetAsync(Online,default),check,"wrong-account session cannot reset another account skin"); sessions.WrongSession=false;
        transport.WrongProfile=true;
        await RejectAsync<SkinException>(()=>service.GetAsync(Online,default),check,"wrong-account API profile is rejected before fetching texture"); transport.WrongProfile=false;
        transport.FailProfileAfterMutation=true;
        try { await service.ApplyAsync(Online,image,SkinModel.Slim,default); throw new Exception("Expected committed refresh failure"); }
        catch(SkinException ex) { check(ex.Message.Contains("Изменение отправлено"),"mutation committed but failed refresh is reported honestly without pretending rollback"); }
    }
    private static async Task Ui(string root,SkinValidator validator,SkinImage image,Action<bool,string> check)
    {
        var local = new OfflineSkinService(new SkinStorage(Path.Combine(root,"ui-data"),validator));
        var router = new SkinService(new ForbiddenSkinService(),local); var store=new ConfigurationStore(Path.Combine(root,"ui-settings"));
        var accounts=new FakeAccounts(); await accounts.CreateLocalAccountAsync("Preview_Player",default);
        var shell=new MainWindowViewModel(store,new FakeMinecraft(),accounts,skins:router); await shell.InitializeAsync();
        var vm=shell.Accounts.Skin!; shell.CurrentPage="settings";
        var errors=new List<Exception>();
        // Also exercise a directly injected operation runner so failure states can be asserted without shell log timing.
        using var model=new SkinManagerViewModel(router,validator,async action=> { try { await action(default); } catch(Exception ex){errors.Add(ex);} },()=>true);
        model.SetAccount(Local); var pngPath=Path.Combine(root,"imported-file.png"); await File.WriteAllBytesAsync(pngPath,image.Png);
        model.PickSkinAsync=()=>Task.FromResult<string?>(pngPath); await model.OpenCommand.ExecuteAsync(null); await model.UploadCommand.ExecuteAsync(null);
        check(model.IsOpen && model.PreviewImage is not null && model.HasChanges && model.Scope.Contains("только в NexLauncher"),"Local Skin UI presents a real draft preview with explicit in-game limitation");
        check((await local.GetAsync(Local,default)).Image is null,"imported draft is not stored or applied before confirmation");
        model.SelectedModel=SkinManagerViewModel.Models[1]; await model.ApplyCommand.ExecuteAsync(null);
        await File.WriteAllTextAsync(pngPath,"source can disappear");
        check(!model.HasChanges && (await local.GetAsync(Local,default)).Model==SkinModel.Slim,"apply persists explicit Slim model independent of original imported source file");
        await model.UploadCommand.ExecuteAsync(null);
        check(errors.Last() is SkinException && model.PreviewImage is not null,"invalid import shows a nonfatal error and preserves saved preview");
        await model.ResetCommand.ExecuteAsync(null);
        check(model.IsMannequin && (await local.GetAsync(Local,default)).Image is null,"Reset clears local skin and returns explicit neutral/default placeholder");
        model.SetAccount(Local with { Username="Renamed_Player" });
        check(model.Username=="Renamed_Player" && !model.ApplyCommand.CanExecute(null),"account change/rename clears stale draft and refreshes labels without fabricating a skin");
        model.SetAccount(null); check(!model.OpenCommand.CanExecute(null) && !model.UploadCommand.CanExecute(null),"skin commands disable when selected account is absent");
        model.SetAccount(Local); model.PickSkinAsync=()=>Task.FromResult<string?>(Path.Combine(root,"missing.png")); await model.UploadCommand.ExecuteAsync(null);
        check(errors.Last() is SkinException && model.Status.Contains("Файл скина не найден"),"missing import file gets a safe localized nonfatal UI error");
        var pending=new DeferredSkinService();
        using(var state=new SkinManagerViewModel(pending,validator,async action=> { try { await action(default); } catch(OperationCanceledException){} },()=>true))
        {
            state.SetAccount(Local); var task=state.OpenCommand.ExecuteAsync(null); state.SetAccount(Local with {Id="different",Username="Other"});
            pending.Result.SetResult(new(image,SkinModel.Slim,true)); await task;
            check(state.PreviewImage is null && state.Username=="Other" && !state.IsBusy,"late skin response cannot overwrite preview after switching accounts");
        }
        pending=new DeferredSkinService();
        using(var state=new SkinManagerViewModel(pending,validator,async action=> { try { await action(default); } catch(OperationCanceledException){} },()=>true))
        {
            state.SetAccount(Local); var task=state.OpenCommand.ExecuteAsync(null); state.CloseCommand.Execute(null);
            pending.Result.SetResult(new(image,SkinModel.Slim,true)); await task;
            check(!state.IsOpen && state.PreviewImage is null && pending.Token.IsCancellationRequested,"closing Skin Manager cancels pending load and discards its late result");
        }
        vm.PickSkinAsync=()=>Task.FromResult<string?>(pngPath); await File.WriteAllBytesAsync(pngPath,image.Png);
        await vm.OpenCommand.ExecuteAsync(null); await vm.UploadCommand.ExecuteAsync(null);
        var window=new MainWindow(shell){Width=1060,Height=760}; window.Show(); Dispatcher.UIThread.RunJobs();
        await Task.Delay(150); Dispatcher.UIThread.RunJobs();
        var view=window.GetVisualDescendants().OfType<SkinManagerView>().Single(); view.BringIntoView(); Dispatcher.UIThread.RunJobs();
        foreach(var width in new[]{1060,900,1920})
        {
            window.Width=width; Dispatcher.UIThread.RunJobs(); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); view.BringIntoView(); Dispatcher.UIThread.RunJobs();
            var preview=view.FindControl<SkinPreview>("PlayerPreview")!;
            check(preview.Bounds.Height==340 && view.Bounds.Width<=window.ClientSize.Width && view.FindControl<StackPanel>("SkinActions")!.Bounds.Width>200,"Skin Manager integrates into existing Accounts UI without horizontal overflow at " + width);
            using var frame=window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root,"skin-manager-"+width+".png"),PngBitmapEncoderOptions.Default);
        }
        var css=Path.Combine(root,"skin.css"); await File.WriteAllTextAsync(css,"#skin-panel { background: #302040; border-radius: 16px; }");
        using var theme=new QuickCssService(window); var themed=await theme.ConfigureAsync(new(){Enabled=true,FilePath=css,AutoReload=false}); Dispatcher.UIThread.RunJobs();
        check(themed.IsApplied && view.GetVisualDescendants().OfType<Border>().Single(x=>x.Classes.Contains("qc-skin-panel")).Background is ISolidColorBrush brush && brush.Color.ToString()=="#ff302040","Skin Manager supports public Quick CSS styling through existing override engine");
        window.Close();
    }
    private static void Reject(Action action,Action<bool,string> check,string name) { try{action();}catch(SkinException){check(true,name);return;}throw new Exception("Expected skin validation error: "+name); }
    private static byte[] Chunk(byte[] png,string type,byte[] data)
    {
        var chunk=new byte[data.Length+12]; BinaryPrimitives.WriteUInt32BigEndian(chunk,(uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk,4); data.CopyTo(chunk,8); var crc=uint.MaxValue;
        foreach(var value in chunk.AsSpan(4,data.Length+4))
        { crc ^= value; for(var bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)==0?0u:0xedb88320u); }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length+8),~crc);
        return png[..33].Concat(chunk).Concat(png[33..]).ToArray();
    }
    private static async Task RejectAsync<T>(Func<Task> action,Action<bool,string> check,string name) where T:Exception {try{await action();}catch(T){check(true,name);return;}throw new Exception("Expected "+typeof(T).Name+": "+name);}
    private static byte[] Png(int width=64,int height=64,byte baseAlpha=255,bool overlay=true)
    {
        using var bitmap=new SKBitmap(new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Unpremul)); bitmap.Erase(SKColors.Transparent);
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            var baseRegion=y<16&&x<32||y>=16&&y<32||y>=48&&x>=16&&x<48;
            if(baseRegion) bitmap.SetPixel(x,y,new SKColor((byte)(50+x*2),(byte)(50+y*2),150,baseAlpha));
            else if(overlay&&x>=40&&x<48&&y>=8&&y<16)bitmap.SetPixel(x,y,new SKColor(240,190,50,128));
        }
        using var image=SKImage.FromBitmap(bitmap);using var png=image.Encode(SKEncodedImageFormat.Png,100);return png.ToArray();
    }
    private sealed class ForbiddenSkinService:ISkinService
    {
        public int Calls;
        private Task<AccountSkin> Fail(){Calls++;throw new Exception("Local must not call online provider");}
        public Task<AccountSkin> GetAsync(LauncherAccount a,CancellationToken t)=>Fail();
        public Task<AccountSkin> ApplyAsync(LauncherAccount a,SkinImage i,SkinModel m,CancellationToken t)=>Fail();
        public Task<AccountSkin> ResetAsync(LauncherAccount a,CancellationToken t)=>Fail();
    }
    private sealed class SkinHttp(byte[] png):HttpMessageHandler
    {
        public string? ProfileJson;
        public HttpStatusCode Status=HttpStatusCode.OK; public bool InvalidJson,FailNetwork,WrongProfile,FailProfileAfterMutation;
        public HttpMethod? LastMethod; public string? LastPath,Variant,FileName,FileType; public byte[]? FileBytes;
        public int Uploads,Resets,Profiles,SecretsOnTextures; public bool AuthPresent,AllUserAgents=true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if(FailNetwork)throw new HttpRequestException("Bearer SECRET must not reach errors");
            if(request.RequestUri!.Host=="textures.minecraft.net") { if(request.Headers.Authorization is not null)SecretsOnTextures++; return new(HttpStatusCode.OK){Content=new ByteArrayContent(png)}; }
            LastMethod=request.Method; LastPath=request.RequestUri.AbsolutePath; AuthPresent=request.Headers.Authorization?.Scheme=="Bearer"; AllUserAgents &= request.Headers.UserAgent.ToString()==LauncherHttp.UserAgent;
            if(Status!=HttpStatusCode.OK)return new(Status){Content=new StringContent("raw-server-secret")};
            if(request.Method==HttpMethod.Post)
            {
                Uploads++;
                foreach(var part in (MultipartFormDataContent)request.Content!)
                {
                    if(part.Headers.ContentDisposition!.Name!.Trim('"')=="variant")Variant=await part.ReadAsStringAsync(token);
                    else{FileName=part.Headers.ContentDisposition.FileName!.Trim('"');FileType=part.Headers.ContentType!.MediaType;FileBytes=await part.ReadAsByteArrayAsync(token);}
                }
                return new(HttpStatusCode.OK){Content=new StringContent("{}")};
            }
            if(request.Method==HttpMethod.Delete){Resets++;return new(HttpStatusCode.NoContent);}
            Profiles++; if(FailProfileAfterMutation&&Uploads>0)return new(HttpStatusCode.BadGateway);
            return new(HttpStatusCode.OK){Content=new StringContent(ProfileJson ?? (InvalidJson?"<html>":JsonSerializer.Serialize(new {id=WrongProfile?"22222222222222222222222222222222":Online.Uuid,name=Online.Username,skins=new[]{new{state="ACTIVE",variant="SLIM",url="http://textures.minecraft.net/texture/test"}}})),Encoding.UTF8,"application/json")};
        }
    }
    private sealed class DeferredSkinService:ISkinService
    {
        public readonly TaskCompletionSource<AccountSkin> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public Task<AccountSkin> GetAsync(LauncherAccount a,CancellationToken t){Token=t;return Result.Task;}
        public Task<AccountSkin> ApplyAsync(LauncherAccount a,SkinImage i,SkinModel m,CancellationToken t)=>throw new NotSupportedException();
        public Task<AccountSkin> ResetAsync(LauncherAccount a,CancellationToken t)=>throw new NotSupportedException();
    }
    private sealed class SkinAccounts:IAccountService
    {
        public bool Expired,WrongSession; public string? RequestedId;
        public IReadOnlyList<LauncherAccount> Accounts=>[Online]; public string? ActiveAccountId=>"other-active"; public string? PlayerName=>"Other"; public string? MicrosoftAvailabilityWarning=>null;
        public Task InitializeAsync(CancellationToken t)=>Task.CompletedTask;
        public Task<MSession> GetSessionForAccountAsync(string id,CancellationToken t)
        {RequestedId=id;if(Expired)throw new InvalidOperationException("Авторизация истекла.");return Task.FromResult(new MSession(Online.Username,"test-credential",WrongSession?"22222222222222222222222222222222":Online.Uuid){UserType="msa"});}
        public Task<MSession> SignInAsync(CancellationToken t)=>throw new NotSupportedException();
        public Task<MSession?> RestoreAsync(CancellationToken t)=>throw new NotSupportedException();
        public Task SelectAccountAsync(string id,CancellationToken t)=>throw new NotSupportedException();
        public Task RemoveAccountAsync(string id,CancellationToken t)=>throw new NotSupportedException();
        public Task SignOutAsync()=>throw new NotSupportedException();
        public Task<LauncherAccount>CreateLocalAccountAsync(string n,CancellationToken t)=>throw new NotSupportedException();
    }
}
