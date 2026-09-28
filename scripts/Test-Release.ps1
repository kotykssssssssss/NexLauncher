#requires -Version 7.0
param([Parameter(Mandatory)][string]$PortableZip)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or ![Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell 7 on Windows.' }
if (Get-Process NexLauncher -ErrorAction SilentlyContinue) { throw 'Close NexLauncher before the smoke test.' }
$repo = Split-Path $PSScriptRoot -Parent
$zip = (Resolve-Path -LiteralPath $PortableZip).Path
$root = Join-Path $repo ('.artifacts/release-smoke/Windows Релиз ' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $root 'portable app'
$native = Join-Path $root 'native cache'
New-Item -ItemType Directory -Force $app, $native | Out-Null
$expected = @('NexLauncher.exe', 'README.txt', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'DEPENDENCIES.json')
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries.FullName)
    if (@(Compare-Object $expected $names).Count -ne 0 -or $names.Count -ne $expected.Count) { throw 'Unexpected portable archive contents.' }
    foreach ($entry in $archive.Entries) { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $app $entry.FullName)) }
}
finally { $archive.Dispose() }

# Deliberately no SDK, source checkout, NuGet cache or installed .NET resolution in the child environment.
# This is still the current Windows user, NOT a clean Windows VM or an isolated account profile.
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $app 'NexLauncher.exe'))
$info.UseShellExecute = $false
$info.WorkingDirectory = $root
foreach ($key in @($info.Environment.Keys | Where-Object { $_ -match '^(DOTNET_|CORECLR_|COMPlus_|NUGET_)' })) { [void]$info.Environment.Remove($key) }
$info.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
$info.Environment['DOTNET_ROOT'] = Join-Path $root 'no installed runtime'
$info.Environment['DOTNET_ROOT_X64'] = $info.Environment['DOTNET_ROOT']
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $native
$tracePath = Join-Path $root 'host-trace.log'
$info.Environment['DOTNET_HOST_TRACE'] = '1'
$info.Environment['DOTNET_HOST_TRACEFILE'] = $tracePath
$info.Environment['DOTNET_HOST_TRACE_VERBOSITY'] = '4'
$process = [Diagnostics.Process]::Start($info)
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "Published application exited prematurely ($($process.ExitCode))." }
    } while (!$process.MainWindowHandle -and [DateTime]::UtcNow -lt $deadline)
    if (!$process.MainWindowHandle -or !$process.Responding) { throw 'Published main window did not become responsive.' }
    # Allow the first frame and background catalogue request to finish.
    Start-Sleep -Seconds 3
    $process.Refresh()
    $modules = @($process.Modules)
    $runtime = @($modules | Where-Object ModuleName -ieq 'coreclr.dll')
    $skia = @($modules | Where-Object ModuleName -ieq 'libSkiaSharp.dll')
    # Windows singlefilehost statically links CoreCLR; a separate coreclr.dll need not exist.
    # Host trace verifies the actual runtime version, bundle and self-contained resolution.
    $trace = Get-Content -LiteralPath $tracePath -Raw
    if (!$trace.Contains('Detected Single-File app bundle') -or !$trace.Contains('Executing as a self-contained app') -or !$trace.Contains('Invoked apphost [version: 10.0.12 ')) { throw 'Host trace did not confirm the expected self-contained runtime.' }
    if (@($runtime | Where-Object { !$_.FileName.StartsWith($native, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 0) { throw 'An external CoreCLR was loaded.' }
    if ($skia.Count -ne 1 -or !$skia[0].FileName.StartsWith($native, [StringComparison]::OrdinalIgnoreCase)) { throw 'Skia native dependency did not load from the bundle.' }
    if (@(Get-ChildItem -LiteralPath $native -Recurse -Filter WebView2Loader.dll).Count -ne 1) { throw 'Missing bundled native WebView2 loader.' }
    if (@(Get-ChildItem -LiteralPath $native -Recurse -Filter '*.pdb').Count -ne 0) { throw 'Debug symbols leaked into the bundle.' }
    $report = [ordered]@{
        zipSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        title=$process.MainWindowTitle
        responsive=$process.Responding
        runtimeVersion='10.0.12 (verified by host trace)'
        selfContainedRuntimeLoaded=$true
        skiaLoaded=$true
        webView2LoaderBundled=$true
        cleanWindowsVm=$false
        existingUserProfile=$true
    }
    if (!$process.CloseMainWindow() -or !$process.WaitForExit(10000)) { throw 'Application did not close cleanly.' }
    $report['exitCode'] = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw "Application closed with exit code $($process.ExitCode)." }
    $report | ConvertTo-Json | Set-Content (Join-Path $root 'smoke-result.json') -Encoding utf8
    $report | ConvertTo-Json | Write-Host
    Write-Host "Smoke evidence: $root"
}
finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
