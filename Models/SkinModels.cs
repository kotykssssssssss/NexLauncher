using System;

namespace NexLauncher.Models;

public enum SkinModel { Classic, Slim }
public sealed record SkinModelChoice(SkinModel Model, string Label);

/// <summary>Decoded and sanitized pixels/PNG only. No path or credentials reach the UI.</summary>
public sealed record SkinImage(byte[] Png, byte[] Pixels, bool Legacy, bool NormalizedTransparency);
public sealed record AccountSkin(SkinImage? Image, SkinModel Model, bool LocalOnly, string Message = "");
public sealed record StoredSkin(int FormatVersion, string AccountId, AccountType AccountType,
    SkinModel Model, string FileName, string Sha256, bool Legacy = false);

public sealed class SkinException(string message) : InvalidOperationException(message);
