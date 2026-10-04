using System.Threading;
using System.Threading.Tasks;
using NexLauncher.Models;

namespace NexLauncher.Services.Skins;

public interface ISkinService
{
    Task<AccountSkin> GetAsync(LauncherAccount account, CancellationToken token);
    Task<AccountSkin> ApplyAsync(LauncherAccount account, SkinImage image, SkinModel model, CancellationToken token);
    Task<AccountSkin> ResetAsync(LauncherAccount account, CancellationToken token);
}

/// <summary>Dispatches by explicit account type; local skins never enter the online provider.</summary>
public sealed class SkinService(ISkinService microsoft, ISkinService local) : ISkinService
{
    private ISkinService Provider(LauncherAccount account) => account.Type switch
    {
        AccountType.Microsoft => microsoft, AccountType.Local => local,
        _ => throw new SkinException("Неизвестный тип аккаунта.")
    };
    public Task<AccountSkin> GetAsync(LauncherAccount account, CancellationToken token) => Provider(account).GetAsync(account, token);
    public Task<AccountSkin> ApplyAsync(LauncherAccount account, SkinImage image, SkinModel model, CancellationToken token) => Provider(account).ApplyAsync(account, image, model, token);
    public Task<AccountSkin> ResetAsync(LauncherAccount account, CancellationToken token) => Provider(account).ResetAsync(account, token);
}
