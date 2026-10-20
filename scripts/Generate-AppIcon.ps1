#requires -Version 7.0
# Rebuilds the established NexLauncher N mark without fonts or external tools.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$repo = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $repo 'Assets/branding'
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $letter = [Drawing.Drawing2D.GraphicsPath]::new()
    $mint = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#78d9b3'))
    $ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#103429'))
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 256.0, $size / 256.0)
        $shape.AddArc(8,8,120,120,180,90); $shape.AddArc(128,8,120,120,270,90)
        $shape.AddArc(128,128,120,120,0,90); $shape.AddArc(8,128,120,120,90,90); $shape.CloseFigure()
        $graphics.FillPath($mint, $shape)
        $points = @(@(78,68),@(103,68),@(156,148),@(156,68),@(180,68),@(180,188),@(155,188),@(102,108),@(102,188),@(78,188))
        $letter.AddPolygon([Drawing.PointF[]]@($points | ForEach-Object { [Drawing.PointF]::new($_[0], $_[1]) }))
        $graphics.FillPath($ink, $letter)
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
    }
    finally { $stream.Dispose(); $ink.Dispose(); $mint.Dispose(); $letter.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$output = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    [IO.File]::WriteAllBytes((Join-Path $folder 'NexLauncher.ico'), $output.ToArray())
}
finally { $writer.Dispose(); $output.Dispose() }
