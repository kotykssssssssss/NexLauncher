# Microsoft / Xbox / Minecraft в NexLauncher

Windows-версия использует поддерживаемый default OAuth flow CmlLib.Core.Auth.Microsoft.
Пользователю **не нужен собственный Azure/Entra Client ID**. Client Secret не используется.
Старое поле MicrosoftClientId в settings.json оставлено только для совместимости со старыми настройками;
новый Windows backend его не читает.

Microsoft и локальные профили доступны в одном списке. Этот документ описывает
Microsoft backend; локальные имена и общий выбор описаны в [ACCOUNTS.md](ACCOUNTS.md).
Локальный профиль создаётся и выбирается явно, ошибка OAuth не превращает его
в автоматическую замену Microsoft-сессии.

## Вход и запуск

1. Нажми «Войти через Microsoft» на главном экране или в настройках аккаунтов — откроется штатное окно Microsoft через Edge WebView2. Настройки можно открыть кнопкой «Аккаунты» в верхней панели.
2. Выбери Microsoft-аккаунт. Пароль вводится только в форме Microsoft, NexLauncher его не получает.
3. CmlLib получает OAuth credentials, затем Xbox token, Minecraft token, проверяет доступ к игре
   и загружает Java profile (UUID, username, skin URL).
4. Только после завершения всех шагов аккаунт сохраняется и появляется в лаунчере.
5. При запуске выбранный аккаунт проходит silent authentication: библиотека использует сохранённый
   Microsoft token либо refresh token; Xbox и Minecraft credentials и профиль проверяются заново.
   Результирующий настоящий MSession передаётся существующему MinecraftService.
6. Если Microsoft требует повторный интерактивный вход, лаунчер показывает сообщение.
   Кнопка входа позволяет войти в тот же аккаунт повторно; аккаунты с одинаковым UUID не дублируются.

Для другой учётной записи используй добавление аккаунта: OAuth запускается с SelectAccount.
Выбор активного аккаунта сохраняется между запусками. Операции выбора, входа, обновления
и удаления выполняются последовательно.

«Выйти» / удаление аккаунта удаляет его локальные OAuth/Xbox/Minecraft credentials.
Это **не выход из Microsoft во всех приложениях** и не отзыв токенов на стороне Microsoft.
Штатный WebView2 хранит свою браузерную сессию отдельно; удаление профиля NexLauncher её не очищает.
Поэтому окно Microsoft может помнить ранее выбранные учётные записи.

## Хранение и безопасность

Skin Manager использует `GetSessionForAccountAsync` для silent refresh конкретного
сохранённого профиля, сохраняя активную identity запуска. Полученный Minecraft
bearer передаётся только фиксированному HTTPS Minecraft Services origin для
profile/upload/reset. Additional OAuth scopes, Client ID/Secret и новый token cache
не нужны. Формат DPAPI не менялся. Подробности API и ручные проверки: [SKINS.md](SKINS.md).

Файл: `<папка данных NexLauncher>/auth/accounts.dat`.
Обычно это `%LOCALAPPDATA%/NexLauncher/auth/accounts.dat`.

Весь payload, включая Microsoft refresh/access tokens, Xbox/Minecraft tokens, публичные профили
и внутренний выбранный Microsoft UUID, зашифрован Windows DPAPI с `DataProtectionScope.CurrentUser`.
Ключ управляется Windows; в исходниках нет паролей, ключа шифрования или Client Secret.
Дополнительная фиксированная entropy служит разделением формата приложения, а не секретным ключом.
Защита привязана к пользователю Windows: копирование файла другому пользователю не переносит вход.
Процессы, уже работающие от имени того же Windows-пользователя, не изолированы этой защитой.

CmlLib подключён к собственному in-memory `IJsonStorage`; стандартный открытый
`cml_accounts.json` не создаётся. Незавершённые/отменённые входы не попадают на диск.
Перед сохранением создаётся только **зашифрованный** временный файл, затем файл атомарно заменяется.
При ошибке записи защищённого кеша Microsoft его прежнее состояние сохраняется.
Общий выбор хранится отдельно в `auth/profiles.json`: если удаление credentials уже завершилось,
а запись общего выбора затем дала ошибку, удаление не откатывается. UI показывает фактический
список и сообщение об ошибке; повторное добавление удалённого аккаунта требует входа.
Повреждённое или недоступное для расшифровки хранилище не перезаписывается автоматически.
В этом случае закрой лаунчер, сохрани резервную копию `accounts.dat`, восстанови его из доверенной
резервной копии либо переименуй, если осознанно хочешь заново войти во все аккаунты.

Токены не публикуются в UI bindings, не записываются в обычный JSON настроек и не логируются.
Сессия MSession проходит через локальную переменную операции запуска и передаётся MinecraftService;
отдельное поле с последней сессией в ViewModel не хранится.
UI получает только публичные поля `LauncherAccount(Id, Username, Uuid, SkinUrl, Type)`.
Общий активный профиль (Microsoft или Local) сохраняется в публичном `auth/profiles.json`;
Microsoft credentials остаются исключительно в защищённом `accounts.dat`.
Ошибки сторонних API преобразуются в фиксированные пользовательские сообщения без их raw body/inner exception.
Токены необходимы библиотеке в памяти во время работы; буферы plaintext DPAPI очищаются после шифрования/чтения.

## Реализация и зависимости

Проверены установленные API CmlLib.Core 4.0.6, CmlLib.Core.Auth.Microsoft 3.3.1,
XboxAuthNet.Game 1.4.1 и XboxAuthNet 3.0.4; signatures дополнительно сверены reflection с DLL.

- `AccountService` — общий список и выбор Microsoft/локальных профилей; делегирует Microsoft-операции существующему провайдеру.
- `MicrosoftAccountService` — выбор/удаление Microsoft-аккаунтов, последовательные операции, commit после сохранения.
- `IMinecraftAuthenticationBackend` — граница платформенного OAuth.
- `WindowsMinecraftAuthenticationBackend` — `JELoginHandlerBuilder`,
  `AddForceMicrosoftOAuthForJE(...Interactive(...))` / `AddMicrosoftOAuthForJE(...Silent())`,
  Xbox Basic и `AddForceJEAuthenticator(je => je.WithGameOwnershipChecker().Build())`.
- `ProtectedAccountVault` — версия формата, безопасная запись и отказ при повреждении.
- `WindowsAccountDataProtector` — DPAPI CurrentUser.
- `AuthenticationErrors` — сообщения об отмене, просроченной сессии, Xbox-профиле,
  семейных ограничениях, недоступной лицензии/профиле и сетевых сбоях.

Добавлена Microsoft dependency `System.Security.Cryptography.ProtectedData 10.0.12` для DPAPI.
Windows target `net10.0-windows` нужен, чтобы NuGet выбрал Windows-реализацию XboxAuthNet с WebView2.
Обычный `net10.0` выбирает netstandard-версию без встроенного OAuth окна.
WebView2 и WindowsForms приходят транзитивно из XboxAuthNet. Текущему executable нужен
.NET Windows Desktop Runtime 10. Microsoft Edge WebView2 Runtime требуется для Microsoft-входа;
создание и использование локального аккаунта не открывают это окно и не требуют WebView2.

## Ограничения и ручная проверка

Default OAuth CmlLib доступен только в Windows. Linux/macOS backend пока не реализован:
текущий executable предназначен для Windows; backend также явно отклоняет другие ОС, фиктивной авторизации нет.
Позже MSAL backend с собственным Client ID можно добавить за `IMinecraftAuthenticationBackend`
вместе с защищённым хранилищем соответствующей ОС, не меняя UI аккаунтов.

Официальный ownership checker библиотеки может не отличать некоторые ошибки entitlement endpoint
от отсутствия доступа к игре; сообщение советует проверить покупку/подписку и повторить попытку.
Этот backend создаёт только проверенные Microsoft-сессии. Отдельный локальный профиль
доступен для одиночной игры и серверов, допускающих offline-профили; его создание не вызывает
OAuth и не снимает требования серверов с Microsoft-проверкой. Установка и проверка файлов
по-прежнему могут требовать сеть. См. [локальные аккаунты](ACCOUNTS.md).

Нужно проверить на реальном аккаунте:
- первый вход и получение username/skin;
- добавление второго аккаунта, выбор, перезапуск лаунчера;
- silent refresh и реальный запуск Minecraft;
- отмену окна, отсутствие Java Edition/Xbox profile и недоступную сеть;
- удаление одного аккаунта с сохранением другого;
- повторный вход после истечения/отзыва Microsoft refresh token.

Автоматические проверки используют искусственные данные и не обращаются к аккаунтам пользователя.

## Первичные источники

- [JELoginHandler: default Windows flow и silent refresh](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/cmllib.core.auth.microsoft/jeloginhandler/)
- [JELoginHandlerBuilder: собственный account manager](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/cmllib.core.auth.microsoft/jeloginhandlerbuilder/)
- [CmlLib auth NuGet 3.3.1](https://www.nuget.org/packages/CmlLib.Core.Auth.Microsoft/3.3.1)
- [XboxAuthNet 3.0.4: target frameworks](https://www.nuget.org/packages/XboxAuthNet/3.0.4)
- [DPAPI CurrentUser](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope)
- [ProtectedData NuGet](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.12)
- [MSAL backend для других платформ](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/cmllib.core.auth.microsoft/authentication-with-msal/)
