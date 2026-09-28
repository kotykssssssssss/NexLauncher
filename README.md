# NexLauncher

Независимый Minecraft: Java Edition launcher на C# / Avalonia.
Не связан с Mojang или Microsoft. Текущая сборка рассчитана на Windows.

## Windows release v0.1.0-alpha

Для Windows 11 x64 подготовлены **Installer** `NexLauncher-Setup-v0.1.0-alpha.exe`
и **Portable** `NexLauncher-v0.1.0-alpha-win-x64.zip`. В portable распакуй архив
и запусти `NexLauncher.exe`. Оба содержат .NET 10.0.12 и Windows Desktop Runtime:
пользователю не нужны Visual Studio, .NET SDK или отдельный .NET Runtime.
Microsoft-вход требует Edge WebView2 Evergreen Runtime; обычно он уже есть в
Windows 11, а Installer при необходимости запускает официальный Microsoft bootstrapper.

Это неподписанная alpha: возможен SmartScreen warning. Проверяй источник и SHA256,
не отключай защиту Windows. Готовый пакет ещё нужно проверить на чистой Windows
перед публикацией. [Сборка релиза, ограничения и полный manual checklist](docs/RELEASE.md).
[Результаты фактически выполненных release-проверок](docs/RELEASE_VALIDATION.md).

## Возможности

- «Играть», «Сборки», «Настройки», журнал запуска и понятные ошибки.
- Каталог Vanilla: релизы по умолчанию, снапшоты по желанию.
- Независимые Vanilla / Fabric / Forge / NeoForge сборки с выбором совместимой
  loader version из официальной metadata. Forge — с Minecraft 1.13, NeoForge — с 1.20.2.
- Modrinth: отдельные полноширинные каталоги **Modpacks / Mods**, фильтры и сортировка,
  выбор целевого instance, проверка dependencies, установка,
  выбор версии, проверка обновлений и безопасное удаление управляемых модов.
  Отдельная страница проекта с полным безопасным Markdown-описанием, авторами,
  ссылками и установленной версией; возврат сохраняет поиск и позицию списка.
  Для отдельных модов: «Modrinth → Mods» или «Играть → Моды».
- Системный выбор папки новых сборок на локальном диске; старые пути сохраняются.
- Отдельные версии, игровые папки, миры, RAM и Java для каждой сборки.
- Установка клиента, библиотек, ресурсов и Java через CmlLib.Core;
  прогресс, отмена и восстановление файлов перед запуском.
- Microsoft → Xbox → Minecraft: штатный Windows OAuth CmlLib без собственного
  Client ID, несколько аккаунтов, выбор активного, обновление сессии и удаление.
- Локальные профили: имя без Microsoft-входа, стабильный offline UUID, общий список
  с Microsoft-аккаунтами и сохранение активного профиля.
- Quick CSS: пользовательские цвета, фоны PNG/JPEG, типографика, отступы,
  скругления и состояния controls, перезагрузка и автообновление.
- Закрытие лаунчера не завершает уже запущенный процесс игры.

Создание сборок и установка файлов доступны без аккаунта. Для одиночной игры
и серверов, допускающих offline-профили, можно выбрать локальное имя. Серверы
с Microsoft-проверкой требуют Microsoft-аккаунт с доступом к Java Edition.
Локальный профиль не отключает сетевые загрузки и проверки файлов игры.

Подробности:

- [Quick CSS: полный справочник, ограничения и примеры](docs/QUICK_CSS.md).
- [Готовая тема Plum Evening](Assets/quickcss.example.css).
- [Локальные и Microsoft-аккаунты: создание, выбор, хранение](docs/ACCOUNTS.md).
- [Microsoft authentication, защищённый кеш и ручные проверки](docs/MICROSOFT_AUTH.md).
- [Загрузчики: источники, установка, Java и ограничения](docs/MOD_LOADERS.md).
- [Modrinth: моды, dependencies, modpacks, правила и восстановление](docs/MODRINTH.md).
- [Папки, migration и безопасность хранения](docs/STORAGE.md).
- [Релиз Windows: упаковка, обновление, smoke tests](docs/RELEASE.md).

## Запуск разработки

Для разработки нужны Windows и .NET 10 SDK. Обычный `dotnet build` создаёт
framework-dependent сборку, которой нужен .NET Windows Desktop Runtime 10.
Распространяемые release-пакеты self-contained и этого требования не имеют.
Microsoft Edge WebView2 Runtime требуется только для Microsoft-входа;
локальный аккаунт не открывает WebView2 и не требует его для входа.

    dotnet restore NexLauncher.csproj --artifacts-path .artifacts
    dotnet build NexLauncher.csproj --configuration Release --artifacts-path .artifacts
    dotnet run --project NexLauncher.csproj --configuration Release --artifacts-path .artifacts

Executable: .artifacts/bin/NexLauncher/release/NexLauncher.exe.
Build outputs и release artifacts находятся в `.artifacts/` и игнорируются Git.
Полный release pipeline: `pwsh -NoProfile -File scripts/Build-Release.ps1`.

CmlLib.Core 4.0.6, Auth.Microsoft 3.3.1 и Avalonia 12.1.0 сохранены.
Добавлен System.Security.Cryptography.ProtectedData 10.0.12 для DPAPI.
Прямые зависимости MSAL, нужные прежней реализации с собственным Client ID,
удалены: Windows использует встроенную OAuth-конфигурацию CmlLib.

## Quick CSS за минуту

Открой «Настройки» → «Quick CSS» → «Создать пример». Лаунчер скопирует
полную тему в папку данных, включит и применит её. Нажми «Открыть в редакторе»
и меняй переменные в начале файла. При включённом автообновлении достаточно
сохранить CSS.

Для своего файла нажми «Выбрать файл», включи Quick CSS и нажми «Применить».
«Перезагрузить» сохраняет выбранные параметры и перечитывает тему.
Чтобы вернуть стандартное оформление, выключи переключатель и нажми «Применить».

Это ограниченный API Avalonia, не браузерный CSS. Тема не исполняет код,
не управляет аккаунтами и не скачивает ресурсы. Локальные фоновые изображения
разрешены только внутри папки темы. Полный reference — в документации выше.

## Аккаунты и данные

На Windows данные находятся в %LOCALAPPDATA%\NexLauncher.

- settings.json — сборки, активная сборка, настройки Quick CSS; без токенов.
  Старое публичное поле MicrosoftClientId сохранено для совместимости конфигурации
  и не используется текущим Windows-входом.
- auth/profiles.json — локальные имена и общий активный аккаунт (тип/ID); без токенов.
- auth/accounts.dat — кеш Microsoft-аккаунтов и внутренний выбор Microsoft-провайдера,
  зашифрованные DPAPI CurrentUser. Прочитать его может текущий пользователь Windows.
- themes/ — примеры пользовательских CSS-файлов.
- instances/<guid>/game/ — отдельная игра, миры и настройки каждой сборки.
  Это прежнее место по умолчанию; новые сборки могут находиться в выбранной папке.
- В папке игры `.nexlauncher/mods.json` — metadata управляемых модов,
  `.nexlauncher/pack.json` — источник и состав установленного Modrinth pack.
- catalogue/ — каталог версий.
- logs/launcher.log — ограниченный журнал с редактированием токенов.

В настройках можно добавить Microsoft-аккаунт или создать локальный профиль
с именем из 3–16 латинских букв, цифр и _. Выбери профиль и нажми «Использовать»;
удаление убирает его с этого устройства. Тип аккаунта виден рядом с именем.
Microsoft-аватар загружается с официального textures.minecraft.net; локальный
профиль использует букву имени. Перед запуском Microsoft-сессия обновляется
с проверкой доступа к Java Edition; локальная сессия создаётся без OAuth.
При ошибке Microsoft-входа автоматического перехода на локальный профиль нет.

Повреждённые настройки и защищённый кеш не перезаписываются молча.
Удаление аккаунта из NexLauncher не завершает отдельную браузерную сессию
Microsoft/WebView2. Повторный вход показывает выбор аккаунта.

## Проверки

    dotnet run --project tests/NexLauncher.Checks --configuration Release --artifacts-path .artifacts

Console harness проверяет настройки, parser, runtime Quick CSS, DPAPI, операции
аккаунтов с тестовым backend, локальные имена/UUID и сохранение выбора, отсутствие
Microsoft-вызовов в локальных операциях, передачу сессии в запуск, отмену и отрисовку окна.
Настоящее окно Avalonia рендерится в Headless; снимки, включая применённую
тему, сохраняются в .artifacts/checks/. Для DPAPI-проверок нужен Windows.
Новые fixtures покрывают metadata загрузчиков, root migration, HTTP/rate limits,
Modrinth compatibility/dependencies, hashes, staging, mrpack, небезопасные пути
и UI создания сборок. Обычный suite не требует доступности Modrinth/loader API.
Проверки details покрывают API metadata, безопасную разметку/ссылки, отмену,
совместимость перед установкой, update/remove и сохранность ручных JAR.
Снимки details и его Quick CSS оформления также находятся в `.artifacts/checks/`.

Отдельная проверка официальных loader metadata и Modrinth API:

    dotnet run --project tests/NexLauncher.Checks -c Release --artifacts-path .artifacts -- --modded-network

Реальные Fabric/Forge/NeoForge installers и построение launch process (без открытия игры):

    dotnet run --project tests/NexLauncher.Checks -c Release --artifacts-path .artifacts -- --loader-install-smoke

Последняя команда скачивает несколько независимых установок в `.artifacts/loader-smoke`.
Нужны сеть, свободное место и время. Повторный запуск проверяет repair.

Реальный каталог Mojang:

    dotnet run --project tests/NexLauncher.Checks --configuration Release --artifacts-path .artifacts -- --network

Необязательная проверка полной установки скачивает Vanilla 1.21.1 и Java
в отдельную тестовую папку, затем проверяет восстановление client JAR.
Нужны сеть, время и место; игра не запускается:

    dotnet run --project tests/NexLauncher.Checks --configuration Release --artifacts-path .artifacts -- --network --install-smoke

Реальный интерактивный вход, аккаунт без Java Edition, два Microsoft-аккаунта,
silent refresh после перезапуска и полноценный игровой запуск нужно проверить
вручную с собственными аккаунтами. Автоматические проверки их не симулируют
как успешную работу внешних сервисов.

## Дальше

Перенос существующих сборок, обновление целого modpack и другие loaders остаются
за рамками этого этапа. Для Linux/macOS нужен отдельный
OAuth backend с собственным зарегистрированным Client ID и хранилище секретов ОС;
границы IAccountService / IMinecraftAuthenticationBackend / IAccountVault
позволяют добавить их без переписывания UI и механизма запуска.
