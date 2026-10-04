#requires -Version 7.0
param([string]$OutputName = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or ![Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell 7 on Windows.' }
$repo = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'NexLauncher.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$numericVersion = [string]$project.Project.PropertyGroup.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid release version.' }
$artifacts = Join-Path $repo '.artifacts'
$run = Join-Path $artifacts ('release-builds/' + $version + '-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $run 'publish'
$payload = Join-Path $run 'payload'
if ($OutputName -and $OutputName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}$') { throw 'OutputName must be a simple directory label (letters, numbers, underscore or hyphen).' }
$output = Join-Path $artifacts ('releases/' + $(if ($OutputName) { $OutputName } else { 'v' + $version }))
$logs = Join-Path $run 'logs'
New-Item -ItemType Directory -Force $publish, $payload, $output, $logs | Out-Null
function Invoke-Dotnet([string]$Name, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs ($Name + '.log')) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $Name failed ($LASTEXITCODE). See $logs" }
}
Push-Location $repo
try {
    Invoke-Dotnet 'debug' @('build','NexLauncher.csproj','-c','Debug','--artifacts-path',$artifacts,'-warnaserror')
    Invoke-Dotnet 'release' @('build','NexLauncher.csproj','-c','Release','--artifacts-path',$artifacts,'-warnaserror')
    Invoke-Dotnet 'checks' @('run','--project','tests/NexLauncher.Checks','-c','Release','--artifacts-path',$artifacts)
    Invoke-Dotnet 'publish' @('publish','NexLauncher.csproj','-c','Release','-p:PublishProfile=Windows-x64','--artifacts-path',$artifacts,'-o',$publish,'-warnaserror')
    $files = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'NexLauncher.exe') { throw 'Publish must contain exactly one NexLauncher.exe, without loose assets, PDBs or config files.' }
    if ($files[0].Length -lt 80MB) { throw 'Unexpectedly small executable: verify the self-contained runtime.' }
    $binaryRoot = Join-Path $artifacts 'bin/NexLauncher/release_win-x64'
    $runtime = Get-Content (Join-Path $binaryRoot 'NexLauncher.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks = @($runtime.runtimeOptions.includedFrameworks)
    if ($frameworks.Count -ne 2 -or @($frameworks | Where-Object version -ne '10.0.12').Count -ne 0) { throw 'The self-contained .NET/Windows Desktop runtime version changed. Review the release profile.' }
    Copy-Item -LiteralPath $files[0].FullName -Destination $payload
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/PORTABLE-README.txt') -Destination (Join-Path $payload 'README.txt')
    & (Join-Path $PSScriptRoot 'Write-ReleaseNotices.ps1') -AssetsFile (Join-Path $artifacts 'obj/NexLauncher/project.assets.json') -DepsFile (Join-Path $binaryRoot 'NexLauncher.deps.json') -Destination $payload
    & (Join-Path $PSScriptRoot 'Get-ReleaseTools.ps1') -ToolsDirectory (Join-Path $artifacts 'tools')
    $setupArgs = @(('/DAppVersion=' + $version), ('/DNumericVersion=' + $numericVersion), ('/DPayloadDir=' + $payload), ('/DOutputDir=' + $run), ('/DWebViewBootstrapper=' + (Join-Path $artifacts 'tools/MicrosoftEdgeWebview2Setup.exe')), (Join-Path $repo 'packaging/NexLauncher.iss'))
    & (Join-Path $artifacts 'tools/InnoSetup/ISCC.exe') @setupArgs 2>&1 | Tee-Object -FilePath (Join-Path $logs 'installer.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $zipName = "NexLauncher-v$version-win-x64.zip"
    $setupName = "NexLauncher-Setup-v$version.exe"
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath (Join-Path $run $zipName) -CompressionLevel Optimal
    # Replace only these known artifact filenames after the entire build succeeds.
    Copy-Item -LiteralPath (Join-Path $run $zipName), (Join-Path $run $setupName) -Destination $output -Force
    $manifest = [ordered]@{ version=$version; runtime='10.0.12'; sdk=(& dotnet --version); builtUtc=[DateTime]::UtcNow.ToString('O'); unsigned=$true; artifacts=@() }
    $sums = foreach ($name in @($zipName, $setupName)) {
        $file = Get-Item -LiteralPath (Join-Path $output $name)
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest.artifacts += [ordered]@{ name=$name; bytes=$file.Length; sha256=$hash }
        "$hash  $name"
    }
    $manifest['webViewBootstrapperSha256'] = (Get-FileHash (Join-Path $artifacts 'tools/MicrosoftEdgeWebview2Setup.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'release-manifest.json') -Encoding utf8
    $sums | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release artifacts: $output"
    Write-Host "Build logs and unpackaged payload: $run"
}
finally { Pop-Location }
