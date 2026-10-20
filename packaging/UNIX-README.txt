NexLauncher 0.1.1-alpha — macOS/Linux preview

Self-contained: no .NET Runtime, SDK or development tools required.
Extract the entire archive. Keep its native libraries beside the application.

macOS: move NexLauncher.app into Applications, then open it using Finder.
The preview is unsigned and not notarized. Follow normal macOS security policy;
do not disable Gatekeeper or remove quarantine attributes automatically.
Use osx-arm64 on Apple Silicon and osx-x64 on Intel. Old Minecraft versions
and native mods may not provide Apple Silicon binaries; this is not emulated.

Linux: extract the tar.gz and run ./NexLauncher. Requires a graphical desktop,
X11/XWayland, .NET 10 distribution prerequisites, fontconfig, X11/GL libraries,
and libsecret-1 + a running/unlocked Secret Service for Microsoft credentials.
Ubuntu 24.04 example dependencies are documented in docs/PLATFORMS.md.
The desktop entry is a template: install it only after choosing a permanent
application location and setting absolute Exec/Icon paths. It is not auto-installed.

Microsoft sign-in: this preview does NOT include the owner's approved OAuth
Client ID. The browser backend is implemented but online sign-in cannot be
enabled for release until application registration/Minecraft approval is complete.
Users do not need to register their own application. Local accounts are available.
Windows keeps the existing WebView2 login and DPAPI credentials unchanged.

User data: macOS ~/Library/Application Support/NexLauncher;
Linux $XDG_DATA_HOME/NexLauncher (normally ~/.local/share/NexLauncher).
Minecraft instances may be stored in a separately selected directory.
Replacing/removing application binaries does not remove user data.
Auth vaults cannot be copied between operating systems; sign in again there.

Package validation alone does not verify native startup. Native launch,
Microsoft login, Java download and Minecraft/loader launch require target-OS tests.
See docs/PLATFORMS.md in the source repository for the checklist.
This product is independent of Mojang/Microsoft. LICENSE.txt and
THIRD-PARTY-NOTICES.txt describe separate component licenses; PRIVACY.txt describes data use.
