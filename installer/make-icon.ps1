<#
.SYNOPSIS
    Renders assets\Hearth.ico: a warm gradient tile with a flame.
    Run once when the icon should change; the .ico is committed.
#>
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
$out = Join-Path (Split-Path $PSScriptRoot) 'assets\Hearth.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Render([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $pad = [Math]::Max(0.5, $size * 0.04)
    $rect = New-Object System.Windows.Rect $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $radius = $size * 0.24
    $fill = New-Object System.Windows.Media.LinearGradientBrush (
        [System.Windows.Media.Color]::FromRgb(0xFF, 0xB3, 0x47)),
        ([System.Windows.Media.Color]::FromRgb(0xE8, 0x45, 0x1E)), 90
    $dc.DrawRoundedRectangle($fill, $null, $rect, $radius, $radius)

    $glyph = [string][char]0xECAD
    $typeface = New-Object System.Windows.Media.Typeface 'Segoe Fluent Icons'
    $text = New-Object System.Windows.Media.FormattedText $glyph,
        ([Globalization.CultureInfo]::InvariantCulture),
        ([System.Windows.FlowDirection]::LeftToRight), $typeface, ($size * 0.62),
        ([System.Windows.Media.Brushes]::White), 1.0
    $origin = New-Object System.Windows.Point (($size - $text.Width) / 2), (($size - $text.Height) / 2)
    $dc.DrawText($text, $origin)
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

# ICO: header, one directory entry per size, then PNG images.
$images = foreach ($s in $sizes) { , (Render $s) }
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$data.Length); $writer.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $writer.Write($data) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($out, $file.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path (Split-Path $out) 'Hearth-256.png'), $images[-1])
"Wrote $out"
