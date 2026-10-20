using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NexLauncher.Services.Authentication;

/// <summary>Encrypts the complete CmlLib cache before any bytes reach disk.</summary>
public sealed class ProtectedAccountVault : IAccountVault
{
    private const int MaximumFileBytes = 8 * 1024 * 1024;
    private readonly string _path;
    private readonly IAccountDataProtector _protector;

    public ProtectedAccountVault(string path, IAccountDataProtector protector)
    {
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public Task<AccountVaultSnapshot> LoadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    public Task SaveAsync(AccountVaultSnapshot snapshot, CancellationToken cancellationToken)
    {
        var copy = snapshot.Copy();
        return Task.Run(() => Write(copy, cancellationToken), cancellationToken);
    }

    private AccountVaultSnapshot Read(CancellationToken cancellationToken)
    {
        byte[]? plaintext = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumFileBytes)
                throw new InvalidDataException();
            var encrypted = new byte[(int)stream.Length];
            stream.ReadExactly(encrypted);
            plaintext = _protector.Unprotect(encrypted);
            cancellationToken.ThrowIfCancellationRequested();
            var root = JsonNode.Parse(plaintext) as JsonObject;
            if (root is null || root["version"]?.GetValue<int>() != 1 || root["accounts"] is not JsonObject accounts)
                throw new InvalidDataException();
            var selected = root["activeAccountId"]?.GetValue<string>();
            foreach (var entry in accounts)
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is not JsonObject)
                    throw new InvalidDataException();
            if (selected is not null && !accounts.ContainsKey(selected))
                throw new InvalidDataException();
            return new AccountVaultSnapshot((JsonObject)accounts.DeepClone(), selected);
        }
        catch (FileNotFoundException) { return AccountVaultSnapshot.Empty(); }
        catch (DirectoryNotFoundException) { return AccountVaultSnapshot.Empty(); }
        catch (OperationCanceledException) { throw; }
        catch (AccountStorageException) { throw; }
        catch (Exception)
        {
            throw new AccountStorageException(
                "Не удалось открыть защищённое хранилище аккаунтов. Возможно, файл повреждён, принадлежит другому пользователю или системный ключ недоступен. Исходный файл сохранён: auth/accounts.dat.");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Write(AccountVaultSnapshot snapshot, CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        byte[]? plaintext = null;
        try
        {
            // Refuse to overwrite a damaged or foreign vault, including damage after initial load.
            _ = Read(cancellationToken);
            var root = new JsonObject
            {
                ["version"] = 1,
                ["activeAccountId"] = snapshot.ActiveAccountId,
                ["accounts"] = snapshot.Accounts.DeepClone()
            };
            plaintext = JsonSerializer.SerializeToUtf8Bytes(root);
            var encrypted = _protector.Protect(plaintext);
            if (encrypted.Length > MaximumFileBytes)
                throw new InvalidDataException();
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            temporaryPath = Path.Combine(directory, $"accounts.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporaryPath, options))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Commit point: no cancellation after the atomic same-volume replacement.
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (AccountStorageException) { throw; }
        catch (Exception)
        {
            throw new AccountStorageException("Не удалось сохранить аккаунты. Проверь доступ к папке NexLauncher и свободное место. Предыдущие аккаунты сохранены.");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
