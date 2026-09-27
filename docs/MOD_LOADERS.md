# Vanilla, Fabric, Forge и NeoForge

В разделе **Сборки**: название → версия Minecraft → загрузчик → его версия →
**Создать сборку**. Затем нажми **Установить** на странице игры. Наличие аккаунта
для установки не требуется. Loader фиксируется при создании; смена загрузчика
существующей сборки и автоматическое обновление loader сейчас не предусмотрены.
Создай другую независимую сборку, чтобы попробовать новую комбинацию.

## Что поддерживается

| Тип | Источник версий | Установка |
| --- | --- | --- |
| Vanilla | Официальный каталог Mojang через CmlLib.Core 4.0.6 | CmlLib устанавливает клиент, ресурсы, библиотеки и Java |
| Fabric | `meta.fabricmc.net/v2/versions/loader/{minecraft}` | Официальный `profile/json`, затем CmlLib устанавливает библиотеки |
| Forge | Maven metadata `maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml` | Официальный installer JAR; Minecraft 1.13+ и install_profile spec 1 |
| NeoForge | Maven metadata `maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml` | Официальный installer JAR; Minecraft 1.20.2+ и install_profile spec 1 |

Список не захардкожен. Forge фильтруется по точному Minecraft prefix координаты.
NeoForge использует опубликованные схемы `20.2.*`/`21.*` и четырёхкомпонентные
`26.1.0.*`; Minecraft 26.1.2 соответствует `26.1.2.*`. Неизвестные схемы
и snapshot/alpha NeoForge не предлагаются. Перед запуском installer дополнительно
проверяется `minecraft` из install_profile и `inheritsFrom` из version.json.

Нет совместимых версий — появляется объяснение, создать неверную комбинацию
через форму нельзя. Forge до 1.13, старый NeoForge 1.20.1 (другой Maven artifact),
Quilt, OptiFine, серверные установки и автоматическое преобразование сборок
в этом этапе не реализованы.

## Launch pipeline и Java

`GameInstance` хранит Minecraft version (`VersionId`), enum `Loader`,
`LoaderVersion`, постоянный `GameDirectory`, имя, Java/RAM и необязательные
Modrinth project/version IDs. Загрузчик не выводится из имени папки.

`ILoaderCatalog`/`LoaderCatalog` отвечают за официальную metadata.
`ILoaderInstaller`/`LoaderInstaller` устанавливают loader независимо от Modrinth
и возвращают конкретный CmlLib profile ID. `MinecraftService` проверяет Vanilla,
устанавливает/проверяет loader, библиотеки и лишь после успеха записывает marker.
Неудачный repair удаляет прежний marker. Установка может быть повторена после отмены.

На каждом запуске используется этот же pipeline. Microsoft-сессия проходит прежний
refresh, локальная сессия остаётся `legacy` с детерминированным UUID. Загрузчики
не изменяют серверную authentication. RAM и вручную указанный Java path сохраняются.
Автоматическая Java определяется базовой версией Minecraft; дополнительный Java 8
для loader profile без своего поля Java не загружается.

## Почему запускается официальный installer

Текущий CmlLib Forge installer 1.1.1 не предоставляет единый cancellable backend
для Forge и NeoForge. NexLauncher не добавляет этот пакет: для современных
Forge/NeoForge используется официальный `java -jar installer.jar --installClient <game>`.
Это штатный процесс, необходимый для processors и патчей клиента.

JAR поступает только с официального Maven host, сверяется с его SHA-1, проверяются
пути ZIP entries и metadata. Путь передаётся отдельным аргументом без shell,
рабочая папка — собственная `.nex-stage-*` внутри данной игры. Администраторские
права не запрашиваются, токены аккаунтов installer не получает. Installer выполняет
свой официальный код с правами пользователя Windows; это **не sandbox для Java**.
NexLauncher не исполняет scripts или installer из modpack.

Отмена/20-минутный timeout останавливают только запущенное дерево installer.
Уже запущенный Minecraft по-прежнему живёт независимо от лаунчера.
Installer JAR удаляется после завершения; журнал остаётся в
`game/.nex-stage-*/installer-output.log`. Проверки hash внутри installer включены.
Junction/symlink в целевом дереве запрещены; проверка выполняется вне UI-потока
до передачи существующей папки CmlLib и повторно перед Java installer. Путь с `!` для официального Java
installer не поддерживается; пробелы и Unicode передаются через ArgumentList.

## Проверки и границы подтверждённой поддержки

Fixtures проверяют resolution, несовместимые сочетания, сохранение и создание
четырёх типов. Opt-in smoke устанавливает Fabric 0.16.10, Forge 52.0.28 и
NeoForge 21.1.172 на Minecraft 1.21.1, проверяет success marker и построение
реального CmlLib process с Java 21. Это не запуск игрового окна и не гарантия
совместимости каждого нового релиза Minecraft или стороннего мода.

    dotnet run --project tests/NexLauncher.Checks -c Release --artifacts-path .artifacts -- --modded-network
    dotnet run --project tests/NexLauncher.Checks -c Release --artifacts-path .artifacts -- --loader-install-smoke

Второй вариант скачивает игру и запускает официальные installers в `.artifacts/loader-smoke`.
Повторный запуск проверяет repair существующих тестовых установок.

## Официальные источники, проверенные 27 сентября 2026

- [CmlLib mod loader installers](https://cmllib.github.io/CmlLib.Core-wiki/en/cmllib.core/installer/)
- [CmlLib MinecraftLauncher: installation, inheritance, Java, process](https://github.com/CmlLib/CmlLib.Core/blob/master/src/MinecraftLauncher.cs)
- [CmlLib Forge installer 1.1.1](https://github.com/CmlLib/CmlLib.Core.Installer.Forge)
- [Fabric Meta API](https://meta.fabricmc.net/)
- [Официальный Forge installer CLI](https://github.com/MinecraftForge/Installer/blob/2.0/src/main/java/net/minecraftforge/installer/SimpleInstaller.java)
- [Официальный NeoForge installer CLI](https://github.com/NeoForged/LegacyInstaller/blob/main/src/main/java/net/minecraftforge/installer/SimpleInstaller.java)
- [NeoForge versioning 20.2](https://neoforged.net/news/20.2release/)
- [NeoForge versioning 26.1](https://neoforged.net/news/26.1release/)
