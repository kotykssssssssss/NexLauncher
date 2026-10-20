#requires -Version 7.0
param([Parameter(Mandatory)][string]$ArchivePath, [switch]$VerifyOnly)
# .NET on Windows writes ZIP origin=MS-DOS even with Unix mode attributes.
# Mark our newly built ZIP's central-directory entries as Unix so extractors
# actually honor those modes. Compressed files and their CRCs are untouched.
$ErrorActionPreference = 'Stop'
$access = if ($VerifyOnly) { [IO.FileAccess]::Read } else { [IO.FileAccess]::ReadWrite }
$file = [IO.File]::Open((Resolve-Path -LiteralPath $ArchivePath).Path,[IO.FileMode]::Open,$access,[IO.FileShare]::None)
$reader = [IO.BinaryReader]::new($file,[Text.Encoding]::UTF8,$true)
try {
    if ($file.Length -lt 22 -or $file.Length -gt [uint32]::MaxValue) { throw 'Expected a non-Zip64 release archive.' }
    $file.Position = $file.Length - 22
    if ($reader.ReadUInt32() -ne 0x06054b50 -or $reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 0) { throw 'Unexpected ZIP end record.' }
    $diskCount = $reader.ReadUInt16(); $count = $reader.ReadUInt16()
    $size = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
    if ($reader.ReadUInt16() -ne 0 -or $count -eq 0 -or $count -eq 65535 -or $count -ne $diskCount -or [long]$offset + $size -ne $file.Length - 22) { throw 'Unsupported ZIP directory layout.' }
    $position = [long]$offset
    for ($index = 0; $index -lt $count; $index++) {
        $file.Position = $position
        if ($reader.ReadUInt32() -ne 0x02014b50) { throw 'Invalid ZIP central-directory entry.' }
        $null = $reader.ReadByte(); $origin = $reader.ReadByte()
        if ($VerifyOnly) {
            if ($origin -ne 3) { throw 'ZIP creator must be Unix for executable modes to survive extraction.' }
        } else { $file.Position = $position + 5; $file.WriteByte(3) }
        $file.Position = $position + 28
        $name = $reader.ReadUInt16(); $extra = $reader.ReadUInt16(); $comment = $reader.ReadUInt16()
        $position += 46 + $name + $extra + $comment
        if ($position -gt [long]$offset + $size) { throw 'ZIP directory is out of bounds.' }
    }
    if ($position -ne [long]$offset + $size) { throw 'ZIP directory size mismatch.' }
    if (!$VerifyOnly) { $file.Flush($true) }
    Write-Host "PASS: Unix ZIP origin metadata ($count entries)."
} finally { $reader.Dispose(); $file.Dispose() }
