param(
    [Parameter(Mandatory)][string]$AssetsFile,
    [Parameter(Mandatory)][string]$DepsFile,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
$deps = Get-Content -LiteralPath $DepsFile -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$text = [System.Text.StringBuilder]::new()
[void]$text.AppendLine("NexLauncher third-party notices`r`nGenerated from the published runtime dependency graph. Copyrights remain with their respective owners.`r`n")
$packages = @()
foreach ($entry in $deps.libraries.PSObject.Properties | Sort-Object Name) {
    if ($entry.Value.type -eq 'project') { continue }
    $identity = $entry.Name -replace '^runtimepack\.', ''
    $packages += $identity
    $packageDirectory = $null
    foreach ($root in $packageRoots) {
        $candidate = Join-Path $root $identity.ToLowerInvariant()
        if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
    }
    if (!$packageDirectory) { throw "Package missing from restored cache: $identity" }
    [void]$text.AppendLine("`r`n=== $identity ===")
    $legalFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object Name -Match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD-PARTY-NOTICES)(\.|$)')
    foreach ($file in $legalFiles) {
        [void]$text.AppendLine("`r`n--- $($file.Name) ---")
        [void]$text.AppendLine((Get-Content -LiteralPath $file.FullName -Raw))
    }
    # Some NuGets publish only an SPDX expression. Their actual upstream notices are vendored.
    $sourceName = switch -Regex ($identity) {
        '^Avalonia(?:\.[^/]+)?/12\.1\.0$' { 'Avalonia-12.1.0-*'; break }
        '^XboxAuthNet.Game/1\.4\.1$' { 'CmlLib.Core.Auth.Microsoft-3.3.1-*'; break }
        default { ($identity -replace '/', '-') + '-*' }
    }
    $upstream = @(Get-ChildItem -LiteralPath (Join-Path $repo 'packaging/licenses') -Filter $sourceName -File)
    foreach ($file in $upstream) { [void]$text.AppendLine((Get-Content -LiteralPath $file.FullName -Raw)) }
    if ($legalFiles.Count -eq 0 -and $upstream.Count -eq 0) { throw "Missing redistribution notice for $identity. Review the new dependency." }
}
[void]$text.AppendLine((Get-Content -LiteralPath (Join-Path $repo 'packaging/licenses/Inter-OFL.txt') -Raw))
[System.IO.File]::WriteAllText((Join-Path $Destination 'THIRD-PARTY-NOTICES.txt'), $text.ToString())
$packages | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'DEPENDENCIES.json') -Encoding utf8
