using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Network;

namespace NexLauncher.Services.Skins;

public sealed class MinecraftSkinProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<MinecraftSkinTexture> Skins { get; set; } = [];
}
public sealed class MinecraftSkinTexture
{
    public string State { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Url { get; set; } = "";
}
public interface IMinecraftSkinApi
{
    Task<MinecraftSkinProfile> ProfileAsync(string accessToken, CancellationToken token);
    Task UploadAsync(string accessToken, SkinImage image, SkinModel model, CancellationToken token);
    Task ResetAsync(string accessToken, CancellationToken token);
}

/// <summary>Credentials are request-local and sent only to this fixed HTTPS origin. No auth logs/cache/redirects.</summary>
public sealed class MinecraftSkinApi(HttpClient client) : IMinecraftSkinApi
{
    public const string Origin = "https://api.minecraftservices.com/";
    public static MinecraftSkinApi Shared { get; } = new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
    public async Task<MinecraftSkinProfile> ProfileAsync(string accessToken, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, "minecraft/profile", accessToken);
        var bytes = await SendAsync(request, token);
        try
        {
            var profile = JsonSerializer.Deserialize<MinecraftSkinProfile>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (profile is null || !Guid.TryParse(profile.Id, out _) || string.IsNullOrWhiteSpace(profile.Name) || profile.Skins is null ||
                profile.Skins.Exists(x => x is null)) throw new SkinException("Minecraft вернул неполный профиль. Попробуй обновить скин.");
            return profile;
        }
        catch (JsonException) { throw new SkinException("Minecraft вернул некорректный ответ. Попробуй позже."); }
    }
    public async Task UploadAsync(string accessToken, SkinImage image, SkinModel model, CancellationToken token)
    {
        SkinValidator.ValidateModel(image, model);
        using var request = Request(HttpMethod.Post, "minecraft/profile/skins", accessToken);
        var multipart = new MultipartFormDataContent(); request.Content = multipart;
        multipart.Add(new StringContent(model == SkinModel.Slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(image.Png); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(file, "file", "skin.png");
        await SendAsync(request, token);
    }
    public async Task ResetAsync(string accessToken, CancellationToken token)
    { using var request = Request(HttpMethod.Delete, "minecraft/profile/skins/active", accessToken); await SendAsync(request, token); }
    private static HttpRequestMessage Request(HttpMethod method, string path, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new SkinException("Сессия Minecraft отсутствует. Войди через Microsoft.");
        var request = new HttpRequestMessage(method, Origin + path);
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); }
        catch (FormatException) { request.Dispose(); throw new SkinException("Сессия Minecraft повреждена. Войди в этот Microsoft-аккаунт снова."); }
        request.Headers.UserAgent.ParseAdd(LauncherHttp.UserAgent); request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
    private async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SkinException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Сессия Minecraft истекла. Обнови скин; если вход не восстановится, войди в этот Microsoft-аккаунт снова.",
                    HttpStatusCode.Forbidden => "Minecraft отклонил изменение скина. Проверь доступ к Java Edition и ограничения аккаунта.",
                    HttpStatusCode.NotFound => "Профиль Minecraft Java Edition не найден. Проверь лицензию и создание профиля.",
                    HttpStatusCode.BadRequest => "Minecraft не принял файл или модель скина. Проверь PNG и повтори попытку.",
                    HttpStatusCode.TooManyRequests => "Слишком много запросов к Minecraft. Подожди и повтори позже.",
                    _ => "Сервис скинов Minecraft временно недоступен. Попробуй позже."
                });
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await SkinValidator.ReadBoundedAsync(stream, 64 * 1024, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new SkinException("Minecraft не ответил вовремя. Попробуй позже."); }
        catch (HttpRequestException) { throw new SkinException("Не удалось связаться с Minecraft. Проверь интернет."); }
    }
}
