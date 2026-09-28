# v0.1.1-alpha — patch validation, 28 сентября 2026

Патч подготовлен поверх `261cd96` (`v0.1.0-alpha`). До изменений рабочее дерево
было чистым, baseline — 439/439 checks. Релизные скрипты и работающая архитектура
сохранены. Commit, tag, push и GitHub Release не создавались.

## Исправление

API уже корректно передавал metadata. XAML ItemTemplate показывал только
`VersionNumber`, а подпись выбора не включала Minecraft. Теперь Mods и Modpacks
используют общий формат модели: номер проекта и `Minecraft … · loader` в dropdown;
полные версии Minecraft/loaders, канал и дата — под ним и в tooltip. Несколько
значений перечисляются без выдуманных диапазонов; длинные списки сокращаются
только в dropdown с `(+N)`. Неизвестные значения не подменяются догадками.

Изменены `Models/ModrinthModels.cs`, `ViewModels/ModrinthBrowserState.cs`,
`Views/ModrinthView.axaml`, release metadata/notes и regression checks.
Добавлен `tests/NexLauncher.Checks/ModrinthVersionDisplayChecks.cs`.
API mapping, install/dependency/compatibility pipeline, accounts/DPAPI и форматы
данных не менялись. Новых dependencies нет. `Assets/very important asset/`
и NexVisuals не затронуты. Больших удалений в patch diff нет.

## Результаты этого патча

| Проверка | Результат |
| --- | --- |
| Debug / Release, `-warnaserror` | Оба успешны, 0 warnings / 0 errors |
| Полный offline regression suite, Release | **463/463** |
| Suite + `--browser-install-live --modded-network`, Debug | **501/501** (463 + 38 opt-in) |
| Windows x64 self-contained publish / Inno Setup | Успех |
| Финальный Portable EXE без developer environment | Responsive window, bundle runtime/Skia, exit 0 |
| Installer: 0.1.0 → 0.1.1, startup, reinstall, uninstall | **20/20** отдельных checks |

Настоящие MainWindow/ModrinthView отрисованы в Avalonia.Headless/Skia; снимки
визуально просмотрены. Проверены dropdown и подпись Fabulously Optimized/Sodium
на реальных API-данных, 1920×1080 и 1060×760, прокрутка до последнего релиза;
fixtures проверяют длинный номер, много game versions/loaders и узкую боковую
панель. Отсутствующие/null metadata имеют отдельные checks. Это не native input/DPI test.

Fabulously Optimized `15.0.0-alpha.4` на момент проверки сообщает **Minecraft 26.3,
Fabric** — это показано из API, не из примера/названия релиза. Выбор другого релиза
`jPRbzNeS` сверён отдельным `/version/{id}` запросом. В live Mods выбран Sodium
`mc1.21.1-0.6.9-fabric` (`26nVNc41`), поддерживающий Minecraft 1.21 и 1.21.1.
Он реально установлен командами details UI в изолированную папку: ID совпал,
SHA512 JAR проверен, неизвестный manual JAR остался. Игровое окно не запускалось.
Выбор и установка modpack по точному ID проверены через fixture API, настоящий
staging/manifest pipeline и fake Minecraft backend; полный live modpack не скачивался.

В Installer проверено сохранение неизвестного файла при upgrade/reinstall/uninstall
и неизменность хешей восьми существующих файлов settings/account metadata/themes.
Portable и Installer проверялись в текущем Windows-профиле, **не в clean VM**.

## Дистрибутивы

Папка: `.artifacts/releases/v0.1.1-alpha/`.

| Artifact | Точный размер, байт |
| --- | ---: |
| `NexLauncher-Setup-v0.1.1-alpha.exe` | 46 026 278 |
| `NexLauncher-v0.1.1-alpha-win-x64.zip` | 60 652 617 |
| `NexLauncher.exe` внутри обоих пакетов | 148 340 115 |

ZIP содержит настоящий self-contained single-file EXE и документы. Runtime/SDK
пользователю не нужен; оба пакета unsigned. Требования WebView2 и прочие alpha
ограничения прежние — см. [RELEASE.md](RELEASE.md). SHA256 записаны в
`SHA256SUMS.txt` и `release-manifest.json` рядом с пакетами.

Локальные evidence (не входят в git/distribution):

- Build/publish/check logs: `.artifacts/release-builds/0.1.1-alpha-8ccc6dc0e7044cd386e3371e9ec9d928/logs/`.
- Live API/UI/install: `.artifacts/patch-live-checks.log`; screenshots/data: `.artifacts/checks/b75c646555674786bed139f7ee2b868d/`.
- Portable: `.artifacts/release-smoke/Windows Релиз 3c9fbd710e92473c8774b815d3c08a23/smoke-result.json`.
- Installer: `.artifacts/installer-smoke/Установка 5e94c2e0d481493f86cfb7229f4d8cb7/result.json`.

## Перед публикацией вручную

- [ ] Проверить native mouse/keyboard dropdown, resize и Windows scaling 125/150%,
  в том числе с пользовательской Quick CSS темой.
- [ ] Выбрать конкретный Fabulously Optimized release, установить полный pack,
  сверить Minecraft/loader/версию и запустить игру. Повторить с выбранным модом.
- [ ] Проверить Microsoft login/refresh и игровой запуск своих Microsoft/Local accounts.
- [ ] На чистой Windows 11 проверить Portable, Installer, обновление и сохранность
  собственных instances/settings. Полный checklist остаётся в [RELEASE.md](RELEASE.md).

Исторические проверки ниже относятся только к `v0.1.0-alpha`; игровые install/repair
и startup из них не объявляются повторно выполненными для текущего патча.

---

# v0.1.0-alpha — результаты проверки 28 сентября 2026

Рабочее дерево подготовлено к финальной ручной проверке, **не к публикации без неё**.
Commit, push и GitHub Release не создавались. Базовый HEAD — `09aa8a9`.
`Assets/very important asset/` не менялась; Git diff для неё пустой.

## Продолжение предыдущего запуска

Уже существовали release metadata, self-contained publish profile, Inno installer,
packaging scripts, notices, CI workflow и первоначальная документация. Они сохранены.
Исходный pre-release suite содержал 392 checks; при возобновлении release baseline
составил 399/399. Прерванная проверка реального Vanilla startup фактически успела
завершиться: найден result.json с responsive window и exit code 0.

Проверены все 101 staged deletion: это ранее отслеживаемые `bin/obj` outputs,
удалённые из Git index, а не с диска. Полезные исходники/ресурсы не удалялись.
Большая замена исходного `ModrinthView.axaml` намеренная: прежний ограниченный
список заменён каталогом и полной страницей проекта. Существующие installers,
auth/DPAPI, instances, Java/RAM, transaction/hash/path checks сохранены.

## Modrinth release blockers и исправления

- Каталог был внутри общего ScrollViewer/StackPanel с ограничением высоты. Теперь
  он занимает растягиваемую строку `*` MainWindow и всю ширину content area.
- Добавлены отдельные Modpacks/Mods, выбор instance, фильтры официального API,
  сортировка и независимое состояние поиска двух режимов.
- Горизонтальные карточки кликабельны целиком; показывают icon, автора/organization,
  описание, tags, совместимость, downloads, дату и известную установленную версию.
  Обновления определяются на details после запроса совместимых версий, не угадываются
  по search hit; catalogue не делает N дополнительных запросов для каждой карточки.
- Details заменяет каталог; full native Markdown, metadata, links, версия и
  установка. Back сохраняет query/filter/sort/page/scroll; выбор instance сохраняется
  при переключении режимов. Состояние поиска живёт только до закрытия приложения.
- В узком окне фильтры раскрываются сверху с ограниченной высотой и собственным
  scroll; в широком — слева. Исправлены обрезание фильтров и вытеснение результатов.
- Исправлена пагинация: неудачный запрос следующей страницы не подменяет номер
  текущих результатов. Пустой список instances имеет отдельное понятное состояние.
- Новый установленный modpack сразу появляется в целевом списке Mods; публикация
  instance больше не сбрасывает открытую страницу Modpacks в режим Mods.
- Журнал доступен и на новой странице. Убран прежний неиспользуемый отдельный
  обработчик проверки update; актуальность версии определяется в project details.
- Первое нажатие установки мода строит план, второе подтверждает его. Повторно
  проверяются выбранная версия/dependencies. Already installed version отключает
  действие; modpack всегда создаёт новый instance. Pack version chooser учитывает
  выбранные Minecraft/loader filters, Mods — точный target instance.
- Добавлены стабильные Quick CSS selectors для каталога, фильтров, карточки и details.
  Базовые стили остаются в App.axaml, пользовательские overrides накладываются сверху.

Фактическое API и ограничения описаны в [MODRINTH.md](MODRINTH.md), selectors —
в [QUICK_CSS.md](QUICK_CSS.md). Новых runtime NuGet dependencies и migrations нет.

## Финальные builds и проверки

| Проверка | Результат |
| --- | --- |
| Debug build, `-warnaserror` | Успех, 0 warnings / 0 errors |
| Release build, `-warnaserror` | Успех, 0 warnings / 0 errors |
| Полный offline automated suite | **439 / 439** |
| Тот же suite + live API/browser + loader install/repair | **465 / 465** (439 + 26 opt-in) |
| Self-contained publish / Inno compilation | Успех |
| Portable EXE startup, bundled runtime/native libraries | Успех, responsive window, exit 0 |
| Installer/start/reinstall/uninstall | **17 / 17** отдельных checks |

Suite включает Vanilla, Microsoft backend/session mocks и Windows DPAPI storage,
Local identity/session, persistence, Java/RAM guards, Quick CSS parser/runtime,
loaders, search/details/compatibility/dependencies, mrpack staging/rollback, hashes,
manual-file preservation, cancellation, network errors и filesystem guards.

Новые checks используют настоящий MainWindow в Avalonia.Headless/Skia на 1920×1080,
1060×760 и 900×650, resize, раскрытые фильтры, long names/descriptions, whole-card
commands, version filtering, Back/scroll и Quick CSS overrides. Live browser проверен
на настоящих ответах Modrinth (Fabulously Optimized, Sodium), включая icon, body,
versions, attribution и возврат. Это не native mouse/keyboard/DPI automation.

Ранее выполненный NuGet restore audit с mode=all не выдал vulnerability warnings.
Отдельный formatter в проекте не настроен. `git diff --check` проходит.

## Реальные игровые проверки

| Minecraft 1.21.1 | Проверено |
| --- | --- |
| Vanilla | Реальный MinecraftService launch, responsive Minecraft window, штатное закрытие, exit 0 |
| Fabric 0.16.10 | Официальный install/repair, CmlLib profile/Java 21, реальный startup/close, exit 0 |
| Forge 52.0.28 | Официальный install/repair, CmlLib profile/Java 21, реальный startup/close, exit 0 |
| NeoForge 21.1.172 | Официальный install/repair, CmlLib profile/Java 21, реальный startup/close, exit 0 |
| Fabric + Modrinth mod | Через ModManager установлен Fabric API `0.116.17+1.21.1` (`P7dR8mSH` / `Mys3P7lK`); Minecraft log подтверждает загрузку мода, окно отвечает, exit 0 |

Игровые окна оставались открытыми ещё 18 секунд после обнаружения responsive window,
затем закрывались штатно. Миры не создавались. Уже скачанные тестовые instances
использованы повторно; игровых данных пользователя эти проверки не меняли.
Real modpack install+world и полная интерактивная Microsoft OAuth цепочка остаются
ручными проверками; автоматические fixtures этих подсистем их не заменяют.

## Distribution и данные

Оба пакета содержат одинаковый self-contained **single-file** `NexLauncher.exe`:
.NET и Windows Desktop runtime 10.0.12 включены, отдельный Runtime/SDK не нужен.
Native библиотеки извлекаются в пользовательский bundle cache. Trimming/AOT выключены.
Portable ZIP добавляет только README/LICENSE/notices/dependency inventory.
Microsoft login дополнительно требует WebView2 Evergreen; Installer использует
официальный подписанный Microsoft bootstrapper при отсутствии runtime.

Portable запущен из отдельной папки с Unicode/пробелами, PATH только System32,
без developer DOTNET/NuGet variables, с недоступным DOTNET_ROOT и новым bundle cache.
Host trace подтверждает single-file self-contained 10.0.12; Skia загружена из bundle,
WebView2Loader присутствует, PDB отсутствуют. Window responsive, exit code 0.
PE import tables четырёх native DLL проверены: прямой зависимости от внешнего
VC++ redistributable не обнаружено, перечислены системные Windows DLL.

Inno per-user install проверен без elevation: отдельная папка с Unicode/пробелами,
Start Menu shortcut, HKCU uninstall metadata, launch, reinstall той же версии,
uninstall. Установленный EXE идентичен portable. Неизвестный контрольный файл
сохранён. Hashes восьми существующих settings/account/vault/theme files не изменились.
Тестовая установка затем удалена. User data и custom game directories не удалялись.

Это проверки на существующей Windows development machine, **не clean Windows VM**.
Не проверены автоматически: полный Microsoft login/refresh с реальными credentials,
отсутствующий WebView2/bootstrap installation, game worlds/multiplayer, реальная
установка и запуск modpack, системные DPI/native input, version-to-version upgrade.

## Release artifacts

| Файл | Точный размер, bytes | MiB |
| --- | ---: | ---: |
| `NexLauncher-Setup-v0.1.0-alpha.exe` | 46 030 280 | 43.90 |
| `NexLauncher-v0.1.0-alpha-win-x64.zip` | 60 649 167 | 57.84 |
| `NexLauncher.exe` внутри обоих пакетов | 148 336 019 | 141.46 |

Пакеты: `.artifacts/releases/v0.1.0-alpha/`, рядом `SHA256SUMS.txt` и
`release-manifest.json`. EXE и Setup: Authenticode **NotSigned**. SmartScreen не обходился.

ZIP SHA256: `f82cc6ebeae1de6fb611daff0d644a7f39be6408d3b14a4e34f7c64a780c2faf`.
Setup SHA256: `50f82934b37542a31e5bf4817f77da9f3fe9d6bc6f980d55ff2de6d89690095c`.

ZIP имеет ровно пять allowlisted файлов, без auth/settings, Minecraft/Java/mods,
logs, PDB, source или NuGet cache. Сканирование 103 текстовых source/config/doc files
по известным secret/personal-path patterns не обнаружило совпадений. В EXE не найдены
проверенные developer-path markers. Это ограниченный pattern/content audit, не
обещание универсального обнаружения любых секретов.

## Локальные evidence

- Build/check/publish/installer logs: `.artifacts/release-builds/0.1.0-alpha-7f412c7609b34ceab8c3996893f82031/logs/`.
- Unpackaged single EXE: та же папка run, `payload/NexLauncher.exe`.
- Live suite: `.artifacts/final-live-checks.log`.
- UI screenshots: `.artifacts/checks/39f06a2d59444cc68f5a7da4369bc908/browser/` и `browser-live/`.
- Portable: `.artifacts/release-smoke/Windows Релиз 27396d11fca34519a37617c8f33cb194/smoke-result.json`.
- Installer: `.artifacts/installer-smoke/Установка bda150c6fc9e4f7c91ae6593c1efa5bd/result.json`.
- Vanilla: `.artifacts/game-boot-results/Vanilla-24b344fccdb24e049bdcd78ce96c5158/result.json`.
- Fabric: `.artifacts/game-boot-results/Fabric-aa4fc3a084a249f7b945b438dbee8847/result.json`.
- Forge: `.artifacts/game-boot-results/Forge-e14e0dbf35384f6c815877f1eba75ddc/result.json`.
- NeoForge: `.artifacts/game-boot-results/NeoForge-3c934fbfca4f4f42b6715a6eba84ff4d/result.json`.
- Fabric + mod: `.artifacts/game-boot-results/Fabric-735c35b3ee71475885cbde72afc39435/` (mod-result.json, result.json, sanitized game.log).
- Content audit: `.artifacts/final-source-audit.json`, `.artifacts/final-artifact-audit.json`.

Evidence не входит в дистрибутив/Git. Для будущей сборки использовать
`scripts/Build-Release.ps1`; новые timestamps/hash/run-id ожидаемы.
Перед публикацией пройти **оба checklist** в [RELEASE.md](RELEASE.md): UI и clean-machine
release scenarios. Рабочее дерево и artifacts готовы именно к этой ручной проверке.
