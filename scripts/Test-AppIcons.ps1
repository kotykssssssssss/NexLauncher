#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ApplicationExe,
    [Parameter(Mandatory)][string]$SetupExe
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets/branding/NexLauncher.ico'
foreach ($path in @($ApplicationExe, $SetupExe)) {
    $resolved = (Resolve-Path -LiteralPath $path).Path
    $actualIcon = [Drawing.Icon]::ExtractAssociatedIcon($resolved)
    if (!$actualIcon) { throw "Missing executable icon: $resolved" }
    $expectedIcon = [Drawing.Icon]::new($source, $actualIcon.Width, $actualIcon.Height)
    $actual = $actualIcon.ToBitmap()
    $expected = $expectedIcon.ToBitmap()
    try {
        $differences = 0
        for ($y = 0; $y -lt $actual.Height; $y++) {
            for ($x = 0; $x -lt $actual.Width; $x++) {
                $a = $actual.GetPixel($x, $y); $b = $expected.GetPixel($x, $y)
                if ($a.A -eq 0 -and $b.A -eq 0) { continue }
                if ($a.ToArgb() -ne $b.ToArgb()) { $differences++ }
            }
        }
        if ($differences) { throw "Executable icon differs from the NexLauncher mark ($differences pixels): $resolved" }
        Write-Output "PASS: NexLauncher icon embedded in $([IO.Path]::GetFileName($resolved)) ($($actual.Width) px)"
    }
    finally { $expected.Dispose(); $actual.Dispose(); $expectedIcon.Dispose(); $actualIcon.Dispose() }
}
