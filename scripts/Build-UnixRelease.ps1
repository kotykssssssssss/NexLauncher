#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateSet('linux-x64','osx-x64','osx-arm64')][string]$RuntimeIdentifier,
    [string]$OutputName = 'cross-platform-preview',
    [string]$MicrosoftClientId = '',
    [switch]$SkipChecks
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if ($OutputName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}$') { throw 'Invalid output directory label.' }
if ($MicrosoftClientId -and ($MicrosoftClientId -notmatch '^[0-9a-fA-F-]{36}$' -or [guid]$MicrosoftClientId -eq [guid]::Empty)) { throw 'Use your own public application GUID, never a client secret.' }
[xml]$project = Get-Content (Join-Path $repo 'NexLauncher.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$run = Join-Path $repo ('.artifacts/platform-builds/' + $RuntimeIdentifier + '-' + [guid]::NewGuid().ToString('N'))
$build = Join-Path $run 'build'
$publish = Join-Path $run 'publish'
$payload = Join-Path $run 'payload'
$output = Join-Path $repo ('.artifacts/releases/' + $OutputName)
New-Item -ItemType Directory -Force $publish,$payload,$output | Out-Null
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object (Join-Path $run ($Name + '.log')) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $Name failed. See $run" }
}
Push-Location $repo
try {
    $common = @('-p:NexLauncherPlatform=Unix','--artifacts-path',$build,'--disable-build-servers','-warnaserror')
    if (!$SkipChecks) {
        Invoke-Dotnet 'debug' (@('build','NexLauncher.csproj','-c','Debug') + $common)
        Invoke-Dotnet 'checks' @('run','--project','tests/NexLauncher.Checks','-c','Release','-p:NexLauncherPlatform=Unix','--artifacts-path',$build,'--disable-build-servers')
    }
    Invoke-Dotnet 'publish' (@('publish','NexLauncher.csproj','-c','Release','-r',$RuntimeIdentifier,'-p:PublishProfile=Unix',('-p:NexLauncherMicrosoftClientId=' + $MicrosoftClientId),'-o',$publish) + $common)
    $runtime = Get-Content (Join-Path $publish 'NexLauncher.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks = @($runtime.runtimeOptions.includedFrameworks)
    if ($frameworks.Count -ne 1 -or $frameworks[0].name -ne 'Microsoft.NETCore.App' -or $frameworks[0].version -ne '10.0.12') { throw 'Unexpected runtime; Unix package must not need WindowsDesktop.' }
    if (!(Test-Path (Join-Path $publish 'NexLauncher')) -or (Test-Path (Join-Path $publish 'NexLauncher.exe'))) { throw 'Invalid Unix apphost.' }
    $deps = Get-Content (Join-Path $publish 'NexLauncher.deps.json') -Raw
    if ($deps -match 'Microsoft.Web.WebView2|WindowsDesktop') { throw 'Windows-only dependency leaked into Unix publish.' }
    if (@(Get-ChildItem $publish -Recurse -Filter '*.pdb').Count -gt 0) { throw 'Unexpected publish debug symbols.' }
    $isMac = $RuntimeIdentifier.StartsWith('osx-')
    $application = if ($isMac) { Join-Path $payload 'NexLauncher.app/Contents/MacOS' } else { Join-Path $payload 'NexLauncher' }
    $documents = if ($isMac) { Join-Path $payload 'NexLauncher.app/Contents/Resources' } else { $application }
    New-Item -ItemType Directory -Force $application,$documents | Out-Null
    Copy-Item (Join-Path $publish '*') $application -Recurse
    Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $documents 'LICENSE.txt')
    Copy-Item (Join-Path $repo 'PRIVACY.txt') $documents
    Copy-Item (Join-Path $repo 'packaging/UNIX-README.txt') (Join-Path $payload 'README.txt')
    & (Join-Path $PSScriptRoot 'Write-ReleaseNotices.ps1') -AssetsFile (Join-Path $build 'obj/NexLauncher/project.assets.json') -DepsFile (Join-Path $publish 'NexLauncher.deps.json') -Destination $documents
    if ($isMac) {
        Copy-Item (Join-Path $repo 'Assets/branding/NexLauncher.icns') $documents
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>NexLauncher</string>
<key>CFBundleDisplayName</key><string>NexLauncher</string>
<key>CFBundleIdentifier</key><string>io.github.kotykssssssssss.NexLauncher</string>
<key>CFBundleExecutable</key><string>NexLauncher</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$($version.Split('-')[0])</string>
<key>CFBundleShortVersionString</key><string>$($version.Split('-')[0])</string>
<key>CFBundleIconFile</key><string>NexLauncher.icns</string>
<key>LSMinimumSystemVersion</key><string>15.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@ | Set-Content (Join-Path $payload 'NexLauncher.app/Contents/Info.plist') -Encoding utf8
    } else {
        Copy-Item (Join-Path $repo 'Assets/branding/NexLauncher.png'),(Join-Path $repo 'packaging/NexLauncher.desktop') $application
    }
    # Set real permissions on Unix and encoded permissions when cross-packaging on Windows.
    function Is-Executable([string]$Name) { $Name -eq 'NexLauncher' -or $Name -like '*.dylib' -or $Name -like '*.so*' }
    if (!$IsWindows) {
        foreach ($file in Get-ChildItem $payload -Recurse -File) {
            $mode = if (Is-Executable $file.Name) { 493 } else { 420 }
            [IO.File]::SetUnixFileMode($file.FullName, [IO.UnixFileMode]$mode)
        }
    }
    $extension = if ($isMac) { 'zip' } else { 'tar.gz' }
    $name = "NexLauncher-v$version-$RuntimeIdentifier.$extension"
    $archivePath = Join-Path $run $name
    $stream = [IO.File]::Create($archivePath)
    try {
        if ($isMac) {
            $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($file in Get-ChildItem $payload -Recurse -File | Sort-Object FullName) {
                    $relative = [IO.Path]::GetRelativePath($payload,$file.FullName).Replace('\','/')
                    $entry = $archive.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
                    $entry.ExternalAttributes = $(if (Is-Executable $file.Name) { 0x81ED } else { 0x81A4 }) -shl 16
                    $from = $file.OpenRead(); $to = $entry.Open()
                    try { $from.CopyTo($to) } finally { $from.Dispose(); $to.Dispose() }
                }
            } finally { $archive.Dispose() }
        } else {
            $gzip = [IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionLevel]::Optimal,$true)
            $tar = [System.Formats.Tar.TarWriter]::new($gzip, $true)
            try {
                foreach ($file in Get-ChildItem $payload -Recurse -File | Sort-Object FullName) {
                    $relative = [IO.Path]::GetRelativePath($payload,$file.FullName).Replace('\','/')
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile,$relative)
                    $entry.Mode = [IO.UnixFileMode]$(if (Is-Executable $file.Name) { 493 } else { 420 })
                    $entry.DataStream = $file.OpenRead()
                    try { $tar.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
                }
            } finally { $tar.Dispose(); $gzip.Dispose() }
        }
    } finally { $stream.Dispose() }
    if ($isMac) { & (Join-Path $PSScriptRoot 'Set-UnixZipMetadata.ps1') -ArchivePath $archivePath }
    & (Join-Path $PSScriptRoot 'Test-UnixPackage.ps1') -ArchivePath $archivePath -RuntimeIdentifier $RuntimeIdentifier
    Copy-Item -LiteralPath $archivePath -Destination $output -Force
    $file = Get-Item (Join-Path $output $name)
    $manifest = [ordered]@{ version=$version; rid=$RuntimeIdentifier; bytes=$file.Length; sha256=(Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); selfContained=$true; unsigned=$true; ownerClientIdConfigured=[bool]$MicrosoftClientId; nativeRuntimeTested=$false; builtUtc=[DateTime]::UtcNow.ToString('O') }
    $manifest | ConvertTo-Json | Set-Content (Join-Path $output ($RuntimeIdentifier + '-manifest.json')) -Encoding utf8
    Write-Host "Package: $($file.FullName) ($($file.Length) bytes)"
    Write-Host 'Cross-publish/package validation does not verify native startup, OAuth or Minecraft launch.'
} finally { Pop-Location }
