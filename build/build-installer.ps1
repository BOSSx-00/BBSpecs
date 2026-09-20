<#
.SYNOPSIS
    Builds the BBSpecs installer for Windows.

.DESCRIPTION
    Compiles the single-file executable first, then wraps it in an Inno Setup
    wizard. Both end up in dist\: the portable .exe for people who want nothing
    installed, and the setup .exe for people who want a Start Menu entry, a
    fixed location and an uninstaller.

    Needs Inno Setup 6. If it isn't there:
        winget install JRSoftware.InnoSetup

.EXAMPLE
    .\build\build-installer.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',

    # Skip recompiling when dist\ already holds the right portable build.
    [switch]$SkipBuild,

    [string]$SignThumbprint = $env:BBSPECS_SIGN_THUMBPRINT,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\BBSpecs\BBSpecs.csproj'
$dist = Join-Path $root 'dist'
$script = Join-Path $PSScriptRoot 'installer.iss'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version |
           Where-Object { $_ } | Select-Object -First 1

Write-Host "BBSpecs - building installer for $version" -ForegroundColor Cyan

# Regenerate the wizard artwork, so a changed logo always reaches the installer.
$artwork = Join-Path $PSScriptRoot '..\tools\make-wizard-images.ps1'
if (Test-Path $artwork) { & $artwork | Write-Host }

# ---- the payload -----------------------------------------------------------

$portable = Join-Path $dist "BBSpecs-$version-$Runtime.exe"

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-windows.ps1') `
        -Configuration $Configuration -Runtime $Runtime `
        -SignThumbprint $SignThumbprint -TimestampUrl $TimestampUrl | Write-Host
}

if (-not (Test-Path $portable)) {
    throw "Expected $portable but it isn't there. Run without -SkipBuild."
}

# ---- the wizard ------------------------------------------------------------

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup"
}

Write-Host "  compiling the wizard..." -ForegroundColor DarkGray

& $iscc /Qp "/DAppVersion=$version" "/DSourceExe=$portable" $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }

$setup = Join-Path $dist "BBSpecs-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "Expected $setup but it wasn't produced." }

# ---- signing ---------------------------------------------------------------
#
# The installer is the file most people will download, so it is the one that
# most needs a signature: it is what SmartScreen judges and what the elevation
# prompt names a publisher from.

if ($SignThumbprint) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' `
                    -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending | Select-Object -First 1

    if (-not $signtool) {
        Write-Warning "signtool.exe not found - install the Windows SDK to sign. Shipping unsigned."
    }
    else {
        Write-Host "  signing the installer..." -ForegroundColor DarkGray
        & $signtool.FullName sign /sha1 $SignThumbprint /fd SHA256 `
            /tr $TimestampUrl /td SHA256 /d 'BBSpecs Setup' $setup
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    }
}
else {
    Write-Host "  unsigned - Windows will show 'Publisher: Unknown' on the prompt." -ForegroundColor DarkGray
}

$size = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "  Done: $setup ($size MB)" -ForegroundColor Green
Write-Host "  Installs to Program Files, adds a Start Menu entry and an uninstaller."
Write-Host "  The portable build is still at $portable."
