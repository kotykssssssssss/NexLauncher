NexLauncher v0.1.1-alpha - Windows 11 x64

Extract this ZIP to a normal local folder and run NexLauncher.exe.
No Visual Studio, .NET SDK or separate .NET Runtime is needed.
NexLauncher.exe is self-contained and single-file. Native components are
automatically extracted to the user's TEMP/.net directory at startup.
Keep LICENSE.txt, THIRD-PARTY-NOTICES.txt and DEPENDENCIES.json when redistributing.

Portable means no installation, NOT isolated data next to the EXE:
settings, accounts, themes and default instances are in %LOCALAPPDATA%\NexLauncher.
Installer and portable share that data. Custom instance folders are respected.
Do not run two copies simultaneously or place the EXE inside your game directory.
For updates close NexLauncher and replace the application files; keep user data.

Microsoft sign-in needs Microsoft Edge WebView2 Evergreen Runtime (normally
included in Windows 11). If missing, obtain it only from Microsoft:
https://developer.microsoft.com/microsoft-edge/webview2/
The Installer variant can install the runtime if it is missing.
Local accounts need no WebView2 and cannot access online-mode servers.

Internet is needed for initial downloads and API/authentication operations.
Minecraft and the appropriate Java runtime are downloaded on demand;
the game's hardware requirements and modpack RAM requirements also apply.

This alpha and its installer are unsigned. Windows may show SmartScreen warnings.
Verify the official source and SHA256; do not disable security software.
Back up important worlds before testing an alpha.

Source, documentation and issues:
https://github.com/kotykssssssssss/NexLauncher
NexLauncher is not affiliated with Mojang, Microsoft or Modrinth.
Modrinth content is obtained through the official API under its project licenses;
no games, mods, modpacks, accounts or Java installations are bundled here.
