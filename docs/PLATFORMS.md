# macOS / Linux preview

Статус на 20 октября 2026: переносимый target, OAuth backend и пакеты добавлены.
Это **preview для проверки на целевых ОС**, а не подтверждённый кроссплатформенный
публичный релиз. Host — Windows; Linux startup и fixture suite проверены
в установленной Ubuntu 24.04 / WSLg без .NET SDK. macOS машины нет.
Microsoft-вход на macOS/Linux **пока нельзя включить для пользователей**:
у владельца нет собственного зарегистрированного/разрешённого Client ID.
Наличие собранного пакета не означает, что вход или Minecraft запускались на этой ОС.

## Платформы и сборка

- Windows x64: сохранён `net10.0-windows`, CmlLib WebView2, Windows DPAPI,
  прежние Installer/Portable и их process/session pipeline.
- Linux x64: `net10.0`, glibc desktop, portable `.tar.gz`. Для первого native
  теста рекомендуется Ubuntu 24.04 x64 с X11/XWayland. musl/Alpine и Linux ARM64
  в этот набор пакетов не входят.
- macOS: `net10.0`, отдельные `.app` ZIP для Intel и Apple Silicon.
  В preview установлен минимум macOS 15; это не `net10.0-macos` и не Mac Catalyst.
  .NET и Avalonia native библиотеки включены, macOS workload не нужен.
- Windows 7/8 не поддерживаются .NET 10. Успешный запуск с нештатными
  изменениями не доказывает совместимость TLS, WebView2 или runtime.

Новые пакеты self-contained, **не single-file**: все DLL/native libraries
должны оставаться рядом с приложением. Trimming/AOT выключены.
PNG/ICNS получены из существующего ICO NexLauncher, без нового рисунка.
Unix execute bits записаны в TAR/ZIP и проверяются даже при сборке на Windows.
Для Mac ZIP также выставляется Unix creator OS в central directory: одного
ExternalAttributes недостаточно для сохранения execute bits обычным unzip.
macOS пакеты не имеют Developer ID подписи/нотаризации: публичную доставку
нужно отдельно проверить по обычным правилам Gatekeeper. Защита не отключается.

```powershell
# PowerShell 7 и .NET SDK 10 на машине сборки; пользователям SDK не нужен.
pwsh -File scripts/Build-UnixRelease.ps1 -RuntimeIdentifier linux-x64
pwsh -File scripts/Build-UnixRelease.ps1 -RuntimeIdentifier osx-x64
pwsh -File scripts/Build-UnixRelease.ps1 -RuntimeIdentifier osx-arm64
```

Результаты: `.artifacts/releases/cross-platform-preview/`, логи:
`.artifacts/platform-builds/<rid>-<run>/`. Скрипт по умолчанию выполняет Debug
build и весь fixture suite, затем Release publish, notices и проверку архива.
`-SkipChecks` допустим для повторной упаковки после отдельно пройденных проверок;
он не подтверждает тестирование. `Test-UnixPackage.ps1` проверяет ELF/Mach-O
архитектуру, execute bits, native resources и self-contained .NET runtime.
Он **не запускает приложение**. `unix-preview.yml` подготовлен для ручного
запуска на native runners; workflow не запускался и ничего не публикует.

Отдельный `scripts/Test-LinuxStartup.py` запускает только готовый launcher в
собственной временной папке `/tmp` с изолированными XDG data/cache/config.
Нужны Python 3.12, X11/WSLg, `xwininfo` и `xprop`; SDK не нужен.
Он проверяет окно именно своего PID, загрузку CoreCLR/Skia из payload и
штатное закрытие через WM_DELETE_WINDOW. Реальные профили не используются,
Minecraft не запускается. Выполненный smoke завершился с exit code 0.
Windows fixture suite: **1095 checks**; настоящий Linux fixture suite:
**1101 checks**, включая owner-only permissions, сохранность case-distinct
ручного JAR и ошибку при действительно отсутствующем libsecret.
Это не проверка Microsoft login, работающего keyring или запуска игры.

Для Ubuntu 24.04 нужны системные зависимости Avalonia/.NET, в частности:

```sh
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libgl1 libicu74 libssl3t64 \
  zlib1g libgcc-s1 libstdc++6 ca-certificates libsecret-1-0 gnome-keyring xdg-utils
tar -xzf NexLauncher-v0.1.1-alpha-linux-x64.tar.gz
./NexLauncher/NexLauncher
```

Список пакетов зависит от дистрибутива. Secret Service должен быть запущен и
разблокирован в пользовательской графической сессии. Само наличие libsecret
недостаточно. В KDE подходит доступный Secret Service provider. `noexec` раздел
не подходит для binaries/Java. `.desktop` приложен как шаблон: после выбора
постоянной папки укажи абсолютные Exec/Icon; автоматической системной установки нет.

## Microsoft-вход: что требуется от владельца

Windows-вход не переводился на Azure и игнорирует новое browser configuration.
На остальных ОС CmlLib документирует MSAL: системный браузер + loopback callback
`http://localhost`, personal account authority `consumers`, scope `XboxLive.signin`.
MSAL управляет PKCE/callback/state и refresh tokens. Пароль вводится в Microsoft.
Далее используется **тот же** Xbox Basic → Minecraft token → ownership check →
Minecraft profile → MSession. Ошибка не переключает пользователя на Local.

Пользователь лаунчера **не регистрирует** Azure app. Один раз это делает владелец:

1. Зарегистрировать NexLauncher как public desktop/native application с personal
   Microsoft accounts. Не создавать/вшивать Client Secret.
2. Разрешить redirect `http://localhost` для desktop application. Не использовать
   Web application confidential flow; не открывать listener на внешнем интерфейсе.
3. Проверить актуальный процесс разрешения Client ID для Minecraft Services.
   Регистрация Entra сама по себе не гарантирует доступа: возможен HTTP 403.
   Заявку подаёт и условия принимает только владелец после своего просмотра.
4. Передать **свой public GUID** сборке:
   `-MicrosoftClientId <ваш-ID>` в `Build-UnixRelease.ps1` (или MSBuild property
   `NexLauncherMicrosoftClientId`). Это публичный ID, не пароль/secret.
5. Для локальной проверки разработчика поддерживается существующее поле
   `MicrosoftClientId` в `settings.json`; bundled ID имеет приоритет.
   Публичным пользователям править JSON не требуется.
6. Проверить обычный вход, MFA, отмену, повторный запуск, silent renewal,
   несколько аккаунтов, ownership/profile и Skin Manager на каждой ОС.

Без ID backend показывает явную ошибку до открытия браузера. Чужие application
IDs, cookie export, ручной ввод токенов и обход TLS/Cloudflare не используются.
Windows session vault нельзя переносить на Mac/Linux; требуется новый вход.

## Хранение и приватность

- Windows: `%LOCALAPPDATA%\NexLauncher`; прежний DPAPI формат не менялся.
- macOS: `~/Library/Application Support/NexLauncher`.
- Linux: `$XDG_DATA_HOME/NexLauncher`, обычно `~/.local/share/NexLauncher`.
- Custom instances остаются в выбранном хранилище; binaries и данные разделены.

Полный CmlLib+MSAL cache хранится в том же version-1 `auth/accounts.dat` и
транзакционно публикуется только после успешной проверки профиля и записи vault.
На Unix это AES-256-GCM с новым nonce на запись; ключ находится в macOS Keychain
или Linux Secret Service через официальный Microsoft MSAL persistence provider.
Рядом с vault **нет открытого ключа**. Системный key record привязан к папке данных;
перенос папки/удаление key record требует повторного входа с сохранением оригинала.
Файлы auth создаются owner-only (`0600`), auth directory — `0700`.
Нет fallback на открытый token cache. Заблокированный/недоступный key store
даёт ошибку и сохраняет предыдущий vault; Local accounts продолжают работать.
Удаление аккаунта удаляет и его MSAL refresh cache из vault. Браузерные cookies
Microsoft остаются под управлением браузера; выход из всех приложений не выполняется.

## Java и Minecraft

CmlLib 4.0.6 выбирает Java по реальным OS/architecture metadata Mojang:
Windows x64, Linux x64, macOS x64/ARM64. Java binary на Unix — `bin/java`
(на Mac внутри `jre.bundle/Contents/Home`); executable bits устанавливает CmlLib.
Minecraft JVM rule `-XstartOnFirstThread` применяется только на macOS.
Перед запуском Java/Forge installer проверяются наличие и execute bits;
ручной Java permissions не меняются автоматически.

Доступность конкретного Java component и игровых native libraries зависит от
версии Minecraft. Старые Minecraft/моды могут не иметь Apple Silicon natives.
Скрытая эмуляция/замена игровых JAR не добавлена. Forge/NeoForge используют
прежний проверенный официальный installer и `ProcessStartInfo.ArgumentList`.
Файловые контейнеры на Unix сравниваются с учётом регистра. Защита от Zip Slip,
небезопасных путей и links/junction не ослаблена.

## Manual checklist на каждой целевой ОС

- Распаковать без source checkout/SDK; проверить запуск, иконку, окно, resize,
  DPI/Retina, кириллицу и путь с пробелами. На Mac открыть `.app` из Finder.
- Проверить file dialogs, открытие папок/HTTPS, Quick CSS и отдельный CSS editor,
  переключение оформления, restart/persistence, старый instance/import/export.
- Создать Local account; вручную установить и запустить Vanilla, Fabric, Forge,
  NeoForge; automatic Java, manual Java, RAM, отмена/repair, native libraries.
- После регистрации ID: Microsoft login/MFA/cancel, ownership, выбранный UUID,
  второй аккаунт, рестарт/silent renewal, Skin Manager. Не переносить Win vault.
- Keychain/libsecret unlock/deny/missing: понятная ошибка, vault не изменён,
  нет plaintext tokens; Local account доступен. Проверить permissions vault.
- Modrinth browse/details/install, скины preview, диагностику/статистику.
- Закрыть лаунчер во время игры: прежняя политика не должна убивать Minecraft.
- Обновить binaries, сохранив instances/settings/skins. macOS signing/notarization
  оценить отдельно перед публичным распространением.

## Первичные источники

- [CmlLib: MSAL на других платформах](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/cmllib.core.auth.microsoft/authentication-with-msal/)
- [CmlLib: свой Client ID и разрешение Minecraft Services](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/)
- [Microsoft: interactive/system-browser authentication](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/acquiring-tokens-interactively)
- [Microsoft: реализация защищённого persistence](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/tree/09b427524fdae98e4bcb1fcf72e2fe50813d48f1/src/client/Microsoft.Identity.Client.Extensions.Msal)
- [Avalonia: macOS bundles](https://docs.avaloniaui.net/docs/deployment/macos), [Linux](https://docs.avaloniaui.net/docs/deployment/linux)
- [.NET 10: поддерживаемые ОС](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
