using System;
using System.Net.Http;
using System.Threading;
using CmlLib.Core.Auth.Microsoft;
using Microsoft.Identity.Client;
using XboxAuthNet.OAuth;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.XboxLive;

namespace NexLauncher.Services.Authentication;

/// <summary>Only fixed, safe messages cross the authentication boundary.</summary>
public static class AuthenticationErrors
{
    public static Exception ForUser(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AuthenticationConfigurationException) return new AuthenticationConfigurationException();
        if (exception is MsalUiRequiredException)
            return Error("Авторизация истекла. Открой «Аккаунты» → «Добавить Microsoft» и войди ещё раз.");
        if (exception is MsalException msal)
            return msal.ErrorCode == "authentication_canceled"
                ? new OperationCanceledException("Вход Microsoft отменён.", cancellationToken)
                : Error("Microsoft не завершил вход через браузер. Проверь сеть, разрешение приложения Minecraft Services и повтори попытку.");
        if (exception is OperationCanceledException)
            return cancellationToken.IsCancellationRequested
                ? new OperationCanceledException("Вход отменён.", cancellationToken)
                : new OperationCanceledException("Окно входа закрыто или время ожидания истекло.");
        if (exception is AuthCodeException code)
            return code.Error is "access_denied" or "user_cancelled" or "user_canceled"
                ? new OperationCanceledException("Вход Microsoft отменён.", cancellationToken)
                : Error("Microsoft не завершил вход. Попробуй войти ещё раз.");
        if (exception is MicrosoftOAuthException oauth)
            return oauth.Error is "invalid_grant" or "interaction_required" or "login_required" || oauth.StatusCode == 0
                ? Error("Авторизация истекла. Открой «Аккаунты» → «Добавить Microsoft» и войди в этот аккаунт ещё раз.")
                : Error("Сервис Microsoft не подтвердил вход. Проверь интернет и повтори попытку.");
        if (exception is XboxAuthException xbox)
            return xbox.Error switch
            {
                "2148916233" => Error("Для этого аккаунта не создан Xbox-профиль. Открой xbox.com, создай профиль и повтори вход."),
                "2148916235" => Error("Xbox Live недоступен для региона этого аккаунта."),
                "2148916236" or "2148916237" or "2148916238" =>
                    Error("Вход Xbox ограничен настройками возраста или семьи. Проверь настройки семейной группы Microsoft."),
                _ => Error("Xbox не подтвердил аккаунт. Проверь Xbox-профиль, семейные ограничения и повтори вход.")
            };
        if (exception is JEAuthException minecraft)
        {
            if (minecraft.StatusCode == 404 || minecraft.Message.Contains("doesn't own", StringComparison.OrdinalIgnoreCase))
                return Error("Не найден доступ к Minecraft: Java Edition или игровой профиль. Проверь покупку/подписку и создай профиль на minecraft.net.");
            if (minecraft.StatusCode == 401)
                return Error("Сессия Minecraft истекла. Войди в Microsoft ещё раз.");
            if (minecraft.StatusCode == 403)
                return Error("Minecraft отклонил доступ аккаунта. Проверь доступ к Java Edition и повтори вход позже.");
            return Error("Minecraft не вернул действительный игровой профиль. Попробуй ещё раз позже.");
        }
        if (exception is HttpRequestException or TimeoutException)
            return Error("Не удалось связаться с серверами входа. Проверь интернет и повтори попытку.");
        if (exception is PlatformNotSupportedException)
            return Error("Этот способ входа недоступен на данной платформе. Используй сборку NexLauncher для своей ОС.");
        if (exception is DllNotFoundException || exception.GetType().Name.Contains("WebView2", StringComparison.Ordinal))
            return Error("Для входа нужен Microsoft Edge WebView2 Runtime. Установи его с сайта Microsoft и повтори вход.");
        return Error("Не удалось подтвердить аккаунт Microsoft/Xbox/Minecraft. Повтори вход и проверь доступ к Java Edition.");
    }

    private static InvalidOperationException Error(string message) => new(message);
}
