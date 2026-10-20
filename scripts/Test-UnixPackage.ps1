#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][ValidateSet('linux-x64','osx-x64','osx-arm64')][string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($RuntimeIdentifier.StartsWith('osx-')) { & (Join-Path $PSScriptRoot 'Set-UnixZipMetadata.ps1') -ArchivePath $ArchivePath -VerifyOnly }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:apphostChecked = $false
$script:runtimeChecked = $false
function Check-Entry([string]$Name, [IO.Stream]$Data, [int]$Mode) {
    if (!$names.Add($Name) -or $Name.StartsWith('/') -or $Name.Contains('\') -or $Name.Split('/') -contains '..' -or $Name.EndsWith('.pdb')) { throw "Invalid package entry: $Name" }
    if ($Name.EndsWith('/NexLauncher')) {
        if (($Mode -band 73) -ne 73) { throw 'Executable permission missing from apphost.' }
        $header = [byte[]]::new(20); $Data.ReadExactly($header)
        if ($RuntimeIdentifier -eq 'linux-x64') {
            if ([Convert]::ToHexString($header[0..4]) -ne '7F454C4602' -or [BitConverter]::ToUInt16($header,18) -ne 62) { throw 'Expected x64 ELF apphost.' }
        } else {
            $cpu = if ($RuntimeIdentifier -eq 'osx-arm64') { 0x0100000C } else { 0x01000007 }
            if ([Convert]::ToHexString($header[0..3]) -ne 'CFFAEDFE' -or [BitConverter]::ToUInt32($header,4) -ne $cpu) { throw 'Expected matching 64-bit Mach-O apphost.' }
        }
        $script:apphostChecked = $true
    }
    if ($Name.EndsWith('/NexLauncher.runtimeconfig.json')) {
        $reader = [IO.StreamReader]::new($Data,[Text.Encoding]::UTF8,$true,4096,$true)
        try { $runtime = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $frameworks = @($runtime.runtimeOptions.includedFrameworks)
        if ($frameworks.Count -ne 1 -or $frameworks[0].name -ne 'Microsoft.NETCore.App' -or $frameworks[0].version -ne '10.0.12') { throw 'Runtime is not self-contained Unix .NET 10.0.12.' }
        $script:runtimeChecked = $true
    }
}
$stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    if ($RuntimeIdentifier.StartsWith('osx-')) {
        $archive = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read,$true)
        try {
            foreach ($entry in $archive.Entries) {
                $data = $entry.Open()
                try { Check-Entry $entry.FullName $data (($entry.ExternalAttributes -shr 16) -band 511) } finally { $data.Dispose() }
            }
        } finally { $archive.Dispose() }
        foreach ($required in @('Contents/Info.plist','Contents/MacOS/libAvaloniaNative.dylib','Contents/Resources/NexLauncher.icns','Contents/Resources/PRIVACY.txt','Contents/Resources/THIRD-PARTY-NOTICES.txt')) {
            if (!$names.Contains('NexLauncher.app/' + $required)) { throw "Missing app bundle file: $required" }
        }
    } else {
        $gzip = [IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress,$true)
        $tar = [System.Formats.Tar.TarReader]::new($gzip, $true)
        try {
            while ($null -ne ($entry = $tar.GetNextEntry())) {
                if ($entry.EntryType -ne [System.Formats.Tar.TarEntryType]::RegularFile) { throw 'Unexpected non-file tar entry.' }
                Check-Entry $entry.Name $entry.DataStream ([int]$entry.Mode)
            }
        } finally { $tar.Dispose(); $gzip.Dispose() }
        foreach ($required in @('libSkiaSharp.so','PRIVACY.txt','THIRD-PARTY-NOTICES.txt','NexLauncher.desktop','NexLauncher.png')) {
            if (!$names.Contains('NexLauncher/' + $required)) { throw "Missing Linux payload file: $required" }
        }
    }
} finally { $stream.Dispose() }
if (!$script:apphostChecked -or !$script:runtimeChecked) { throw 'Apphost/runtime checks did not run.' }
Write-Host "PASS: $RuntimeIdentifier package structure, apphost architecture, executable permissions, native resources, self-contained runtime ($($names.Count) files). Native startup not tested."
