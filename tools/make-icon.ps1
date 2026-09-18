# Builds Assets\bbspecs.ico from img\bbspecs_logo.png (multi-size, PNG-compressed entries).
# Run once, or again whenever the logo changes.
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\img\bbspecs_logo.png'),
    [string]$Output = (Join-Path $PSScriptRoot '..\src\BBSpecs\Assets\bbspecs.ico')
)

Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$outDir = Split-Path $Output -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$src = [System.Drawing.Image]::FromFile($Source)

$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
}
$src.Dispose()

$fs = [System.IO.File]::Create($Output)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([uint16]0)               # reserved
$bw.Write([uint16]1)               # type: 1 = icon
$bw.Write([uint16]$frames.Count)   # image count

# ICONDIRENTRY table
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim)          # width  (0 means 256)
    $bw.Write([byte]$dim)          # height (0 means 256)
    $bw.Write([byte]0)             # palette colours
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # colour planes
    $bw.Write([uint16]32)          # bits per pixel
    $bw.Write([uint32]$f.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}

foreach ($f in $frames) { $bw.Write($f.Bytes) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

"Wrote $Output ($([math]::Round((Get-Item $Output).Length / 1KB, 1)) KB, $($frames.Count) sizes)"
