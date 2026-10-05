# Export / Import Instance

Это первый завершённый этап менеджера сборок: локальный Export/Import. Update Center,
pin/channel/rollback UI, полноценный Instance Health и Crash Assistant **в этом этапе
не реализованы**. Существующие обновление отдельных Modrinth mods, repair и журнал
продолжают работать. Проверки целостности переноса не являются гарантией отсутствия crash.

## Использование

**Сборки → выбрать instance → Export / Share выбранную…**

1. Выбери, что добавить: local/unknown mods, configs, resource packs, shader packs.
   Все четыре переключателя по умолчанию выключены.
2. Нажми **Подготовить / обновить preview**. Сводка показывает Minecraft/loader,
   сохранённую RAM, managed/local mods, включённые и исключённые файлы и размер
   bundled-содержимого до сжатия. Размер загрузки managed JAR показан отдельно.
3. Проверь предупреждения и полный список файлов. Если изменяешь переключатели,
   нужен новый preview. Если содержимое или metadata изменятся во время export,
   операция не заменит прежний архив новым повреждённым файлом.
4. **Export / сохранить .nexpack…** открывает системный Save dialog с подтверждением
   перезаписи. Получается один файл, который можно передать другим пользователям.
   NexLauncher сам никуда его не загружает.

**Сборки → Import Instance… → выбрать .nexpack…**

1. Launcher полностью проверяет ZIP, manifest и hashes bundled-файлов; пока ничего
   не устанавливает и не выполняет файлы из package.
2. Preview показывает состав и исключённые автором файлы. Выбери новое имя, если оно
   совпадает с существующим. Имена сравниваются без учёта регистра.
3. **Import** создаёт новый instance в текущей **Папке сборок**. Сначала заново
   проверяются archive identity и exact Modrinth metadata, затем устанавливаются
   Minecraft и loader, скачиваются managed mods и копируются выбранные bundled files.
4. Instance появляется в общем списке только после успешной установки и проверки.
   Выбери **Играть** для запуска. Аккаунт запуска импорт не меняет.

Для долгих операций используется существующий progress/cancellation внизу окна.
При ошибке можно открыть журнал. Import недоступен во время запуска Minecraft,
другой установки или операции с аккаунтами.

## Содержимое

Managed Modrinth mods экспортируются **как ссылки**, без JAR:

- точные `projectId` / `versionId`, основной `filename`, размер, SHA512/SHA1;
- флаг `explicit`, отделяющий выбранный пользователем проект от dependency.

Import использует существующий официальный Modrinth API/service и HTTP layer.
Он сверяет ID, конкретную version metadata, client environment, Minecraft/loader,
основной filename/size/hashes и required/incompatible dependencies. URL из архива
не принимается: download URL берётся из проверенного API response, с существующим
HTTPS/host allowlist, User-Agent, timeout, bounded retries и rate-limit handling.
Никаких PAT, Microsoft tokens или аккаунтов для передачи рецепта не нужно.

Вместо latest устанавливается **именно указанная версия**. Если API удалил её,
metadata больше не совпадает или exact required dependency отсутствует в manifest,
import прекращается до установки. При проверке используется существующий
`DependencyResolver` с snapshot всех exact versions, без запросов к latest.
Optional dependencies не добавляются, embedded не скачиваются повторно.
Циклы и дубли обрабатываются существующим resolver; conflicts блокируют import.

Local/unknown файлы не сопоставляются с Modrinth по имени. При явном выборе можно
перенести корневые `mods/*.jar`, `config/`, `defaultconfigs/`, `resourcepacks/`,
`shaderpacks/`. Каждый bundled file получает SHA256 и размер в manifest.
Неизвестные JAR при import остаются **manual mods**, managed metadata для них
не выдумывается. Исходные пользовательские файлы не удаляются.

Resource/shader packs и моды из `.mrpack`, для которых текущая persisted metadata
не хранит надёжной project/version связи, относятся к local/unknown. Они переносятся
только при включении соответствующих переключателей. Исходные Modrinth modpack IDs
сохраняются как provenance, но исходный pack не переустанавливается поверх instance.

RAM переносится; абсолютный Java path автора **не переносится**, используется
существующая автоматическая Java. При необходимости укажи свою Java после import.
Новая личность instance генерируется локально; старые directories не перемещаются.

## Privacy и права на файлы

Это allowlist export, **не ZIP всей папки игры**. Никогда автоматически не копируются:
accounts/auth vault, launcher config, `.nexlauncher`, game libraries/assets/Java,
logs, crash reports, saves/worlds, screenshots, server lists, usercache и skins.
World export в этой версии отсутствует. `options.txt` не переносится.

Даже внутри выбранных папок отвергаются известные sensitive filenames/директории
и executable/script extensions. Имя вроде `SecretRooms.jar` не считается секретом.
**Нельзя надёжно распознать пароль в произвольном mod config по имени файла.**
Configs по умолчанию выключены; перед их явным включением просмотри содержимое и
удали личные значения. Launcher не обещает автоматическую полную redaction.

Managed JAR не распространяются внутри package, а заново скачиваются получателем
через API/CDN. Для local mods, resource packs и shaders самостоятельно проверь
разрешение автора/лицензию на передачу. Checkbox не выдаёт прав на чужие файлы.
Это локальный формат NexLauncher, не Modrinth `.mrpack` и не официальный продукт Modrinth.

Официальные источники, проверенные 5 октября 2026:
[Get a version](https://docs.modrinth.com/api/operations/getversion/),
[API overview](https://docs.modrinth.com/api/),
[Terms](https://modrinth.com/legal/terms).

## Format v1 reference

`.nexpack` — ZIP с одной записью `nexlauncher.instance.json` и ровно перечисленными
bundled entries под `files/<relative-path>`. Неперечисленные файлы и directory
entries отвергаются. Manifest — UTF-8 JSON, camelCase:

```json
{
  "manifestVersion": 1,
  "name": "Vanilla example",
  "minecraftVersion": "1.21.1",
  "loader": "Vanilla",
  "loaderVersion": "",
  "memoryMb": 4096,
  "launcherVersion": "0.1.1-alpha",
  "exportedUtc": "2026-10-05T10:00:00+00:00",
  "originalModpackProjectId": null,
  "originalModpackVersionId": null,
  "mods": [],
  "files": [],
  "omittedFiles": []
}
```

- `manifestVersion`: сейчас только `1`; будущая версия требует явной migration,
  неизвестная версия не трактуется как v1. Unknown/duplicate JSON properties отвергаются.
- `name`: 1–60 символов, без control characters; получатель может изменить его.
- `minecraftVersion`, `loaderVersion`: идентификаторы из текущей loader architecture.
  `loader`: `Vanilla`, `Fabric`, `Forge`, `NeoForge`. У Vanilla loaderVersion пустая.
- `memoryMb`: 1024–32768; Java/runtime/accounts/game path в схеме отсутствуют.
- `launcherVersion`, `exportedUtc`: версия экспортера и время export.
- `originalModpackProjectId` / `originalModpackVersionId`: обе null либо оба
  корректных Modrinth IDs; provenance, без команд установки pack.
- `mods[]`: `{projectId, versionId, filename, hashes: {sha512, sha1}, size, explicit}`.
  Допускается один из поддерживаемых hashes либо оба. Project IDs уникальны;
  версия/имя/hash/размер повторно сверяются через API. Максимум 256 проектов.
- `files[]`: `{path, size, sha256}`. Path относительно `game`, `/` как separator,
  без absolute paths; данные находятся в entry `files/<path>`.
- `omittedFiles[]`: относительные имена обнаруженных файлов, которые не включены;
  это предупреждение о неполноте, не инструкция создавать/скачивать их.

Новый manifest не изменяет версии `settings.json`, account vault или старых instances.
FormatVersion 1 Vanilla instances по-прежнему загружаются и могут экспортировать
рецепт установки даже без созданной game directory. В импортированном game появляется
`.nexlauncher/import.json`: версия схемы, hash package, export/import timestamps,
относительные omitted paths. Account data и исходный абсолютный путь отсутствуют.

## Transaction / security limits

- Package максимум 2 ГиБ, manifest 2 МиБ, bundled-файл/managed reference 512 МиБ,
  общий размер файлов и загрузок 4 ГиБ; максимум 8192 bundled entries/обнаруженных
  пользовательских files/directories и 256 managed projects.
- Для entries больше 1 МиБ compression ratio ограничен 200:1. Это консервативный
  предел: очень сжимаемый легитимный package тоже может быть отклонён.
- `..`, absolute paths, `\\`, ADS/drive syntax, Windows reserved names, trailing
  dots/spaces, case-insensitive duplicates и file/directory collisions отвергаются.
  Symlink/device/special archive entries, filesystem symlinks/junctions не поддерживаются.
- Preview читает весь package с bounded decompression и SHA256. Import повторяет
  проверку и сравнивает hash с preview; read handle не допускает concurrent writes.
- Сеть, parsing, compression, hashing и file operations работают вне UI thread.
- Import использует `<root>/.nex-stage-<guid>/<new-instance-id>/game`; пакет не может
  выбирать конечную папку. Bundled files создаются только через `CreateNew` и не
  перезаписывают installer output. Managed downloads используют temporary → hash
  verification → final staged file. Новые JAR не запускаются при анализе package.
- Устанавливаются только Minecraft/официальный loader через существующий pipeline.
  Forge/NeoForge могут требовать штатные official installer processors, как обычная
  установка; package scripts не запускаются.
- Только после installation marker, managed hashes, bundled hashes и безопасного
  дерева выполняются Directory.Move и atomic сохранение instance через
  ConfigurationStore. Cancellation после начала final registration не разрывает commit.
- При обычной ошибке/отмене staging удаляется; при отказе регистрации directory
  возвращается в staging и очищается. Не удаляются существующие пользовательские
  directories. При аварийном выключении до регистрации может остаться staging/orphan;
  он не считается зарегистрированной установленной сборкой. Если rollback самого
  filesystem невозможен, данные оставляются для ручного восстановления.

SHA256 не доказывает доверие к автору: злонамеренный автор может создать корректный
manifest с вредоносным mod. Импортируй только доверенные сборки; Minecraft исполняет
JAR при запуске. Health inspection локального кода здесь не заявляется.

## Manual checks

1. Export/Import Vanilla, Fabric, Forge и NeoForge; проверь loader version, RAM,
   автоматическую Java, запуск и сохранение после restart.
2. Добавь managed mod с required dependency и ручной JAR. Сравни exact version IDs
   и bytes после import; optional не должна скачиваться автоматически.
3. Проверь все переключатели, полный список, предупреждение при выключенных local
   mods; не передавай configs с личными данными.
4. Импортируй в другую папку с пробелами/Unicode. Старый instance/аккаунты остаются.
5. Проверь совпадающее имя, отмену picker/export/import, read-only disk и сетевую
   ошибку во время download; прежний archive/instance должен сохраниться.
6. Resize 900–1920, DPI 125–200%, scrolling длинного списка и Quick CSS
   `#instance-transfer`. Проверь системные Save/Open dialogs и overwrite prompt.

Fixture tests не заменяют запуск Minecraft с настоящими модами. Отдельно требуется
проверка Installer и clean Windows из [release checklist](RELEASE.md).

## Результаты проверки 5 октября 2026

- Исходное дерево было чистым (`c9543ff`), baseline: 654 checks и успешные Debug/Release.
- Clean build выполнен; финальный штатный packaging process повторил Debug/Release,
  restore, всю automated suite и Windows x64 publish: 0 warnings / 0 errors.
- Итог: **739 checks**, **85 новых**. Fixture round-trips проверяют Vanilla/Fabric/
  Forge/NeoForge metadata, exact versions/dependencies, RAM/Java defaults, custom root,
  сохранение старых paths/accounts, вредоносные ZIP/JSON, cancellation, failed-download
  hashes и rollback при отказе installer/network/registration. Native Avalonia headless
  renders проверены на ширине 900, 1060 и 1920, включая Quick CSS и UI state.
- Final self-contained Portable реально запущен на текущей Windows: responsive окно,
  встроенный .NET 10.0.12/Skia, нормальное закрытие, exit code 0.
- Самостоятельно не выполнялись real Minecraft launch после transfer, новая установка
  Forge/NeoForge через package, интерактивные system pickers, native DPI, Installer
  install/uninstall и clean Windows VM. Новые network tests использовали fixtures;
  официальная API документация проверена, live download round-trip не заявляется.
- `git diff --check` проходит. Нет удалённых source files, generated archives в git
  или изменений защищённой папки assets. Commit/tag/push/release не создавались.

Artifacts находятся в `.artifacts/releases/instance-transfer-preview/` и изолированы
от предыдущего релиза. Metadata остаётся `0.1.1-alpha`: это preview рабочего дерева,
а не автоматически опубликованный новый публичный release. Installer и Portable
unsigned; версия будущего публичного релиза выбирается отдельно после manual checks.
