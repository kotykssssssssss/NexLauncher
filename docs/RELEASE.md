# Windows release v0.1.0-alpha

Цель дистрибутива — Windows 11 x64, обычный пользователь без Visual Studio, .NET SDK,
NuGet cache и checkout проекта. Эта alpha **не подписана** code-signing сертификатом.
Установщик и EXE могут вызвать предупреждение SmartScreen о репутации. Не отключай
SmartScreen/антивирус; проверь источник загрузки и SHA256. ARM64, Windows 10,
Linux/macOS и каждый возможный Minecraft/modpack в этом релизе не сертифицированы.

## Что скачивает пользователь

| Пакет | Как использовать |
| --- | --- |
| `NexLauncher-Setup-v0.1.0-alpha.exe` | Рекомендуемый installer; установка для текущего пользователя, Start Menu shortcut, необязательный Desktop shortcut, uninstall. |
| `NexLauncher-v0.1.0-alpha-win-x64.zip` | Распаковать в отдельную локальную папку, запустить `NexLauncher.exe`. Не запускать прямо из ZIP. |

В обоих пакетах одинаковый **self-contained single-file** `NexLauncher.exe`:
.NET Core Runtime и Windows Desktop Runtime **10.0.12**, Avalonia и managed/native
dependencies включены в bundle. Отдельный .NET Runtime/SDK не требуется.
В ZIP также лежат README, LICENSE, THIRD-PARTY-NOTICES и DEPENDENCIES — это документы,
не обязательные runtime-файлы. Сохраняй их при распространении.

Native библиотеки bundle извлекаются runtime в пользовательский `%TEMP%\.net`.
Нужны доступная для записи TEMP и локальная папка данных. Это настоящий single-file
payload с извлечением native компонентов, а не EXE, скачивающий себе .NET.
Trimming, Native AOT и bundle compression выключены ради совместимости.
Нет отдельной новой иконки: подходящий открытый `.ico` в используемых source assets
не задан, поэтому launcher сохраняет стандартный executable icon.

### WebView2, Java и сеть

Microsoft login через CmlLib/XboxAuthNet использует **Microsoft Edge WebView2 Evergreen
Runtime**. Его browser engine не является частью .NET и не входит в bundle.
Обычно он установлен в Windows 11. Installer проверяет per-user/per-machine registry
registration; при отсутствии запускает подписанный Microsoft Evergreen bootstrapper
без администратора. Нужен интернет. Если установка runtime не удалась, доступен
Local Account; для Microsoft-входа установи Evergreen Runtime с
[официальной страницы Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).
Portable runtime не устанавливает. Native `WebView2Loader.dll` включён в EXE.

Microsoft OAuth/DPAPI не переписаны. Библиотека XboxAuthNet сохраняет browser profile
в `%USERPROFILE%\.msal\webview2\data`; NexLauncher credentials остаются в
зашифрованном `%LOCALAPPDATA%\NexLauncher\auth\accounts.dat`. Uninstall не очищает
этот browser profile и не удаляет общий WebView2 runtime.

Minecraft/Java/loaders/mods/modpacks в distribution **не включены**. Они загружаются
по запросу через существующий pipeline. Интернет нужен для установки/repair,
официальных каталогов, Modrinth и Microsoft auth. Local Account не означает
полностью автономную работу без сети. Java выбирается для Minecraft автоматически;
ручной путь и RAM остаются в настройках конкретной сборки. Требования игры/модпака
к GPU, драйверам, RAM и свободному месту действуют отдельно.

## Данные, установка и обновления

- Binaries: по умолчанию `%LOCALAPPDATA%\Programs\NexLauncher`.
- Данные: `%LOCALAPPDATA%\NexLauncher`; игры могут быть в custom instances directory.
- Portable и Installer используют одни данные. Не запускай их одновременно.
- Stable Inno AppId сохраняется между версиями; reinstall/upgrade использует прежнюю
  installation directory. При обновлении закрой launcher. Автоматического updater нет.
- Uninstall удаляет файлы дистрибутива и ярлыки, сохраняет settings, auth metadata/vault,
  themes, saves, mods, screenshots, resource/shader packs и custom instances.
- Не устанавливай binaries в папку игры или данных. Setup запрещает стандартную
  папку данных; произвольная custom game directory не распознаётся автоматически.
- Portable обновляй заменой application files после закрытия EXE. Важные миры
  резервируй отдельно. Перенос DPAPI vault между Windows users не поддерживается.
- Новых migrations форматов settings/accounts в release-этапе нет.

## Локальная сборка

Нужны Windows x64, **.NET 10 SDK**, **PowerShell 7**, интернет для NuGet и официальных
build prerequisites. Это требования разработчика, а не конечного пользователя.
Запускай из checkout:

```powershell
pwsh -NoProfile -File scripts/Build-Release.ps1
```

Скрипт выполняет Debug/Release build с warnings-as-errors, полный offline check suite,
publish через `Properties/PublishProfiles/Windows-x64.pubxml`, проверку единственного
EXE и bundled runtime versions, сбор notices по фактическому deps graph, упаковку
ZIP и Inno installer. Не создаёт commit/push/GitHub Release.

Готовые файлы:

```text
.artifacts/releases/v0.1.0-alpha/
  NexLauncher-Setup-v0.1.0-alpha.exe
  NexLauncher-v0.1.0-alpha-win-x64.zip
  SHA256SUMS.txt
  release-manifest.json
```

Logs, промежуточные publish/payload и точный unpackaged EXE:
`.artifacts/release-builds/0.1.0-alpha-<run-id>/`. Каждый прогон получает новую папку,
поэтому в payload не попадают остатки старых DLL/тем/debug files. В общий releases
directory копируются только известные filenames после успешной сборки. Manifest
содержит SDK/runtime versions, размеры, SHA256, unsigned status и hash WebView bootstrapper.

`scripts/Get-ReleaseTools.ps1` скачивает **Inno Setup 7.1.0** только из официального
GitHub release, проверяет закреплённый SHA256 и Authenticode publisher Pyrsys B.V.,
распаковывает compiler в portable mode внутри `.artifacts/tools`. В систему compiler
не устанавливается. Microsoft bootstrapper приходит с официального Microsoft URL,
проверяется подпись Microsoft Corporation; его Evergreen версия обслуживается
Microsoft. Cached подписанный bootstrapper используется повторно. Для его обновления
удали только `.artifacts/tools/MicrosoftEdgeWebview2Setup.exe` и пересобери.

Inno выбран за per-user install, обычные shortcuts/uninstall и стабильный AppId без
необходимости Store identity или сертификата. Build tool сохраняет свои notices;
условия [Inno Setup](https://jrsoftware.org/isinfo.php) нужно повторно проверить перед
коммерческим использованием. Это воспроизводимый процесс, но **не обещание побитовой
идентичности** при изменении SDK, Evergreen bootstrapper или timestamps installer.

Лицензии NuGet/native runtime packages берутся из restored packages; недостающие
upstream notices закреплены в `packaging/licenses` с provenance. Inter имеет отдельную
OFL. Новый runtime dependency без notice останавливает packaging. Внешние NuGet
версии приложения сохранены. Перед будущим релизом обновляй .NET servicing patch
в profile и соответствующую проверку в script; self-contained приложение не получает
исправления runtime от системного .NET автоматически.

Ручная эквивалентная publish-команда:

```powershell
dotnet publish NexLauncher.csproj -c Release -p:PublishProfile=Windows-x64 `
  --artifacts-path .artifacts -o .artifacts/manual-publish
```

GitHub Actions `.github/workflows/windows-release.yml` запускается только вручную
через `workflow_dispatch`: build/check/package и upload artifacts, без публикации Release.
GUI и clean Windows smoke tests CI workflow не подменяет.

## Автоматическая и локальная проверка

```powershell
dotnet run --project tests/NexLauncher.Checks -c Release --artifacts-path .artifacts
pwsh -NoProfile -File scripts/Test-Release.ps1 `
  -PortableZip .artifacts/releases/v0.1.0-alpha/NexLauncher-v0.1.0-alpha-win-x64.zip
```

Второй script извлекает allowlisted ZIP в новую папку с пробелами/Unicode, запускает
реальный EXE с PATH только System32, без developer DOTNET/NuGet variables, отдельным
bundle cache и недоступным DOTNET_ROOT. Проверяет responsive window, self-contained
runtime 10.0.12 через host trace, Skia из bundle cache, наличие WebView2Loader,
отсутствие native PDB и чистое закрытие. Windows singlefilehost статически включает
CoreCLR; отдельного `coreclr.dll` в списке загруженных modules может не быть. Он использует
**текущий Windows-профиль**, читает существующие launcher settings и запускает
обычное обновление каталога. Закрой NexLauncher до проверки. Это не fresh-user/VM test.
Не удаляет user data. Результат — `.artifacts/release-smoke/.../smoke-result.json`.

Network/loader smoke отдельно, по желанию:

```powershell
dotnet run --project tests/NexLauncher.Checks -c Release --artifacts-path .artifacts -- `
  --network --modded-network --loader-install-smoke
```

Smoke использует официальные API и installers, проверяет install/repair и CmlLib
BuildProcess для Minecraft 1.21.1 + Fabric 0.16.10 / Forge 52.0.28 / NeoForge 21.1.172
с Mojang Java 21. Не входит в обязательный offline suite и **не запускает игровое окно**.
Подробные результаты именно подготовленной alpha записаны в `RELEASE_VALIDATION.md`.

## Manual UI checklist Modrinth

- [ ] Открыть Modrinth в maximized/1920×1080, обычном 1060×760 и минимальном 900×650
  окне; изменить размер при открытом каталоге и details. Каталог занимает рабочую
  область после sidebar, нет перекрытий/горизонтального обрезания.
- [ ] Проверить Windows scaling 100%, 125%, 150% и перенос между мониторами.
- [ ] Переключить **Modpacks / Mods**, выбрать instance, проверить закреплённые
  Minecraft + loader. Vanilla не должна появляться как цель модов.
- [ ] Search, реальные фильтры Minecraft/loader/category/environment и сортировка;
  предыдущая/следующая страница. В узком окне раскрыть фильтры над результатами.
- [ ] Длинные названия/описания и много tags: карточка читается, прокручивается,
  вся её площадь открывает details. Иконки и attribution видны, если доступны.
- [ ] Details: полное описание, ссылки, выбранная версия, metadata; длинное описание
  прокручивается. **К результатам** сохраняет запрос/фильтры/sort/page/scroll.
  Переключение вкладок сохраняет отдельные поиски и target instance.
- [ ] Install → review dependencies → confirm; installed version, update, remove;
  несовместимый loader/version не включает установку. Manual JAR сохраняются.
- [ ] Loading/cancel, offline/error/retry, no-results, отсутствие instances/mods.
  Отмена и ошибки не оставляют заблокированные controls.
- [ ] Quick CSS в каталоге/details, `.project-card:hover`, выключение темы;
  открытие/закрытие журнала и progress при установке.

Offline suite проверяет настоящий MainWindow на трёх размерах, геометрию и state;
это не заменяет проверку native input, системного DPI и визуальную оценку пользователем.
Opt-in проверка реальных Modrinth API и screenshot rendering:

```powershell
dotnet run --project tests/NexLauncher.Checks -c Release --artifacts-path .artifacts -- --browser-live
```

## Обязательный manual checklist перед публикацией

Используй чистую Windows 11 x64 VM или отдельный обычный Windows account. На VM не
должно быть VS, .NET SDK/runtime, исходников, NuGet cache и `%LOCALAPPDATA%\NexLauncher`.
Не удаляй свои реальные данные ради имитации clean test. Сделай snapshot VM.
Скопируй только ZIP/Setup и SHA256SUMS; не делись auth vault или launcher logs без проверки.

- [ ] Проверить SHA256 (`Get-FileHash <file> -Algorithm SHA256`) и отсутствие подписи
  как ожидаемое состояние alpha. Пройти обычный Windows security flow без отключения защиты.
- [ ] **Fresh installer:** запуск обычным пользователем без UAC elevation, правильная
  version/name, Start Menu, необязательный Desktop shortcut, приложение открывается.
- [ ] **Fresh portable:** из ZIP в папке с пробелами/Unicode; никаких loose DLL/.NET
  рядом не нужно. Повторить на fresh snapshot, проверить writable TEMP/data folders.
- [ ] Первое создание сборки/Local Account/Quick CSS создаёт user directories;
  binaries не получают settings/auth/instances рядом с собой.
- [ ] **Microsoft:** вход владельцем Java Edition, профиль/avatar/type, отмена окна,
  повторный запуск без постоянного OAuth, переключение и удаление. Если WebView2
  отсутствует, проверить его установку Setup; offline failure понятен, Local доступен.
- [ ] **Local:** валидное/невалидное имя, сохранение/выбор, Microsoft ↔ Local,
  явный Local label. Online-mode сервер отвергает offline-клиента.
- [ ] **Vanilla:** создать 1.21.1, установить без заранее установленной Java,
  дождаться главного меню, создать мир, закрыть игру, повторно запустить.
- [ ] **Fabric**, **Forge**, **NeoForge:** отдельные 1.21.1 instances с loader versions
  выше; установить и реально открыть игру/мир каждой сборки. Проверить, что миры отдельные.
- [ ] **Java/RAM:** auto Java, ручная поддерживаемая Java в пути с пробелами, ошибочный
  путь даёт понятную ошибку; сохранённая RAM доходит до игры.
- [ ] **Modrinth mod:** «Играть → Моды» → поиск → details/full description/links →
  совместимая версия → install + required dependencies → реально запустить игру с модом.
  Проверить update/remove, отказ на несовместимость, сохранность manual JAR.
- [ ] **Modpack:** details → version → install; новая отдельная сборка, правильные
  loader/MC, overrides/configs, реальный старт. Старый instance остаётся неизменным.
- [ ] **Custom folder:** диск D:/E: либо другая доступная папка с Unicode/пробелами;
  новые instances там, прежние на старых путях; недоступный/read-only диск даёт ошибку.
- [ ] **Restart:** selected account/instance, custom folder, RAM/Java, installed mods,
  theme и saves сохраняются после закрытия/restart.
- [ ] **Quick CSS:** «Создать пример» без исходников рядом, заметно меняется UI;
  редактирование/reload/watcher, ошибочное правило, выключение и возврат стандартной темы.
- [ ] **Repair:** на тестовом instance удалить один известный managed library JAR;
  проверка восстанавливает файл и игра запускается, saves/manual mods сохранены.
- [ ] **Cancellation/errors:** отменить game/loader/mod/pack install, отключить сеть;
  UI разблокирован, частичный pack не считается installed, повторная операция работает.
- [ ] **Uninstall/reinstall:** завести мир, mod, CSS, оба типа аккаунта, custom folder;
  сохранить hashes settings/auth, удалить приложение, проверить сохранность данных,
  поставить снова и открыть прежнюю сборку. WebView2 отдельно не удаляется.
- [ ] **Upgrade rehearsal:** переустановить эту alpha поверх себя; проверить сохранение
  путей/данных и единственную uninstall entry. После появления следующей версии повторить
  настоящий version-to-version upgrade — этот сценарий пока нельзя подтвердить.

Публикуй только после прохождения checklist. Полноценная VM, ввод Microsoft credentials,
игровое меню/мир и отсутствие WebView2 требуют ручной проверки; successful build их не заменяет.

## Официальные источники упаковки

- [.NET single-file deployment и native extraction](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [.NET deployment/self-contained](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
- [Avalonia Windows](https://docs.avaloniaui.net/docs/platform-specific-guides/windows)
- [Microsoft WebView2 distribution/detection](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- [Inno Setup official downloads](https://jrsoftware.org/isdl.php)
- [Inno Setup documentation](https://jrsoftware.org/ishelp/)
