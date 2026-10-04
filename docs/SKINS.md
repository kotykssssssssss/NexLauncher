# Скины в NexLauncher

В **Настройках → Аккаунты** выбери сохранённый аккаунт и нажми **Скин…**.
Кнопка «Аккаунты» в верхней панели открывает настройки. Skin Manager работает с
выбранным в списке профилем; для изменения его скина не нужно делать этот профиль
активным для запуска игры.

## Возможности и границы

| | Microsoft / Java Edition | Локальный аккаунт |
| --- | --- | --- |
| Получить текущий скин | Из Minecraft Services | Из хранилища NexLauncher |
| Импорт PNG, Classic / Slim, preview | Да | Да |
| Применить | Меняет скин самого Minecraft-аккаунта | Сохраняет **Local Skin только в NexLauncher** |
| Reset | Удаляет активный пользовательский скин в Minecraft Services | Удаляет сохранённый Local Skin |
| Видимость в игре | Через штатный профиль Minecraft, с учётом кеширования | **Не обеспечивается этой функцией** |

Ни моды, ни Java agents, ни resource packs не добавляются в instances. Оригинальные
игровые JAR, launch arguments, session UUID и loader не меняются. Local Skin не
выдаётся за Microsoft-профиль и никогда не отправляется в сеть.

## Как пользоваться

Также доступна отдельная вкладка **Скины**: локальная библиотека PNG с карточками,
поиском, экспортом и передачей черновика в этот же Skin Manager.
Это не автоматический онлайн-каталог: [описание и ограничения](SKIN_CATALOG.md).

1. Открой **Скин…**: лаунчер загрузит текущий скин выбранного аккаунта. «Обновить»
   повторяет чтение; при переключении аккаунта прежний preview и черновик очищаются.
2. Нажми **Загрузить PNG…** и выбери файл системным picker. Импорт создаёт черновик,
   а не сразу меняет аккаунт.
3. Выбери **Classic / Steve** (руки 4 px) либо **Slim / Alex** (руки 3 px).
   Эти названия обозначают геометрию, а не обещают стандартную текстуру Steve/Alex.
4. Проверь персонажа: перетаскивание мышью поворачивает его, колесо меняет масштаб.
5. Нажми **Применить к Minecraft-аккаунту** или **Сохранить Local Skin**. Изменение
   модели уже загруженного скина также можно применить без повторного выбора файла.
6. **Вернуть стандартный скин** / **Удалить Local Skin** сбрасывает соответствующее
   сохранение. Для Microsoft после операции автоматически перечитываются профиль
   и текстура. Для Local данные перечитываются с этого устройства.

Общий индикатор операции и кнопка отмены лаунчера продолжают работать. Закрытие
Skin Manager отменяет его запрос. Отмена после отправки upload/reset не гарантирует,
что сервер не применил изменение: нажми «Обновить», чтобы проверить результат.
Если изменение принято, но последующее чтение не удалось, интерфейс явно сообщает
об этом; сетевые мутации автоматически не повторяются.

## PNG и preview

- Реальный статический PNG, максимум **256 КиБ**, **64×64** или legacy **64×32**.
  HD, GIF/JPEG с расширением `.png`, APNG, повреждённые/обрезанные изображения
  и полностью прозрачные файлы отклоняются.
- Проверяются сигнатура, размеры, структура, CRC блоков и полное декодирование.
  PNG заново кодируется: комментарии и прочие ancillary metadata не копируются.
- Основные области текстуры делаются непрозрачными по семантике Minecraft;
  прозрачность внешнего слоя сохраняется. Интерфейс сообщает о нормализации.
- Legacy автоматически преобразуется в 64×64 с зеркальными левыми конечностями.
  Для такого импорта разрешён только Classic; для Slim нужен современный layout.
- Preview — собственный небольшой ортографический renderer на **уже используемом
  SkiaSharp**: голова, тело, руки, ноги, внешний слой и обе ширины рук. Рендеринг
  и декодирование выполняются вне UI thread; кадры ограничены 512×512.
- Если custom skin отсутствует или текстуру не удалось получить, показывается
  подписанный **нейтральный манекен**, а не выдуманный «текущий Steve». Точный
  стандартный скин, выбранный игрой по UUID, в этом preview не воспроизводится.
- Позы/анимация, capes, аксессуары Bedrock, idle rotation и полный игровой renderer
  не реализованы. На максимальном приближении персонаж может выходить за preview.

## Microsoft: API и авторизация

Используется фиксированный HTTPS origin `https://api.minecraftservices.com`:

| Операция | Запрос |
| --- | --- |
| Профиль | `GET /minecraft/profile` |
| Upload | `POST /minecraft/profile/skins`, multipart: `variant=classic` или `slim`, `file=skin.png`, `image/png` |
| Reset | `DELETE /minecraft/profile/skins/active` |

Профиль содержит UUID, имя и список skins; выбирается запись `state=ACTIVE`,
геометрия определяется `variant`, текстура — `url`. UUID проверяется относительно
выбранного аккаунта. Текстуры загружаются отдельно только с `textures.minecraft.net`,
по HTTPS, без bearer и cookies, с проверкой host при каждом redirect; legacy HTTP
URL повышается до HTTPS. Credential-bearing API requests redirects не следуют.

Bearer — **Minecraft access token** из существующей CmlLib Microsoft → Xbox →
Minecraft session. OAuth/Xbox token не подставляется в этот API. Метод
`GetSessionForAccountAsync` обновляет credentials нужного сохранённого аккаунта
штатным silent flow и сохраняет прежний активный аккаунт запуска. Если refresh
отозван/истёк, потребуется обычный повторный вход; Skin Manager сам не открывает
OAuth окно. Дополнительный Client ID, Client Secret или новые scopes не добавлены.

Minecraft skin API не предоставляет отдельную публичную регистрацию scopes для
этой desktop-функции: используется уже полученная сессия лицензированного Java
профиля. DPAPI vault и его формат не изменены. Токены не сохраняются в skin.json,
preview, UI bindings или журнале и не уходят на CDN.

HTTP client переиспользуется. Запрос ограничен 35 секундами, response — 64 КиБ,
текстура — 256 КиБ. 400/401/403/404/429, 5xx, timeout, некорректный JSON и network
failure получают короткие сообщения. Raw body и серверные credentials не выводятся.
При 401 пользователь может повторить refresh; при 429 нужно подождать.

**Проверка источников, 4 октября 2026:** официальная [статья Minecraft о скинах](https://www.minecraft.net/en-us/article/what-is-minecraft-skin)
подтверждает upload и Classic/Slim. Полная актуальная публичная официальная REST
schema этих трёх endpoints не найдена; не следует считать документацию Minecraft
Help OpenAPI-контрактом. Адреса/поля дополнительно сверены с первичным
[исходником CmlLib/MojangAPI](https://github.com/CmlLib/MojangAPI/blob/master/MojangAPI/Mojang.cs),
а получение сессии — с [документацией JELoginHandler](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/cmllib.core.auth.microsoft/jeloginhandler/).
Эта библиотека не добавлялась в dependencies. Проверки HTTP используют fake transport;
**настоящие upload/reset аккаунта пользователя автоматически не выполнялись**.
Изменение лицензированного профиля требуется проверить вручную перед публикацией.

Скин относится к аккаунту, поэтому не зависит от instance или loader. Уже запущенный
Minecraft и другие игроки могут использовать кеш до переподключения/перезапуска.
Маленький аватар в верхней панели обновляется существующим механизмом обновления
публичного профиля; Skin Manager перечитывает свой полный preview сразу.

## Почему Local Skin не подменяет скин Vanilla

Исследованы оригинальные официальные клиенты **1.21.1** (основная проверенная версия
проекта, Authlib 6.0.54) и **26.3** (release в manifest на момент исследования).
Артефакты получены по [официальному manifest Mojang](https://piston-meta.mojang.com/mc/game/version_manifest_v2.json),
SHA1 сверены с metadata. Использованы official mappings 1.21.1 и `javap -p -c` для
client/Authlib; игровые файлы не менялись. Выводы bytecode лежат только в игнорируемой
`.artifacts/skin-research`, а не распространяются вместе с приложением.

Цепочка в клиенте:

1. `Minecraft.getGameProfile()` читает результат profile future. Профиль запрашивается
   через session service по UUID (`fetchProfile`); fallback — `GameProfile(UUID, name)`
   без property `textures`.
2. `SkinManager` строит ключ из UUID / texture property и разбирает профиль через
   Authlib/session service. URL, модель и signature state берутся из texture payload.
   В 26.3 результат skin API клиента используется как `PlayerSkin`; принцип тот же.
3. `SkinManager.TextureCache` кеширует **уже разрешённую** текстуру по её URL/hash.
   Простое помещение PNG в cache не связывает его с offline GameProfile.
4. Если текстуры нет, `DefaultPlayerSkin` выбирает встроенный fallback по UUID.
   В 1.21.1 есть 18 wide/slim вариантов. NexLauncher не подменяет UUID ради скина.

В launch/session API нет поддерживаемых `--skin` / `--skinModel`. Старые
`userProperties/profileProperties` есть в конфигурации 1.21.1, но их парсинг **не
делает произвольный texture payload локальным профилем**: путь
`Minecraft.getGameProfile()` получает профиль через session service и empty fallback,
а соответствующие поля UserData не участвуют в этой привязке. SkinManager также
различает проверенные и непроверенные texture signatures; нельзя объявлять свой PNG
официальным подписанным профилем. Это не утверждение, что любые unsigned textures
в любом server/client flow всегда отвергаются.

Resource pack способен заменить встроенную fallback-текстуру, но замена общая для
всех игроков с тем же fallback, не даёт свободного выбора ширины рук, зависит от
версии и настроек каждого instance. Это не корректное независимое per-account
решение. Поэтому он не генерируется и не включается автоматически.

Вывод исследования: для этих современных стандартных клиентов launcher/session
параметров и локального cache недостаточно для произвольного per-account скина.
UI честно реализует **локальное сохранение и preview**, а не фиктивную игровую замену.
Для остальных версий не заявляется неподтверждённая совместимость client hooks.

Минимальный собственный механизм на будущее — version-aware NexLauncher client
hook (например, Java instrumentation agent), подменяющий результат skin lookup
только для конкретного локального UUID и читающий локальный PNG/модель. Это уже
изменение поведения клиента в памяти, требует mappings/совместимости каждого
release/loader и отдельного решения пользователя. **Такой код не реализован и не
внедряется** в данном этапе. Его наличие само по себе не передаст скин другим игрокам.

## Кто увидит Local Skin

| Сценарий | Поведение этой реализации |
| --- | --- |
| Singleplayer | Preview есть в NexLauncher; неизменённая игра использует штатный skin lookup/fallback |
| LAN | Локальный PNG не передаётся другим клиентам или LAN host |
| Online-mode server | Требуется настоящая авторизованная сессия; Local Skin не снимает проверку |
| Offline-mode server | Сервер допускает offline identity, но произвольный PNG не становится общим скином автоматически |

Сервер может сам предоставлять texture properties, а клиенты — поддерживать свой
skin protocol. Такое поведение зависит от сервера/клиента, signatures и доступности
текстур, а не от этой функции NexLauncher. Обещания автоматической видимости нет.

## Хранение и совместимость

`%LOCALAPPDATA%\NexLauncher\skins\<sha256(type:id)>\`:

- `skin.json` — `FormatVersion=1`, стабильный AccountId, AccountType, Classic/Slim,
  managed filename, SHA256 и признак legacy import. Credentials отсутствуют.
- `<случайный-guid>.png` — проверенный, заново закодированный PNG.

Исходный путь из picker не сохраняется. Skin не зависит от папки instance и
сохраняется при обновлении/uninstall/reinstall вместе с остальными user data.
Nickname не участвует в ключе storage: если identity сохраняется при переименовании,
skin сохранится. **UI переименования аккаунта сейчас нет**; создание нового локального
ника создаёт новую identity, а не переименование существующего профиля.

Новый PNG создаётся без перезаписи существующего файла, затем metadata атомарно
заменяется. Только после commit удаляется предыдущий управляемый PNG. Ошибка/отмена
не заменяет прежнюю metadata. Hash, формат, размер и путь проверяются при чтении.
Некорректная/будущая metadata не затирается; не удаляй оригинал без резервной копии.
Reset удаляет только проверенную managed запись, неизвестные файлы сохраняет.
Удаление аккаунта не стирает его Local Skin: для очистки используй «Удалить Local
Skin» до удаления аккаунта. Остатки после сбоя питания можно разбирать только
после закрытия лаунчера и резервного копирования.

Старые аккаунты без skin metadata загружаются как раньше, migration accounts/
settings и изменение DPAPI не нужны. Quick CSS: `#skin-panel` — Border с теми же
свойствами, что `.card`; существующие `button`, `combobox`, `text`, `.caption`
продолжают действовать. Тема не получает доступ к skin API или credentials.

## Перед публикацией проверить вручную

- [ ] Microsoft: открыть текущий скин; импортировать свой 64×64 PNG, выбрать Slim,
  применить. Проверить изменение на minecraft.net и в новом игровом запуске.
- [ ] Переключить Classic, применить; вернуть стандартный скин; повторно обновить.
- [ ] Изменить skin второго сохранённого Microsoft-профиля при другом активном
  аккаунте запуска: активная identity не должна меняться.
- [ ] Local: импорт, черновик, Classic/Slim, сохранение, удалить исходный PNG,
  перезапустить лаунчер и проверить сохранение; затем удалить Local Skin.
- [ ] Проверить битый/слишком большой PNG, 64×32, network error, expired auth,
  отмену загрузки и закрытие панели во время запроса.
- [ ] Проверить drag/wheel, внешний слой, узкое/широкое окно, DPI 125–200%, Quick CSS.
- [ ] Запустить существующие Vanilla/Fabric/Forge/NeoForge instances с обоими типами
  аккаунтов. Local Skin не должен появляться в игре или добавлять файлы в mods.

Автоматические checks используют synthetic PNG, fake HTTP/auth и временные папки.
Реальные Microsoft skin mutations, видимость у другого игрока, мышь/DPI и clean
Windows требуют ручной проверки. Обычный release checklist: [RELEASE.md](RELEASE.md).

## Результаты проверки working tree, 4 октября 2026

- Clean Debug / Release: **0 warnings, 0 errors** (`-warnaserror`).
- Полный Release regression suite: **556/556 checks**, включая 93 новых skin/auth/UI
  checks относительно исходных 463. Сохранены все исходные проверки.
- Headless UI проверен при ширине 900, 1060 и 1920; сохранены реальные render frames,
  проверены import/draft/apply/reset, account switch, late responses, отмена и Quick CSS.
  Это не подтверждение native мыши или Windows DPI — они остаются в manual checklist.
- Официальные clients 1.21.1 и 26.3: SHA1 совпадает с Mojang metadata. Live read-only
  `GET /minecraft/profile` без credentials возвращает ожидаемый **401**.
- Production Installer и Portable собраны в `.artifacts/releases/skin-manager-preview`.
  Версия metadata **0.1.1-alpha сохранена**, новую публичную версию надо выбрать
  отдельно перед выпуском. Прежние patch artifacts не заменены.

| Artifact | Размер, bytes |
| --- | ---: |
| `NexLauncher-Setup-v0.1.1-alpha.exe` | 46 057 584 |
| `NexLauncher-v0.1.1-alpha-win-x64.zip` | 60 668 774 |

Portable ZIP содержит single-file self-contained EXE и notices; .NET SDK/Runtime
пользователю не нужен. Артефакты unsigned. EXE запускался из распакованного ZIP
с отключённым SDK/NuGet/runtime lookup дочернего процесса: responsive window,
bundled Skia/WebView2 loader, .NET/Windows Desktop **10.0.12**, завершение с code 0.
Два существующих settings/account metadata files не изменились (hash comparison).
Это текущий Windows account, **не clean VM**. Installer собран, но новый install/
uninstall и настоящая Microsoft skin mutation в этом этапе не выполнялись.
