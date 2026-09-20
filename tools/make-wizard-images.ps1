# Builds the installer wizard artwork from img\bbspecs_logo.png.
#
# Inno Setup ships a generic box-and-disc illustration, which says nothing about
# what is being installed. This puts the logo there instead, on the same near
# black the app itself uses, so the wizard looks like it belongs to BBSpecs.
#
# Two sizes of each are produced and both are listed in installer.iss. Inno
# picks whichever suits the display, so the artwork stays sharp on a high DPI
# screen instead of being scaled up from the 100% version.
#
# Written as 24-bit BMP rather than PNG: PNG support arrived late in Inno 6, and
# a build that works on whatever version is installed is worth more here than a
# slightly smaller file.
#
# Run once, or again whenever the logo changes. build-installer.ps1 calls it.
[CmdletBinding()]
param(
    [string]$Source,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Worked out here rather than in the param defaults: $PSScriptRoot is not
# populated in those when the script is started with powershell -File, which is
# how it gets run from anywhere other than another script.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) { $Source = Join-Path $here '..\img\bbspecs_logo.png' }

# Written under build\ rather than into the app's own Assets folder. Anything
# in there is compiled into the executable, and this artwork is an input to the
# installer: shipping it inside the program too would put the best part of a
# megabyte of pictures nobody ever sees into every download.
if (-not $OutDir) { $OutDir = Join-Path $here '..\build\wizard' }

$Source = (Resolve-Path $Source).Path
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

$background = [System.Drawing.Color]::FromArgb(11, 12, 13)      # the app's own --bg
$logo = [System.Drawing.Image]::FromFile($Source)

function Write-Panel {
    param(
        [int]$Width,
        [int]$Height,
        # How much of the shorter edge the logo should take up.
        [double]$Fill,
        # Fraction of the height the logo centre sits at. Slightly above the
        # middle looks composed; dead centre looks like a placeholder.
        [double]$Centre,
        [string]$Path
    )

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)

    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear($background)

    $side = [Math]::Round([Math]::Min($Width, $Height) * $Fill)
    $x = [Math]::Round(($Width - $side) / 2)
    $y = [Math]::Round(($Height * $Centre) - ($side / 2))

    $g.DrawImage($logo, (New-Object System.Drawing.Rectangle($x, $y, $side, $side)))
    $g.Dispose()

    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()

    $name = Split-Path $Path -Leaf
    Write-Host ("  {0,-26} {1} x {2}" -f $name, $Width, $Height)
}

Write-Host "Wizard artwork from $(Split-Path $Source -Leaf)"

# The tall panel down the left of the Welcome and Finish pages. Inno's modern
# style wants 164x314 at 100% and 328x628 at 200%.
Write-Panel -Width 164 -Height 314 -Fill 0.62 -Centre 0.42 -Path (Join-Path $OutDir 'wizard-large.bmp')
Write-Panel -Width 328 -Height 628 -Fill 0.62 -Centre 0.42 -Path (Join-Path $OutDir 'wizard-large-2x.bmp')

# The badge in the corner of every other page: 55x55 at 100%, 110x110 at 200%.
Write-Panel -Width 55 -Height 55 -Fill 0.82 -Centre 0.5 -Path (Join-Path $OutDir 'wizard-small.bmp')
Write-Panel -Width 110 -Height 110 -Fill 0.82 -Centre 0.5 -Path (Join-Path $OutDir 'wizard-small-2x.bmp')

$logo.Dispose()
Write-Host "  Done."
