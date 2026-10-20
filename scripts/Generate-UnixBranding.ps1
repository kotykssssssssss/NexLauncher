#requires -Version 7.0
# Reuses the existing PNG-encoded 256px ICO frame; no fonts, rendering dependency or new artwork.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$ico = [IO.File]::ReadAllBytes((Join-Path $repo 'Assets/branding/NexLauncher.ico'))
$reader = [IO.BinaryReader]::new([IO.MemoryStream]::new($ico))
try {
    $reader.BaseStream.Position = 4; $count = $reader.ReadUInt16()
    $reader.BaseStream.Position = 6 + ($count - 1) * 16 + 8
    $size = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
    [byte[]]$png = $ico[$offset..($offset + $size - 1)]
    if ([Convert]::ToHexString($png[0..7]) -ne '89504E470D0A1A0A') { throw 'Expected a PNG ICO frame.' }
    [IO.File]::WriteAllBytes((Join-Path $repo 'Assets/branding/NexLauncher.png'), $png)
    $output = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('icns'))
        foreach ($number in @(($size + 16), ($size + 8))) {
            $value = [BitConverter]::GetBytes([uint32]$number); [Array]::Reverse($value); $writer.Write($value)
            if ($number -eq $size + 16) { $writer.Write([Text.Encoding]::ASCII.GetBytes('ic08')) }
        }
        $writer.Write($png)
        [IO.File]::WriteAllBytes((Join-Path $repo 'Assets/branding/NexLauncher.icns'), $output.ToArray())
    } finally { $writer.Dispose(); $output.Dispose() }
} finally { $reader.Dispose() }
