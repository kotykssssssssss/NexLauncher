using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;
using NexLauncher.Services.Loaders;
using NexLauncher.Services.Storage;

namespace NexLauncher.Services;

/// <summary>Stores public launcher settings. Account credentials belong in the authentication cache.</summary>
public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public ConfigurationStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? GetDefaultDataDirectory());
        ConfigurationPath = Path.Combine(DataDirectory, "settings.json");
    }

    public string DataDirectory { get; }
    public string ConfigurationPath { get; }

    public async Task<LauncherConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(ConfigurationPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var configuration = await JsonSerializer.DeserializeAsync<LauncherConfiguration>(
                stream, JsonOptions, cancellationToken);
            return ValidateAndCopy(configuration ?? throw new InvalidDataException("Файл настроек пуст."));
        }
        catch (FileNotFoundException)
        {
            return new LauncherConfiguration();
        }
        catch (DirectoryNotFoundException)
        {
            return new LauncherConfiguration();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Не удалось прочитать настройки «{ConfigurationPath}»: {exception.Message} " +
                "Исходный файл сохранён. Исправьте его или переименуйте, чтобы создать новые настройки.", exception);
        }
    }

    public async Task SaveAsync(LauncherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var validated = ValidateAndCopy(configuration);
        await _saveLock.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            // Protect settings damaged after the UI initially loaded them.
            _ = await LoadAsync(cancellationToken);
            Directory.CreateDirectory(DataDirectory);
            temporaryPath = Path.Combine(DataDirectory, $"settings.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, validated, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // The temporary file is on the same volume, so readers see either the old or new file.
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _saveLock.Release();
        }
    }

    private LauncherConfiguration ValidateAndCopy(LauncherConfiguration source)
    {
        if (source.FormatVersion is < 1 or > 2) throw new InvalidDataException("Неизвестная версия настроек.");
        if (source.Instances is null)
            throw new InvalidDataException("Список сборок не может быть null.");

        var clientId = source.MicrosoftClientId?.Trim() ?? "";
        if (clientId.Length > 0)
        {
            if (!Guid.TryParse(clientId, out var parsedClientId) || parsedClientId == Guid.Empty)
                throw new InvalidDataException("Microsoft Client ID должен быть GUID или пустой строкой.");
            clientId = parsedClientId.ToString("D");
        }

        var result = new LauncherConfiguration
        {
            InstancesDirectory = string.IsNullOrWhiteSpace(source.InstancesDirectory) ? Path.Combine(DataDirectory, "instances") : SafePaths.LocalRoot(source.InstancesDirectory),
            MicrosoftClientId = clientId,
            ShowSnapshots = source.ShowSnapshots,
            QuickCss = CopyQuickCss(source.QuickCss)
        };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in source.Instances)
        {
            if (instance is null || !Guid.TryParse(instance.Id, out var id) || id == Guid.Empty)
                throw new InvalidDataException("У каждой сборки должен быть непустой идентификатор GUID.");
            var normalizedId = id.ToString("N");
            if (!ids.Add(normalizedId))
                throw new InvalidDataException($"Идентификатор сборки «{normalizedId}» повторяется.");

            var name = instance.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 60)
                throw new InvalidDataException("Название сборки должно содержать от 1 до 60 символов.");

            var version = instance.VersionId ?? "";
            if (!IsValidVersionId(version))
                throw new InvalidDataException($"У сборки «{name}» недопустимый идентификатор версии Minecraft.");
            if (instance.MemoryMb < 1024 || instance.MemoryMb > 32768)
                throw new InvalidDataException($"Память для сборки «{name}» должна быть от 1024 до 32768 МБ.");
            LoaderCatalog.ValidateInstance(instance);

            result.Instances.Add(new GameInstance
            {
                Id = normalizedId,
                Name = name,
                VersionId = version,
                Loader = instance.Loader,
                LoaderVersion = instance.LoaderVersion,
                // Missing paths belong to old Vanilla profiles and must never follow a new default root.
                GameDirectory = SafePaths.GamePath(DataDirectory, instance),
                ModrinthProjectId = instance.ModrinthProjectId,
                ModrinthVersionId = instance.ModrinthVersionId,
                MemoryMb = instance.MemoryMb,
                JavaPath = instance.JavaPath?.Trim() ?? ""
            });
        }

        var selectedId = Guid.TryParse(source.SelectedInstanceId, out var selected)
            ? selected.ToString("N")
            : "";
        result.SelectedInstanceId = ids.Contains(selectedId)
            ? selectedId
            : result.Instances.Count > 0 ? result.Instances[0].Id : null;
        return result;
    }

    private static QuickCssSettings CopyQuickCss(QuickCssSettings? source)
    {
        source ??= new();
        var path = source.FilePath?.Trim() ?? "";
        if (path.Length > 2048 || (path.Length > 0 &&
            (!Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)))
            throw new InvalidDataException("Quick CSS: укажи полный путь к файлу.");
        return new QuickCssSettings { Enabled = source.Enabled, FilePath = path, AutoReload = source.AutoReload };
    }

    private static bool IsValidVersionId(string value)
    {
        if (value.Length > 100 || value is "." or "..")
            return false;
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '.' or '_' or '+' or '-'))
                return false;
        }
        return true;
    }

    private static string GetDefaultDataDirectory()
    {
        var userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(userDirectory))
            return Path.Combine(userDirectory, "Library", "Application Support", "NexLauncher");

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localData))
            return Path.Combine(localData, "NexLauncher");
        if (!string.IsNullOrWhiteSpace(userDirectory))
            return Path.Combine(userDirectory, ".local", "share", "NexLauncher");
        throw new InvalidOperationException("Не удалось определить папку пользователя для данных NexLauncher.");
    }
}
