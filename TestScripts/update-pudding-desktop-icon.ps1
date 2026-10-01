# Rebuild the desktop ICO from the existing Web brand asset (no external tools).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path $PSScriptRoot
$source = [Drawing.Image]::FromFile((Join-Path $repo 'Source/PuddingPlatformAdmin/public/assets/images/logo.png'))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $frames += ,$stream.ToArray()
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $assets = Join-Path $repo 'Source/PuddingDesktop/Assets'
    New-Item -ItemType Directory -Path $assets -Force | Out-Null
    $file = [IO.File]::Create((Join-Path $assets 'Pudding.ico'))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
} finally { $source.Dispose() }
