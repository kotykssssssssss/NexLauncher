using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client.Extensions.Msal;

namespace NexLauncher.Services.Authentication;

public interface ICredentialKeyStore
{
    byte[] GetKey(bool create);
}

/// <summary>Uses Microsoft's OS persistence provider, never its unprotected-file fallback.</summary>
public sealed class SystemCredentialKeyStore(string authDirectory) : ICredentialKeyStore
{
    public byte[] GetKey(bool create)
    {
        try
        {
            var directory = Path.GetFullPath(authDirectory);
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
            var properties = new StorageCreationPropertiesBuilder("vault-key", directory)
                .WithMacKeyChain("NexLauncher.AccountVault.v1", identity)
                .WithLinuxKeyring("org.nexlauncher.AccountVault.v1", "default", "NexLauncher account encryption key",
                    new KeyValuePair<string, string>("application", "NexLauncher"),
                    new KeyValuePair<string, string>("profile", identity)).Build();
            var trace = new TraceSource("NexLauncher.CredentialStore", SourceLevels.Off);
            // Distinct from the provider's per-read/write lock, and covers first-key creation.
            using var gate = new CrossPlatLock(Path.Combine(directory, "vault-key.creation-lock"), 100, 300);
            var helper = MsalCacheHelper.CreateAsync(properties, trace).GetAwaiter().GetResult();
            var encoded = helper.LoadUnencryptedTokenCache();
            try
            {
                if (encoded is { Length: > 0 })
                {
                    var key = Convert.FromBase64String(Encoding.UTF8.GetString(encoded));
                    if (key.Length == 32) return key;
                    CryptographicOperations.ZeroMemory(key);
                    throw new CryptographicException();
                }
                if (!create) throw new CryptographicException();
                var created = RandomNumberGenerator.GetBytes(32);
                try
                {
                    var text = Encoding.UTF8.GetBytes(Convert.ToBase64String(created));
                    try { helper.SaveUnencryptedTokenCache(text); }
                    finally { CryptographicOperations.ZeroMemory(text); }
                    // Confirm native persistence before publishing any encrypted account file.
                    var read = helper.LoadUnencryptedTokenCache();
                    try
                    {
                        if (read is null || Encoding.UTF8.GetString(read) != Convert.ToBase64String(created))
                            throw new CryptographicException();
                    }
                    finally { if (read is not null) CryptographicOperations.ZeroMemory(read); }
                    return (byte[])created.Clone();
                }
                finally { CryptographicOperations.ZeroMemory(created); }
            }
            finally { if (encoded is not null) CryptographicOperations.ZeroMemory(encoded); }
        }
        catch (Exception)
        {
            throw new AccountStorageException("Системное хранилище секретов недоступно. Разблокируй Keychain на macOS; на Linux нужны libsecret-1 и работающий Secret Service (например, GNOME Keyring). Токены не сохранены открытым текстом. Локальные аккаунты доступны.");
        }
    }
}
