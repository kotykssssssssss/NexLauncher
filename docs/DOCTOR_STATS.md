# NexDoctor и NexStats

Это локальные функции NexLauncher. Они не требуют Microsoft-входа, не устанавливают
моды/Java agents и не отправляют игровые логи в облако. История начинается с запусков
через эту версию лаунчера: прежние игровые часы не восстанавливаются задним числом.

## NexDoctor

Открой **NexDoctor**, выбери instance и нажми **Scan Now**. Overview показывает
ошибки, предупреждения и дату выбранного отчёта. Healthy означает только отсутствие
известных обнаруживаемых проблем, а не гарантию успешного запуска.

Проверяются:

- наличие папки, установленного профиля/marker и важных файлов игры/loader через
  существующий `MinecraftService.IsInstalled`;
- незавершённая транзакция модов и корректность `.nexlauncher/mods.json`;
- Minecraft version, loader и hashes управляемых Modrinth-файлов;
- required/incompatible dependencies **конкретных сохранённых versions**;
- структура JAR/ZIP и читаемость ограниченных descriptors;
- повторяющиеся literal mod ID из `fabric.mod.json`, `META-INF/mods.toml` и
  `META-INF/neoforge.mods.toml` (`[[mods]]`, не dependencies);
- вероятное несоответствие ручного Fabric JAR выбранному Forge/NeoForge;
- доступность выбранной вручную Java, её `release` metadata и минимальный Java major
  из локального Minecraft version JSON. Произвольные executable для проверки не запускаются.

Отсутствие зависимости в managed metadata — **Warning / Possible**, поскольку
ручной JAR может удовлетворять требование. Fabric API не считается универсальной
зависимостью: учитываются только реальные dependency metadata/сообщения загрузчика.
Одинаковое имя файла само по себе не доказывает duplicate mod.

После ненулевого exit code анализируются соответствующие `latest.log` и свежий
crash report. При exit 0 свежий файл с заголовком Minecraft Crash Report тоже
считается признаком crash. Старые reports до начала текущего процесса исключаются.
Неизвестный exit после потери процесса не превращается в подтверждённый crash.

Независимые правила распознают ошибки Fabric/Forge/NeoForge, dependency resolution,
Java bytecode version, Mixin, initialization/entrypoint, missing class, ZipException,
OutOfMemoryError и распространённые GLFW/OpenGL initialization failures.
Обычная строка stack trace не назначает виновный mod. Информационное упоминание
dependency не является доказательством её отсутствия.

### Severity, confidence и доказательства

- **Error / Warning / Info** — тяжесть обнаруженного состояния.
- **Confirmed** — конкретный факт: hash mismatch, отсутствующий файл, metadata
  mismatch, известное сообщение OOM. Это не обещание единственной первопричины.
- **Likely** — узнаваемая ошибка загрузчика/Java/графики.
- **Possible** — Mixin/class failure, возможная dependency или loader проблема,
  когда контекста недостаточно.
- **Unknown** — данных недостаточно. Лаунчер сообщает об этом прямо.

Выбери issue, раскрой **Технические доказательства** и прочитай рекомендацию.
**Crash History** содержит связанные с сессиями crash/preparation reports.
Кнопка **Сессия в NexStats** открывает соответствующий session ID.

Logs показывает ограниченный сохранённый фрагмент (до 120 KiB каждого источника).
Поиск и фильтр ERROR/WARN/INFO работают по тексту строк, не являются полноценным
парсером всех форматов logging. **Открыть исходный лог** открывает полный локальный
файл, если он ещё существует. Логи и доказательства очищаются от известных token
patterns; перед ручной публикацией проверяй имена, пути и адреса серверов самостоятельно.

### Безопасные исправления

**Fix Safe Issues** выполняет только:

1. штатную установку/repair игры и loader через существующий CmlLib pipeline;
2. восстановление **отсутствующего** совместимого managed JAR по точному version ID,
   сохранённому filename и hash; temporary download → hash/JAR validation → move
   без overwrite. Metadata, manual mods и существующие replacements сохраняются;
3. сброс недоступной/слишком старой ручной Java на штатный автоматический выбор.

Изменённый или повреждённый существующий JAR не перезаписывается автоматически:
это может быть намеренная ручная замена. Missing dependency без однозначного
подтверждения требует проверки в разделе Моды. Duplicates не удаляются: сначала
проверь mod IDs/версии и сохрани backup. Автоматического лечения любых конфликтов,
подбора драйверов или установки случайного Fabric API нет.

Полный repair всех assets/libraries остаётся частью обычной установки/запуска.
Health scan не перепроверяет каждый byte ручного JAR и не интерпретирует произвольный
TOML/все version constraints сторонних модов. Нестандартные descriptors могут быть
пропущены. Для RAM/Java/mod incompatibility первопричину нельзя всегда доказать извне.

## NexStats

Session ID создаётся при принятом запуске Minecraft backend. Подготовка/repair,
открытие лаунчера, самостоятельная установка и OAuth не считаются игровым временем.
Таймер начинается только после успешного `Process.Start` и заканчивается при exit,
до дочитывания stdout/stderr. Повторная запись или finalization одного ID не создаёт
вторую сессию. Instance ID стабилен: переименование не сбрасывает часы.

Сохраняются Minecraft version/loader, attempt/start/end timestamps, наблюдаемая
длительность, exit code, outcome, связь с Doctor report и доступные process samples.
Аккаунт, session/access tokens и launch arguments не сохраняются.

Outcomes: Running, Completed, Crashed, FailedToStart, Cancelled, Interrupted.
FailedToStart имеет нулевое игровое время. Successful Launches означает, что процесс
действительно стартовал, не обещает достижения главного меню Minecraft. Crash Rate
равен Crashed / (Completed + Crashed); неизвестные результаты не входят в знаменатель.
Без известных результатов показывается «—».

При перезапуске проверяются PID **и OS process start time**. Живой совпадающий процесс
наблюдается повторно. Если процесса уже нет или PID был переиспользован, сохраняется
только последняя наблюдаемая длительность, outcome Interrupted и неизвестный exit.
Когда лаунчер закрыт и игра тоже завершилась до его повторного открытия, точное время
выхода восстановить нельзя. Checkpoint записывается примерно раз в 30 секунд;
неподтверждённый хвост не добавляется к часам.

### Dashboard, фильтры и Recap

- total playtime, launches, started/failed launches, average/longest session,
  last played, crash count/rate, most played instance;
- playtime по Minecraft versions и loaders;
- native Avalonia графики playtime Daily/Weekly/Monthly, instance distribution,
  session duration, crash history и RAM выбранной сессии. Наведение показывает
  точное значение. Отрисовывается до 120 последних точек, список сессий — до 500
  последних; агрегаты и экспорт используют весь выбранный период;
- All/Selected Instance, Last 7/30/90 Days, All Time, Custom (yyyy-MM-dd;
  последняя дата включительно);
- Recap: This Month / This Year / All Time, часы, любимая версия/сборка,
  longest session, busiest day, crashes, максимальная серия **завершённых сессий**
  без crash. Unknown/interrupted прерывают подтверждённую серию.

Все соответствующие графики и суммы используют выбранные фильтры. Сессия,
пересекающая границу периода, даёт только часы внутри периода. Calendar buckets
используют локальные даты. Пересчёт выполняется при изменении фильтров/истории,
не на каждом frame. После crash из NexStats можно открыть точный Doctor report.
При пустой истории показывается empty state, а не выдуманная активность.

### Performance monitoring

В **Настройки → NexStats / Privacy** включи sampling и сохрани настройки.
По умолчанию он выключен. Раз в 5 секунд читаются process working set, peak working
set и delta CPU time. Не больше 360 graph samples на сессию: старые точки уплотняются,
сводные avg/count учитывают все наблюдения. Sampling не исполняет код внутри JVM.

RAM — физический working set процесса, включая shared pages; это не Java heap и
не заданный `-Xmx`. CPU — процент суммарной мощности логических процессоров,
не FPS. [Определение WorkingSet64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64?view=net-10.0),
[TotalProcessorTime](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.totalprocessortime?view=net-10.0).

FPS/frametime не собираются: стандартные внешние process counters не содержат
кадров Minecraft. DLL injection, Java agents, скрытые mods или изменения клиента
не добавлены. UI явно сообщает `FPS tracking unavailable without client integration`.
Если samples нет, RAM/CPU показываются как недоступные, а не как выдуманный ноль.
Это ограничение текущего коллектора, а не утверждение о принципиальной невозможности
внешнего измерения: [PresentMon](https://github.com/GameTechDev/PresentMon) использует
Windows ETW, включая OpenGL. Ему нужны дополнительные права для tracing, а часть
OpenGL timing менее точна. Такой отдельный native backend в NexStats не подключён;
CPU utilization не подменяет FPS. Сообщение в UI относится к встроенному коллектору.

### Экспорт и удаление

**Export JSON / CSV** сохраняет отфильтрованные сессии через стандартный Save dialog.
В экспорт не входят game directory, Java path, PID, account ID, токены и логи.
JSON/CSV содержат summary metrics сессий; сырые RAM/CPU samples остаются в локальной истории.
CSV экранирует значения и нейтрализует spreadsheet formula prefixes.
**Export Recap PNG** рисует карточку из текущей реальной сводки через уже имеющийся
SkiaSharp; screenshot/ручное выделение экрана не нужны.

В настройках нужно отдельно подтвердить удаление истории и нажать **Удалить историю**.
Во время активной игры/операции действие недоступно. Сборки, миры, mods, settings,
accounts и skins не удаляются.

## Хранение, migration и ограничения

`%LOCALAPPDATA%\NexLauncher\insights\sessions\<session-id>.json` и
`insights\reports\<diagnostic-id>.json`: versioned schema 1, atomic temporary writes,
ID/instance индексы в памяти и файловые операции вне UI thread. SQLite/новых NuGet
пакетов нет. Старые settings без MonitorPerformance загружаются с выключенным sampling.
Формат instances/accounts не изменён. Ограниченный schema 0 session reader сохраняет
отсутствующие performance fields как недоступные.

Лимит — 10 000 сессий (затем экспорт/явная очистка), 512 Doctor reports; при переполнении
reports сначала удаляются старые manual scans, затем самые старые crash reports.
Историческая сессия остаётся, но её старый report может уже быть недоступен.
Повреждённые/неизвестные оригиналы сохраняются, остальные корректные записи читаются.
Ошибки хранения/мониторинга/диагностики не должны мешать запуску игры.

Логи не отправляются сторонним сервисам. Repair по кнопке может скачивать официальный
Minecraft/loader/Modrinth файл обычным инфраструктурным HTTP layer; содержимое лога
в эти запросы не включается. User data не входит в Installer/Portable.

## Ручная проверка

1. Запусти любую сборку, закрой Minecraft нормально: одна Completed session, корректные
   часы, без crash notification. Повтори для Vanilla/Fabric/Forge/NeoForge.
2. Включи sampling, сыграй несколько минут: RAM graph/peak/CPU; затем отключи и проверь
   новую сессию без metrics. FPS не должен появляться.
3. Закрой только NexLauncher во время игры, открой снова: reattach к тому же процессу.
   Отдельно закрой игру при закрытом лаунчере: Interrupted с консервативным временем.
4. Проверь Scan Now, cancellation, missing managed file restore, mismatch Java,
   duplicate warnings и сохранение manual JAR. Исправления не должны запускать игру.
5. Для известного crash fixture/реального crash проверь report/evidence, notification,
   исходный лог, поиск и переходы Doctor ↔ Stats по ID.
6. Проверь instance rename, restart persistence, все date/instance filters, Recap,
   JSON/CSV/PNG, подтверждение очистки и отсутствие удаления других данных.
7. Проверь 900×650 / обычное / maximized окно, scrolling, Windows DPI и Quick CSS
   (`#nexdoctor`, `#nexstats`, `.diagnostic-issue`, `.stats-chart`).

Автоматические fixtures не запускают Minecraft и не меняют реальные аккаунты.
Настоящие игровые завершения, драйверы, clean Windows и DPI требуют ручной проверки.
