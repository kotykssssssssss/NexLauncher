# macOS/Linux preview и иконки навигации — 20 октября 2026

Версия остаётся `0.1.1-alpha`, пакеты — локальные preview для проверки владельцем.
Заявки/формы не отправлялись, Client ID не регистрировался, CI не запускался.
Commit/tag/push/GitHub Release не создавались. Предшествующие изменения сохранены.

## Что изменено

- `NexLauncher.csproj`, Checks csproj и `Unix.pubxml`: Windows сохраняет
  `net10.0-windows`/WebView2/WindowsDesktop; macOS/Linux получают `net10.0`,
  self-contained runtime 10.0.12 без WindowsDesktop/WebView2. Trimming/AOT выключены.
- `CmlMinecraftAuthenticationBackend` переиспользует прежний Xbox Basic,
  Minecraft ownership/profile и session pipeline. Большое удаление из
  `WindowsMinecraftAuthenticationBackend` — перенос общего кода в этот класс;
  Windows Interactive/Silent WebView2 configuration сохранён.
- `BrowserMinecraftAuthenticationBackend`, `PlatformAuthentication`:
  MSAL system browser, consumers/loopback, per-account refresh cache в том же
  encrypted vault transaction. Проверка UUID блокирует подмену выбранного профиля
  при silent launch/skin refresh. Чужие Client IDs не используются.
- `SystemAccountDataProtector`/`SystemCredentialKeyStore`, `ProtectedAccountVault`:
  Unix AES-256-GCM, случайный ключ в системном Keychain/libsecret через официальный
  MSAL provider; Windows DPAPI без смены формата. Нет plaintext fallback;
  Unix auth directory 0700, новые vault files 0600.
- `MinecraftRuntimePolicy`, MinecraftService/LoaderInstaller: проверка наличия
  и execute bits Java до запуска и перед installed marker. Ручной Java chmod не меняется.
- SafePaths и Export: регистрозависимые Unix пути; case-distinct manual JAR не
  может молча исчезнуть из export как якобы managed. Непереносимые коллизии блокируются.
- MainWindow, страницы статистики/диагностики: векторные cube/T-shirt/stethoscope/chart
  иконки, русские названия «Исправление неполадок»/«Статистика», доступные имена
  кнопок. Длинный label переносится; цвет иконок следует `.nav`/`.nav.active` Quick CSS.
  На Mac редактор CSS использует Cmd+S. Внутренние IDs/schema/CSS selectors не переименованы.
- Unix branding PNG/ICNS получен из существующего ICO. Build-UnixRelease,
  Test-UnixPackage, Test-LinuxStartup и manual-only Unix CI добавлены; notices MSAL включены.
- README, PRIVACY, ACCOUNTS, MICROSOFT_AUTH, PLATFORMS и черновик заявки актуализированы.

## Фактические проверки

| Проверка | Результат |
|---|---|
| Исходный baseline на Windows | 1046 checks |
| Финальный Windows full regression | **1095 checks**, успешно |
| Переносимый net10.0 full suite на Windows | **1095 checks**, успешно; это не native Linux test |
| Native Ubuntu 24.04 / WSL2 full suite, без SDK | **1101 checks**, exit 0 |
| Windows Debug / Release / publish | 0 compiler warnings/errors, `-warnaserror` |
| Переносимый net10.0 Debug build | 0 compiler warnings/errors, отдельная папка artifacts |
| Linux x64, Mac Intel, Mac ARM64 Release publish | 3 успешных self-contained publish + проверки архивов |
| Windows Portable startup | Окно responsive, собственный runtime/Skia, WebView2 loader bundled, штатный exit 0 |
| Linux GUI startup | Настоящая Ubuntu 24.04 / WSLg, **нет dotnet SDK/runtime в PATH**, окно своего PID, собственный CoreCLR/Skia, штатный exit 0 |
| Layout иконок | Headless Skia на Windows/Linux, 900×650 и 1920×1080, перенос label и цвет active Quick CSS проверены |
| git diff --check / generated files / protected asset | Успешно; пользовательских vault/cache/packages в Git status нет |

В ходе native suite обнаружен Windows-only fixture недоступного диска.
Windows assertion сохранён; для Unix добавлен соответствующий недоступный parent.
Failure не подавлялся. Дополнительные native checks проверяют permissions,
неисполняемую manual Java, case-distinct JAR и **фактическое отсутствие libsecret**:
оно возвращает безопасную ошибку и не создаёт plaintext/token vault.

Windows smoke использует существующий профиль Windows, **не clean VM**.
Linux GUI smoke использует собственную `/tmp` папку и изолированные XDG directories;
это WSLg, **не тест физической Ubuntu/dual boot**. Microsoft login, успешная
Keychain/Secret Service persistence и Minecraft launch не выполнялись.
Mac запуск вообще не проверен. Minecraft автоматически не запускался.

## Готовые локальные пакеты

Все пути ниже относительно `E:\Projects\NexLauncher\.artifacts\releases\cross-platform-preview\`.

| Файл | Точный размер, bytes |
|---|---:|
| NexLauncher-Setup-v0.1.1-alpha.exe | 47 401 458 |
| NexLauncher-v0.1.1-alpha-win-x64.zip | 62 522 796 |
| NexLauncher-v0.1.1-alpha-linux-x64.tar.gz | 49 446 362 |
| NexLauncher-v0.1.1-alpha-osx-x64.zip | 51 781 336 |
| NexLauncher-v0.1.1-alpha-osx-arm64.zip | 49 423 803 |

SHA256 для всех пяти: `SHA256SUMS.txt`; сводка: `validation-manifest.json`.
Windows — прежние Installer и single-file EXE внутри ZIP; Mac — `.app` ZIP;
Linux — portable directory TAR.GZ с execute bits. Unix не single-file.
У Mac ZIP проверены Unix creator OS и mode `100755` для executable, чтобы
распаковщики учитывали права исполнения. Реальная распаковка `unzip` и запуск
на macOS не проверены: `unzip` в тестовой Ubuntu отсутствует, Mac недоступен.
Пользователю не нужны SDK/Visual Studio/.NET Runtime. Linux требует системные
desktop библиотеки. Пакеты без Developer ID/code-signing/notarization владельца.

Evidence в `.artifacts/`: `platform-windows-final.log`,
`platform-unix-final-checks.log`, `platform-unix-debug-final.log`,
`platform-linux-final.log`, `platform-mac-x64-final.log`,
`platform-mac-arm64-final.log`, `native-linux-regression/checks.log` и `result.json`,
`native-linux-smoke/smoke-result.json`. Windows smoke:
`release-smoke/Windows Релиз 529f851014984d28afc697673df0edda/smoke-result.json`.
Последний Windows build: `release-builds/0.1.1-alpha-a58a8e8d5fcc41978a8f6d7951ca454d/logs/`.

## Что остаётся владельцу

**Microsoft-вход на Mac/Linux нельзя считать готовым:** собственного разрешённого
Client ID пока нет. Зарегистрировать public desktop application и доступ
Minecraft Services должен владелец после своего просмотра. Пользователям
лаунчера регистрировать Azure application не потребуется; Windows flow сохранён.

На физической Ubuntu (включая предложенный владельцем dual boot) и Mac проверить
native window/DPI/file dialogs/Quick CSS, Local account, Modrinth, Java install,
Vanilla/Fabric/Forge/NeoForge install+launch, cancellation/repair, persistence.
После регистрации ID — Microsoft login/MFA/cancel/multi-account/silent refresh,
skin upload и системное хранилище секретов. На Mac отдельно проверить обычную
доставку `.app`, подпись/нотаризацию и Gatekeeper без отключения защиты.
Подробный checklist и воспроизводимые команды: [PLATFORMS.md](PLATFORMS.md).

`Assets/very important asset/` не изменялась; её содержимое не исследовалось.

---

# Подготовка заявки CurseForge и удаление слоганов — 20 октября 2026

Это **локальная подготовка к просмотру владельцем**, без отправки заявки,
заполнения внешней формы, принятия API terms, регистрации ключа или публикации.
CurseForge provider не добавлен. Существующие Microsoft/Local accounts сохранены.

Сделано:

- Удалены все подписи с присланных скриншотов: footer, главная, настройки,
  NexDoctor, NexStats, библиотека скинов и Modrinth Modpacks. Главная имеет
  заголовок «Играть», пустые состояния — нейтральные названия. Ошибки и
  предупреждения о совместимости/подтверждении действий сохранены.
- Создан [английский черновик заявки](CURSEFORGE_APPLICATION.md). В нём раскрыты
  возможные добровольные донаты по внешней ссылке, выбранная площадка пока не
  определена. Поддержка разработчика отделена от поддержки авторов модов.
- В «Настройки → О программе» добавлены сведения о лицензии, source/issue links
  по явному клику и полный локальный [PRIVACY.txt](../PRIVACY.txt). Текст встроен
  в EXE и отдельно включён в Installer/Portable; интернета для чтения не требуется.
- Экспорт с bundled-файлами требует подтверждения проверки прав получателя.
  Подтверждение сбрасывается при изменении параметров, instance или preview.
  Рецепт без копий файлов не требует такого флажка. Это не license detector.
  Управляемые Modrinth JAR по-прежнему не копируются в package.
- Документированы вопросы для письменного согласования: применение правила о
  конкурирующих продуктах, desktop/open-source key deployment, persistent metadata,
  cache, лимиты и возможные донаты. Не заявляется, что эти вопросы уже решены.
- Контакты, реальное имя, окончательные ссылки, права на распространяемые материалы
  и acceptance-пункты оставлены владельцу; личные данные не выдумывались.

Validation:

- Исходный проверенный baseline предыдущего этапа: **1020 checks**.
- Focused application/transfer run: **116 checks**.
- Окончательный полный suite: **1046 checks**, включая 26 новых проверок.
  Проверены реальные UI-тексты, локальный privacy notice, безопасные mapped links,
  отсутствие запроса к неактивному CF host, rights confirmation и сохранение
  прежних import/export transactions. Tests не нажимают внешние links и не
  отправляют форму/данные реального аккаунта.
- Debug / Release / Windows publish с `-warnaserror`: **0 warnings, 0 errors**.
- Full build logs: `.artifacts/release-builds/0.1.1-alpha-8220405206854644b1286bc0947a8dfc/logs/`.
- Headless screenshots: `.artifacts/checks/74c8b297bfe641c785aa9f65caa9bdf6/`.
- Native Portable startup: responsive window, bundled runtime 10.0.12, Skia,
  WebView2 loader, очищенное development environment, exit **0**. Evidence:
  `.artifacts/release-smoke/Windows Релиз 799293e34b094ced812b3830697f1b7a/smoke-result.json`.
  Это текущая Windows с существующим user profile, **не clean VM**.
- Portable содержит только EXE, README, LICENSE, PRIVACY, notices и dependencies
  manifest. Иконки в EXE и Setup проверены. Пакеты self-contained и unsigned.
- `git diff --check` проходит; generated files остаются в ignored `.artifacts`.
  `Assets/very important asset/` не изменялась. Commit/tag/push/release не создавались.

Пакеты для ручного просмотра в `.artifacts/releases/curseforge-application-preview/`:

| Artifact | Bytes | SHA256 |
| --- | ---: | --- |
| `NexLauncher-Setup-v0.1.1-alpha.exe` | 47070160 | `632edc93affe8738d19ac32c4be3411943864e0779c1194b89d31ca726a62353` |
| `NexLauncher-v0.1.1-alpha-win-x64.zip` | 62053931 | `00e2eeb5b0c1b556f140d22ea80cb1c91764adefd4faef20c15641ca0d2c4354` |

Вручную проверить: все отмеченные страницы без подписей, resize/DPI, чтение privacy
без интернета, export с файлами/без файлов и сброс подтверждения, существующие
игровые сценарии и установку на чистой Windows по [RELEASE.md](RELEASE.md).
Minecraft автоматически не запускался. Перед подачей владелец должен полностью
просмотреть черновик и подтвердить факты; право на доступ к API определяет Overwolf.

---

# Общие параметры оформления — 20 октября 2026

Продолжена существующая реализация без замены launcher/auth/instance pipeline.
Добавлены **Настройки → Оформление**, 12 общих `--nx-*` параметров, пресеты Мята /
Сумерки / Океан, preview цвета, ползунки, применение, перечитывание полей и сброс.
Сохранённые параметры действуют и при отключённом CSS. Включённый файл добавляет
свои токены и точные правила поверх них. Старые settings без Appearance сохраняют
прежние стили до явного сохранения новых параметров.

Переиспользованы parser, typed style applier, watcher и revision-checked atomic
editor writes. Общий root-token writer используется и для существующего HomeLayout.
Настройки не теряют новый ввод при сохранении; открытый CSS-черновик объединяется без
автосохранения других правок. Конфликт внешнего редактора не перезаписывается.
Встроенный пример переведён на общие токены; точные правила старых пользовательских
тем сохраняют приоритет. Исправлены disabled-цвета primary buttons при смене акцента
и одинаковое применение явно сохранённых default values с CSS и без CSS.

Основные файлы: `Services/QuickCss/QuickCssAppearance.cs`, `QuickCssRootTokens.cs`,
`ViewModels/AppearanceViewModel.cs`, `Views/AppearanceView.*`, `QuickCssSettings.cs`,
`ConfigurationStore.cs`, существующие QuickCss parser/service/applier/editor,
`MainWindow.*`, `Assets/quickcss.example.css`, `AppearanceChecks.cs`.
Токены и приоритет: [QUICK_CSS.md](QUICK_CSS.md#общие-параметры-оформления).

Фактически выполнено:

- Baseline: **960 checks** до новых изменений.
- Окончательный Debug и Release с `-warnaserror`: **0 warnings, 0 errors**.
- Полный suite: **1020 checks**, включая **60 новых appearance checks**.
  Настоящий Settings UI, typed styles, 1060/1920 layout, пресеты, persistence,
  absent legacy Appearance, invalid values/paths, двусторонняя синхронизация,
  реальный watcher, IO failure, черновики, внешние конфликты, сброс и disabled states.
- Headless screenshots: `.artifacts/checks/c896901323a6442280de361ebc3e33d5/`.
- Production build logs: `.artifacts/release-builds/0.1.1-alpha-41a4f47417204d32b15505ed438d9416/logs/`.
- Native Portable startup: responsive main window, bundled .NET 10.0.12 / Skia /
  WebView2 loader, очищенный development environment, exit **0**.
  Evidence: `.artifacts/release-smoke/Windows Релиз a904182eef374673b4fbd4487112541b/smoke-result.json`.
  Это **не clean Windows VM**; использован существующий пользовательский профиль.
- Windows-проверка ICO в приложении и Setup прошла. Пакеты self-contained,
  unsigned; .NET SDK/Runtime пользователю не нужен. Сам Microsoft login использует
  системный WebView2, как и в предыдущем release workflow.
- `git diff --check` проходит. Generated outputs остаются в ignored `.artifacts`.
  `Assets/very important asset/` не изменялась. Commit/tag/push/release не создавались.

Окончательные пакеты (metadata пока `0.1.1-alpha`, preview следующей сборки):

| Artifact в `.artifacts/releases/appearance-preview/` | Bytes | SHA256 |
| --- | ---: | --- |
| `NexLauncher-Setup-v0.1.1-alpha.exe` | 47073887 | `e328763eafc441e28396ee81de8918d95401958be6dac730d185f712ecee8d26` |
| `NexLauncher-v0.1.1-alpha-win-x64.zip` | 62038656 | `2a531b47e9715a071953851a6bfd4ef7cfe6e2a233d6ad88f5c70ec16312ce54` |

CurseForge: исследован официальный REST API и условия стороннего доступа.
Без одобренного для NexLauncher доступа и согласованных условий интеграция не
подключалась; чужие ключи/обходы не использованы. [Источники](CURSEFORGE.md).

Ручная проверка:

1. Применить пресет, изменить цвет/радиус/размер текста, проверить разные страницы
   и restart persistence в Installer/Portable.
2. Включить/отключить CSS, изменить `--nx-*` в редакторе с AutoReload; проверить
   обратное обновление полей и отдельный `--home-layout`.
3. С несохранённым CSS-черновиком изменить настройку, затем сохранить черновик;
   проверить сохранность своих правок. У старых тем проверить явные CSS overrides.
4. Проверить resize и Windows DPI 125/150/200%; игровые сценарии, Microsoft login,
   clean Windows, install/uninstall/upgrade — по [RELEASE.md](RELEASE.md).
   Minecraft автоматически не запускался; реальные аккаунты/игровые запуски
   остаются ручной проверкой.

---

# Главный экран, Quick CSS и иконка — 19 октября 2026

В прежней сборке `--home-layout` был обычной неиспользуемой переменной: корректный
синтаксис не менял экран. Теперь это реально подключённый токен `default`/`tiles`/`list`.
На первом пользовательском скриншоте дополнительно отсутствовала `;` после `tiles`;
parser теперь показывает точную строку пропущенного разделителя и сохраняет соседние
объявления, вместо вводящего в заблуждение сообщения ниже по файлу.

- **Настройки → Главный экран** сохраняет режим сразу и обновляет token в CSS.
- Обратное применение CSS обновляет экран и выбранный пункт настроек.
- Действуют existing Save/Apply, reload и debounce watcher; отключение CSS возвращает
  сохранённый вариант. Сбой чтения сохраняет предыдущий активный layout.
- Файл обновляется атомарно с revision check. Открытый черновик объединяется без
  автосохранения других правок; ввод во время IO сохраняется. Parsing выполняется
  вне UI thread. Внешнее изменение файла вызывает конфликт без перезаписи.
- Плитки/список используют existing selection, install/launch commands, RAM/Java
  и account pipeline. Использован предоставленный пользователем Minecraft default
  image. Стандартный вид сохранён, у новых видов есть empty state и действия сверху.
- `.home-tile` и `#home-instances` документированы. Проверка выявила приоритет
  локальных default styles над Quick CSS; defaults перенесены в application theme.
- Общая иконка «N» встроена в EXE, все Avalonia Windows и Inno Setup. ICO содержит
  семь размеров. `Generate-AppIcon.ps1` воспроизводит asset; `Test-AppIcons.ps1`
  сравнивает извлечённые Windows иконки EXE/Setup с source ICO при каждой упаковке.
- `HomeLayout` — additive setting с default `Default`; старые настройки/instances
  загружаются без пересоздания. Auth vault и formats аккаунтов не менялись.

Полный Debug/Release: **0 warnings / 0 errors**. Suite: **960 checks**, включая
47 новых Home/CSS/icon checks и Windows import regression. Последний release-run:
`.artifacts/release-builds/0.1.1-alpha-a45d2496b9994c0bb5e261c3cd954d3a/`.
Пакеты: `.artifacts/releases/home-layout-icon-preview/`; точные размеры и SHA-256
находятся рядом в `release-manifest.json`/`SHA256SUMS.txt`.
Portable ZIP: **62 031 269 bytes**; Installer: **47 056 489 bytes**. Оба unsigned.
Готовый EXE прошёл native startup smoke: responsive window, bundled .NET 10.0.12,
Skia, exit 0, очищенная development environment, текущий Windows profile.
Evidence: `.artifacts/release-smoke/Windows Релиз f40a031b46fa4469b3e8638b6ae3753f/`.

Повторный полный прогон воспроизвёл прежний отказ Windows на staging rename Import.
Добавлены ограниченные повторы только Win32 5/32/33, максимум 600 мс, с cancellation
и повторной проверкой links. Постоянный отказ/коллизия не игнорируется; rollback
сохраняет прежние гарантии. Regression использует настоящий временно удерживаемый
directory handle, без запуска Minecraft. Существующие проверки не отключались.

Вручную: запустить **новый** пакет, переключить три режима, изменить/сохранить token
в редакторе и проверить обратное переключение; проверить restart, свой CSS, выбранную
сборку, длинное имя, scaling/DPI, taskbar/Start Menu/installer icons. Minecraft не
запускался; clean Windows VM и полный install/uninstall остаются manual checklist.

---

# Modrinth: описание и совместимость — 19 октября 2026

Изменения выполнены поверх существующего рабочего дерева. Baseline: **870 checks**.
Исправлен общий renderer описаний Mods/Modpacks: смешанные Markdown/HTML descriptions
теперь превращаются в безопасную модель native-текста. HTML-комментарии, scripts,
iframe и обработчики событий не отображаются/не исполняются. Вложенные badge-ссылки
сохраняют подпись и внешнюю целевую ссылку. Заголовки, цитаты, разделители и код
оформлены отдельно; длина строки ограничена внутри полноширинной страницы.
Внешние изображения по-прежнему открываются только по явной ссылке.

Вместо стены Minecraft versions — до шести меток, точное общее число и кнопка
**Все версии (N)**. Полный список разделён на релизы и тестовые/прочие значения,
ограничен по высоте и прокручивается. Непрерывные диапазоны не выдумываются.
Version selection, exact version ID, compatibility/dependency/install logic сохранены.
Новые зависимости: Markdig 1.3.2 и AngleSharp 1.8.5; upstream licenses включены
в `packaging/licenses/` и generated distribution notices.

| Проверка | Результат |
| --- | --- |
| Debug / Release (`-warnaserror`) | Успех, 0 warnings / 0 errors |
| Полный offline regression suite | **912 checks**, включая 42 дополнительных проверки |
| Полный suite + `--browser-install-live` | **946 checks** |
| Live Modrinth | Fabulously Optimized и Sodium: описание, версии, точный выбор ID, Back, wide/narrow UI |
| Live individual mod install | Sodium `26nVNc41`, isolated instance, SHA-512 совпал, manual JAR сохранён |
| Production startup | Responsive native window, self-contained .NET 10.0.12/Skia, exit 0 |
| Git whitespace / protected assets | `git diff --check` прошёл, protected directory не изменена |

Один packaging-прогон остановился на `IOException: Access denied` при перемещении
тестовой staging-папки в существующем Import check. Проверка потоков не выявила
незакрытого handle; повторный полный прогон и последующий live-прогон прошли без
изменения/отключения Import checks. Причина единичного отказа Windows не установлена.
Первоначальное ограничение сети для NuGet restore/audit устранено повторным restore
через официальный NuGet; audit и warnings-as-errors не отключались.

Готовые unsigned пакеты текущего working tree:

- `.artifacts/releases/modrinth-readable-preview/NexLauncher-v0.1.1-alpha-win-x64.zip` — **61 665 958 bytes**.
- `.artifacts/releases/modrinth-readable-preview/NexLauncher-Setup-v0.1.1-alpha.exe` — **46 766 334 bytes**.
- Общий single-file payload `NexLauncher.exe` — **151 217 550 bytes**.
- SHA-256 и runtime metadata — `release-manifest.json` и `SHA256SUMS.txt` рядом.

Build logs: `.artifacts/release-builds/0.1.1-alpha-3dd563d5568c4ff482ec5011fbee6787/logs/`.
Финальный live log: `.artifacts/modrinth-readable/live-final.log`.
Native startup evidence: `.artifacts/release-smoke/Windows Релиз c67eed8e1fd04520a22c0fd3f49fe544/`.
Финальные UI screenshots: `.artifacts/checks/2710204ec5f940d4ac3ce20223868281/browser-live/`.

Minecraft не запускался. Headless UI проверен на fixtures и реальных API descriptions,
production EXE — в текущем Windows profile с очищенной development environment,
**не в clean Windows VM**. Вручную: открыть Sodium/другой mod и modpack, проверить
ссылки, длинное описание, раскрытие/scroll списка версий, resize и системный DPI;
выполнить игровой запуск и clean-machine/installer checklist из [RELEASE.md](RELEASE.md).

---

# NexDoctor / NexStats — 10 октября 2026

Работа продолжена поверх незакоммиченного polish от 9 октября; его исправления
сохранены. Baseline этой задачи — **752 checks**. Отдельных готовых Instance Health
и Crash Assistant в рабочем дереве не было, поэтому добавлен один общий diagnostic
model/engine, а не два конкурирующих механизма. Новых NuGet dependencies нет.

## Реализованный scope

- Общий lifecycle в существующем MinecraftService: accepted attempt, реальный
  process start/exit, один session ID, финализация и связанный Doctor report.
- Локальные health checks из managed Modrinth metadata/hashes и ограниченных
  JAR descriptors; независимые crash signatures Fabric/Forge/NeoForge/Java/Mixin/
  memory/graphics с severity/confidence/evidence и unknown fallback.
- Safe fixes: стандартный repair игры, восстановление отсутствующего managed JAR
  с exact version/hash и без перезаписи, переключение на automatic Java.
  Отсутствие dependency в managed metadata не доказывает её отсутствие среди
  manual mods; автоматического удаления duplicates/неизвестных JAR нет.
- NexStats: реальные часы/запуски/известные outcomes, фильтры, native charts,
  Recap, JSON/CSV summary export и PNG, opt-in RAM/CPU с ограничением 360 samples.
- Atomic schema-1 records, bounded history, lazy samples, PID + OS start-time
  recovery, ID navigation Doctor ↔ Stats, подтверждённая очистка в Settings.
- Полноширинные native страницы и документированные Quick CSS selectors.

Финальный review исправил unreadable drive baseline, повреждённый/неверно типизированный
Minecraft/Fabric JSON, защиту identity сессии, ложный выбор другого report при истёкшем ID,
разделение копирования лога и evidence, точное замораживание duration до checkpoint IO
и CSV formula neutralization после ведущих пробелов. Неизвестные/повреждённые originals
сохраняются; optional telemetry failure не должен ломать игровой launch.

## Validation этого прохода

| Проверка | Результат |
| --- | --- |
| Clean Debug / Release (`-warnaserror`) | Успех, **0 warnings / 0 errors** |
| Полный offline automated suite | **870/870**, на 118 checks больше baseline |
| Schema/settings compatibility, recovery, cancellation, retention, exports | Fixture checks прошли |
| Avalonia.Headless/Skia UI | Проверены 1920×1080, 1060×760 и 900×650, long name, scrolling, crosslinks, Quick CSS |
| Self-contained win-x64 publish / Inno compilation | Успех |
| Финальный Portable native startup без SDK/runtime lookup | Responsive NexLauncher, bundled .NET 10.0.12/Skia/WebView2Loader, exit **0** |
| `git diff --check` / protected asset | Чисто / изменений нет |

Production preview, metadata версии **0.1.1-alpha сохранена**, в
`.artifacts/releases/nexdoctor-stats-preview/`:

| Artifact | Bytes |
| --- | ---: |
| `NexLauncher-v0.1.1-alpha-win-x64.zip` | 61 070 778 |
| `NexLauncher-Setup-v0.1.1-alpha.exe` | 46 343 279 |

Пакеты unsigned. ZIP содержит один self-contained single-file NexLauncher.exe
и четыре документа; user history/accounts/settings/skins/logs/PDB в payload нет.
SHA256 указан в `release-manifest.json` / `SHA256SUMS.txt`.

Evidence:
`.artifacts/release-builds/0.1.1-alpha-fc5539e3e6ba4642914a7fd804f8f9e3/logs/`,
`.artifacts/doctor-stats/{production-build,portable-smoke}.log`,
`.artifacts/release-smoke/Windows Релиз 4623aa2a08f747faaa1148dbfb881af2/smoke-result.json`,
`.artifacts/checks/ec2cf2d7e8b04d46ae5d76761641995e/insights-tests/`.

**Minecraft не запускался**, как запрошено. Lifecycle fixtures используют текущий
test process; это не доказательство реального игрового exit/recovery/driver behavior.
Native smoke использовал существующий Windows profile, не clean VM. Microsoft login,
настоящие crashes/gameplay всех четырёх loaders, installer upgrade/uninstall с новой
историей, DPI/input и clean Windows требуют ручной проверки. Точный checklist и
ограничения — в [DOCTOR_STATS.md](DOCTOR_STATS.md) и [RELEASE.md](RELEASE.md).

FPS/frametime не реализованы; внешняя ETW-интеграция возможна, но не подключена.
Если игра завершилась при закрытом лаунчере, сохраняется только наблюдённое время
с Interrupted/unknown exit. 10 000 sessions / 512 reports / 360 samples — явные лимиты.
Диагностика не является полным solver всех loader-specific constraints.
`Assets/very important asset/` не затронута. Commit/tag/push/release не создавались.

Ниже сохранены исторические результаты предыдущих проходов с их отдельным scope.

# Working tree polish — 9 октября 2026

Исходный `8e0c5bd` был чистым; baseline: **739 checks**, Debug/Release без warnings.
Рабочие подсистемы и version metadata не переписаны. Commit/tag/push не выполнялись.

Исправлены шесть пограничных проблем:

- Отмена установки/import больше не меняет статус авторизации аккаунта.
- Большие сильно сжимаемые bundled-файлы экспортируются без ZIP-сжатия, поэтому
  package не отвергается собственной защитой 200:1. Hash/size и все лимиты сохранены.
- ZIP-префикс `files/` не уменьшает допустимые 240 символов instance-relative path.
- Пустой выбор Classic/Slim не вызывает NullReferenceException и отключает Apply.
- Legacy 64×32 предлагает только Classic; неподдерживаемый Slim не применяется.
- Quick CSS открывает именно выбранный файл; переключение не теряет несохранённый
  черновик другого файла и сопровождается понятным сообщением.

## Validation текущего working tree

| Проверка | Результат |
| --- | --- |
| Debug build / Release build (`-warnaserror`) | Успех, 0 warnings / 0 errors |
| Полный automated suite | **752/752** — 13 новых regression checks |
| Live API/browser checks | **39/39** |
| Повторные loader install/repair + process construction checks | **9/9** |
| Итог opt-in suite | **800/800** |
| Self-contained publish / Inno compilation | Успех |
| Portable native startup | Responsive NexLauncher window, bundled .NET 10.0.12/Skia/WebView2Loader, exit 0 |

Live checks используют официальный Mojang catalogue и Fabric/Forge/NeoForge
metadata. Modrinth проверен на Fabulously Optimized и Sodium: Mods/Modpacks,
full details, конкретные Minecraft/loader metadata, dropdown при широком/узком
окне, Back и установка выбранного Sodium version ID `26nVNc41` в отдельный
test instance. SHA512 совпал, контрольный unknown/manual JAR сохранился.

Minecraft 1.21.1: повторная проверка/repair **Fabric 0.16.10**, **Forge 52.0.28**,
**NeoForge 21.1.172** использовала уже скачанные изолированные instances.
Официальные installers успешно завершились; CmlLib разрешил конкретные loader
profiles, offline session и Mojang Java 21. В этом проходе процесс самой игры
не запускался: process construction не является доказательством входа в мир.

Preview packages в `.artifacts/releases/polish-preview/`:

| Artifact | Bytes |
| --- | ---: |
| `NexLauncher-v0.1.1-alpha-win-x64.zip` | 60 987 571 |
| `NexLauncher-Setup-v0.1.1-alpha.exe` | 46 280 148 |

Пакеты unsigned; Runtime/SDK пользователю не нужен. SHA256 и размеры находятся
в `release-manifest.json` / `SHA256SUMS.txt`. ZIP имеет пять allowlisted файлов,
без auth/settings, test instances, user skins, logs, downloads или PDB.

Evidence: `.artifacts/polish/{baseline-checks,final-live-checks,production-build,portable-smoke}.log`,
`.artifacts/release-builds/0.1.1-alpha-866694b26812410b87d92ff5bdb1cb0f/logs/`,
`.artifacts/release-smoke/Windows Релиз e1497507f05649aa90ca4ae43fe58ef3/smoke-result.json`.
Headless UI frames: `.artifacts/checks/1e8de67074044be4928a1ccb8665c812/`.

Native smoke использовал существующий Windows profile, **не clean VM**.
Реальный Microsoft OAuth/skin change, game worlds/multiplayer/modpack launch,
native mouse/DPI и install/reinstall/uninstall новых packages в этом проходе
не выполнялись. Перед публикацией остаются checklists в [RELEASE.md](RELEASE.md).
Дополнительно вручную проверь переключение двух CSS-файлов с черновиком,
legacy skin, отмену установки без изменения аккаунта и export/import своих configs.

Ниже сохранены результаты предыдущих проходов; их даты и scope не относятся
к текущей проверке.

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
