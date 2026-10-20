using System;
using System.Security.Cryptography;
using System.Text;

namespace NexLauncher.Services.Authentication;

/// <summary>AES-GCM envelope with a random 256-bit key kept exclusively in the user's OS key store.</summary>
public sealed class SystemAccountDataProtector(ICredentialKeyStore keys) : IAccountDataProtector
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("NXAV1");
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("NexLauncher.AccountVault.v1");
    public byte[] Protect(byte[] plaintext)
    {
        var key = keys.GetKey(create: true);
        try
        {
            var result = new byte[Header.Length + 12 + 16 + plaintext.Length];
            Header.CopyTo(result, 0);
            var nonce = result.AsSpan(Header.Length, 12);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, result.AsSpan(Header.Length + 28), result.AsSpan(Header.Length + 12, 16), Domain);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length < Header.Length + 28 || !ciphertext.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new CryptographicException();
        var key = keys.GetKey(create: false);
        var plaintext = new byte[ciphertext.Length - Header.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(ciphertext.AsSpan(Header.Length, 12), ciphertext.AsSpan(Header.Length + 28),
                ciphertext.AsSpan(Header.Length + 12, 16), plaintext, Domain);
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
