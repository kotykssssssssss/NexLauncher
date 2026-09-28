#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef WebViewBootstrapper
  #error WebViewBootstrapper is required
#endif

[Setup]
; Keep this identity for all subsequent upgrades, including stable releases.
AppId={{3A9BC67A-E38C-4C82-A721-45F11663F31D}
AppName=NexLauncher
AppVersion={#AppVersion}
AppPublisher=kotyksssssssss
AppPublisherURL=https://github.com/kotykssssssssss/NexLauncher
AppSupportURL=https://github.com/kotykssssssssss/NexLauncher/issues
AppUpdatesURL=https://github.com/kotykssssssssss/NexLauncher/releases
VersionInfoVersion={#NumericVersion}
VersionInfoDescription=NexLauncher Setup
VersionInfoProductName=NexLauncher
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\NexLauncher
DefaultGroupName=NexLauncher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=NexLauncher-Setup-v{#AppVersion}
UninstallDisplayIcon={app}\NexLauncher.exe
UninstallDisplayName=NexLauncher
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=NexLauncher.exe
RestartApplications=no
SetupLogging=yes
LicenseFile={#PayloadDir}\LICENSE.txt
InfoBeforeFile=INSTALL-NOTES.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#WebViewBootstrapper}"; DestDir: "{tmp}"; DestName: "NexLauncher-WebView2Setup.exe"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{group}\NexLauncher"; Filename: "{app}\NexLauncher.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NexLauncher"; Filename: "{app}\NexLauncher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\NexLauncher-WebView2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime for Microsoft sign-in..."; Flags: waituntilterminated; Check: NeedsWebView2; AfterInstall: CheckWebView2Result
Filename: "{app}\NexLauncher.exe"; Description: "{cm:LaunchProgram,NexLauncher}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
const
  WebViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasRuntime(Root: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, WebViewKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function NeedsWebView2: Boolean;
begin
  Result := not (HasRuntime(HKCU32) or HasRuntime(HKCU64) or HasRuntime(HKLM32) or HasRuntime(HKLM64));
end;

procedure CheckWebView2Result;
begin
  if NeedsWebView2 then
    SuppressibleMsgBox('WebView2 could not be installed. NexLauncher and local accounts are available. For Microsoft sign-in, install Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and retry. Internet access may be required.', mbInformation, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Target, Data: String;
begin
  Result := '';
  Target := AddBackslash(ExpandFileName(WizardDirValue));
  Data := AddBackslash(ExpandConstant('{localappdata}\NexLauncher'));
  if CompareText(Copy(Target, 1, Length(Data)), Data) = 0 then
    Result := 'Choose a separate application folder. This folder is reserved for NexLauncher user data.';
end;
// No UninstallDelete rules: settings, auth, games and themes are never owned by Setup.
