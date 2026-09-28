using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services.Network;

public sealed class LauncherHttp
{
    public static string UserAgent { get; } = $"NexLauncher/{BuildInfo.Version} (github.com/kotykssssssssss/NexLauncher)";
    public static LauncherHttp Shared { get; } = new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan });
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _apiGate = new(1, 1);
    private DateTimeOffset _rateReset;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, byte[] Bytes)> _cache = new();
    public LauncherHttp(HttpClient client) => _client = client;
    public static readonly string[] PackHosts = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];
    public static readonly string[] LoaderHosts = ["meta.fabricmc.net", "maven.fabricmc.net", "maven.minecraftforge.net", "maven.neoforged.net", "files.minecraftforge.net"];

    public static Uri ValidateUrl(string url, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !allowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Адрес загрузки не разрешён. Используй официальный источник проекта.");
        return uri;
    }

    public async Task<byte[]> GetBytesAsync(string url, IReadOnlyCollection<string> hosts, CancellationToken token, int maxBytes = 8 * 1024 * 1024, bool cache = true)
    {
        token.ThrowIfCancellationRequested();
        ValidateUrl(url, hosts);
        if (cache && _cache.TryGetValue(url, out var item) && item.Until > DateTimeOffset.UtcNow)
        {
            if (item.Bytes.Length > maxBytes) throw new InvalidDataException("Ответ сервера слишком велик.");
            return item.Bytes;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            using var response = await SendAsync(url, hosts, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Ответ сервера слишком велик.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var result = new MemoryStream();
            var buffer = new byte[32768];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > maxBytes) throw new InvalidDataException("Ответ сервера слишком велик.");
                result.Write(buffer, 0, count);
            }
            var bytes = result.ToArray();
            var policy = response.Headers.CacheControl;
            if (cache && policy?.NoStore != true && policy?.NoCache != true && policy?.Private != true)
            {
                var lifetime = policy?.MaxAge ?? TimeSpan.FromMinutes(3);
                lifetime -= response.Headers.Age ?? TimeSpan.Zero;
                if (lifetime > TimeSpan.FromMinutes(3)) lifetime = TimeSpan.FromMinutes(3);
                if (lifetime > TimeSpan.Zero)
                { if (_cache.Count >= 128) _cache.Clear(); _cache[url] = (DateTimeOffset.UtcNow.Add(lifetime), bytes); }
            }
            return bytes;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Сервер не ответил вовремя. Повтори запрос."); }
    }

    public async Task<T> JsonAsync<T>(string url, IReadOnlyCollection<string> hosts, CancellationToken token, bool cache = true)
    {
        var bytes = await GetBytesAsync(url, hosts, token, cache: cache).ConfigureAwait(false);
        try { return await Task.Run(() => JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new JsonException("null"), token).ConfigureAwait(false); }
        catch (JsonException ex) { _cache.TryRemove(url, out _); throw new InvalidDataException("Сервер вернул неожиданную metadata. Повтори позже.", ex); }
    }
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    private async Task<HttpResponseMessage> SendAsync(string url, IReadOnlyCollection<string> hosts, CancellationToken token)
    {
        var uri = ValidateUrl(url, hosts);
        var api = uri.Host == "api.modrinth.com";
        if (api) await _apiGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (api && _rateReset > DateTimeOffset.UtcNow)
                {
                    var delay = _rateReset - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.FromSeconds(30)) throw new IOException("Лимит Modrinth исчерпан. Повтори через минуту.");
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                }
                var current = uri;
                HttpResponseMessage? response = null;
                for (var redirect = 0; redirect <= 5; redirect++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    request.Headers.UserAgent.ParseAdd(UserAgent);
                    response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if ((int)response.StatusCode is >= 300 and <= 399)
                    {
                        var location = response.Headers.Location;
                        response.Dispose();
                        if (redirect == 5 || location is null) throw new IOException("Слишком много перенаправлений загрузки.");
                        current = ValidateUrl(new Uri(current, location).AbsoluteUri, hosts);
                        continue;
                    }
                    break;
                }
                var retrySeconds = response!.Headers.RetryAfter?.Delta?.TotalSeconds ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 2 * (attempt + 1);
                if (api && response.Headers.TryGetValues("X-Ratelimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
                {
                    if (response.Headers.TryGetValues("X-Ratelimit-Reset", out var reset) && double.TryParse(reset.FirstOrDefault(), System.Globalization.CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
                        _rateReset = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 0, 3600));
                }
                if (response.IsSuccessStatusCode) return response;
                var status = (int)response.StatusCode;
                response.Dispose();
                if (attempt < 2 && (status == 429 || status >= 500) && retrySeconds <= 30)
                { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, retrySeconds)), token).ConfigureAwait(false); continue; }
                throw new IOException(status == 429 ? "Лимит запросов Modrinth исчерпан. Повтори позже." : $"Сервер {uri.Host} вернул HTTP {status}. Повтори позже.");
            }
            throw new IOException("Запрос не выполнен.");
        }
        catch (HttpRequestException ex) { throw new IOException("Не удалось подключиться к " + uri.Host + ". Проверь сеть.", ex); }
        finally { if (api) _apiGate.Release(); }
    }

    public async Task DownloadAsync(string url, string destination, IReadOnlyDictionary<string, string> hashes, long expectedSize,
        IReadOnlyCollection<string> hosts, IProgress<LaunchProgress> progress, CancellationToken token)
    {
        ValidateHashes(hashes);
        if (expectedSize < -1 || expectedSize > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Недопустимый размер загрузки.");
        SafePaths.NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            using var response = await SendAsync(url, hosts, timeout.Token).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long total = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                int count;
                var reported = DateTime.UtcNow;
                while ((count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > (expectedSize >= 0 ? expectedSize : 2L * 1024 * 1024 * 1024)) throw new InvalidDataException("Размер загрузки превышает metadata.");
                    sha512.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    if ((DateTime.UtcNow - reported).TotalMilliseconds > 150)
                    { progress.Report(new("Загрузка " + Path.GetFileName(destination), expectedSize > 0 ? total * 100d / expectedSize : null)); reported = DateTime.UtcNow; }
                }
            }
            if (expectedSize >= 0 && total != expectedSize) throw new InvalidDataException("Неполная загрузка.");
            CheckHash(hashes, "sha512", sha512.GetHashAndReset()); CheckHash(hashes, "sha1", sha1.GetHashAndReset());
            token.ThrowIfCancellationRequested();
            SafePaths.NoLinks(destination);
            File.Move(temporary, destination, false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Время загрузки истекло. Повтори установку."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ValidateHashes(IReadOnlyDictionary<string, string> hashes)
    {
        if (hashes is null || (!hashes.ContainsKey("sha512") && !hashes.ContainsKey("sha1"))) throw new InvalidDataException("Сервер не предоставил проверяемый hash файла.");
        foreach (var key in new[] { "sha512", "sha1" })
            if (hashes.TryGetValue(key, out var value) && (value is null || value.Length != (key == "sha512" ? 128 : 40) || !value.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Некорректный hash файла.");
    }
    private static void CheckHash(IReadOnlyDictionary<string, string> hashes, string key, byte[] actual)
    {
        if (hashes.TryGetValue(key, out var expected) && !Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hash не совпал: повреждённый файл не установлен.");
    }
    public static async Task<bool> MatchesAsync(string path, IReadOnlyDictionary<string, string> hashes, CancellationToken token)
    {
        ValidateHashes(hashes);
        if (!File.Exists(path)) return false;
        SafePaths.NoLinks(path);
        foreach (var key in new[] { "sha512", "sha1" })
        {
            if (!hashes.TryGetValue(key, out var expected)) continue;
            await using var stream = File.OpenRead(path);
            var actual = key == "sha512" ? await SHA512.HashDataAsync(stream, token) : await SHA1.HashDataAsync(stream, token);
            if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
