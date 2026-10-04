using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;

namespace NexLauncher.Services.Skins;

public sealed class OfflineSkinService(SkinStorage storage) : ISkinService
{
    public const string Limitation = "Local Skin хранится и показывается только в NexLauncher. Vanilla Minecraft не читает этот PNG: в игре и у других игроков он не появится без поддержки клиента/сервера.";
    private static void RequireLocal(LauncherAccount account)
    { if (account.Type != AccountType.Local) throw new SkinException("Для Local Skin нужен локальный аккаунт."); }
    public Task<AccountSkin> GetAsync(LauncherAccount account, CancellationToken token) { RequireLocal(account); return storage.LoadAsync(account, token); }
    public Task<AccountSkin> ApplyAsync(LauncherAccount account, SkinImage image, SkinModel model, CancellationToken token) { RequireLocal(account); return storage.SaveAsync(account, image, model, token); }
    public Task<AccountSkin> ResetAsync(LauncherAccount account, CancellationToken token) { RequireLocal(account); return storage.ResetAsync(account, token); }
}
