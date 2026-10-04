using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core.Auth;
using NexLauncher.Models;
using NexLauncher.Services.Network;

namespace NexLauncher.Services.Skins;

public sealed class MinecraftSkinService(IAccountService accounts, IMinecraftSkinApi api, LauncherHttp textures, SkinValidator validator) : ISkinService
{
    private async Task<string> AccessTokenAsync(LauncherAccount account, CancellationToken token)
    {
        if (account.Type != AccountType.Microsoft) throw new SkinException("Для Minecraft Services нужен Microsoft-аккаунт.");
        var session = await accounts.GetSessionForAccountAsync(account.Id, token);
        if (session.UserType != "msa" || string.IsNullOrWhiteSpace(session.AccessToken) || !Guid.TryParse(session.UUID, out var actual) || !Guid.TryParse(account.Uuid, out var expected) || actual != expected)
            throw new SkinException("Сессия относится к другому аккаунту. Обнови список аккаунтов.");
        return session.AccessToken;
    }
    public async Task<AccountSkin> GetAsync(LauncherAccount account, CancellationToken token)
    { var accessToken = await AccessTokenAsync(account, token); return await ReadAsync(account, accessToken, token); }
    private async Task<AccountSkin> ReadAsync(LauncherAccount account, string accessToken, CancellationToken token)
    {
        var profile = await api.ProfileAsync(accessToken, token);
        if (!Guid.TryParse(profile.Id, out var actual) || !Guid.TryParse(account.Uuid, out var expected) || actual != expected)
            throw new SkinException("Minecraft вернул скин другого аккаунта. Изменение остановлено.");
        var active = profile.Skins.FirstOrDefault(x => x.State == "ACTIVE");
        if (active is null) return new(null, SkinModel.Classic, false, "Стандартный скин выбирается Minecraft по UUID. В preview показан нейтральный манекен.");
        var model = active.Variant?.ToUpperInvariant() switch { "CLASSIC" => SkinModel.Classic, "SLIM" => SkinModel.Slim, _ => throw new SkinException("Minecraft вернул неизвестную модель скина.") };
        try
        {
            var uri = OfficialTexture(active.Url);
            var bytes = await textures.GetBytesAsync(uri.AbsoluteUri, ["textures.minecraft.net"], token, SkinValidator.MaxBytes, cache: false);
            var image = await Task.Run(() => validator.Validate(bytes, token), token);
            return new(image, model, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(null, model, false, "Профиль получен, но текстура недоступна. Обнови скин; preview пока показывает манекен."); }
    }
    public static Uri OfficialTexture(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host != "textures.minecraft.net" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.AbsolutePath.StartsWith("/texture/", StringComparison.Ordinal) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new SkinException("Minecraft вернул недопустимый адрес текстуры.");
        return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    }
    public async Task<AccountSkin> ApplyAsync(LauncherAccount account, SkinImage image, SkinModel model, CancellationToken token)
    {
        SkinValidator.ValidateModel(image, model);
        var clean = await Task.Run(() => validator.Validate(image.Png, token), token);
        SkinValidator.ValidateModel(clean, model);
        var accessToken = await AccessTokenAsync(account, token);
        await api.UploadAsync(accessToken, clean, model, token);
        return await RefreshCommittedAsync(account, accessToken, token);
    }
    public async Task<AccountSkin> ResetAsync(LauncherAccount account, CancellationToken token)
    {
        var accessToken = await AccessTokenAsync(account, token); await api.ResetAsync(accessToken, token);
        return await RefreshCommittedAsync(account, accessToken, token);
    }
    private async Task<AccountSkin> RefreshCommittedAsync(LauncherAccount account, string accessToken, CancellationToken token)
    {
        try { return await ReadAsync(account, accessToken, token); }
        catch (Exception) { throw new SkinException("Изменение отправлено в Minecraft, но обновить preview не удалось. Нажми «Обновить» для проверки результата."); }
    }
}
