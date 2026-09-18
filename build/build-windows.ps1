<#
.SYNOPSIS
    Builds the downloadable BBSpecs app for Windows.

.DESCRIPTION
    Produces a single self-contained BBSpecs.exe in dist\. Nobody needs the .NET
    runtime, Visual Studio or anything else installed to run it: they download
    the one file and double-click it.

.EXAMPLE
    .\build\build-windows.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',

    # Thumbprint of a code-signing certificate in the current user's store.
    # Falls back to the BBSPECS_SIGN_THUMBPRINT environment variable so a build
    # machine can set it once. Without one the build still succeeds, unsigned.
    [string]$SignThumbprint = $env:BBSPECS_SIGN_THUMBPRINT,

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\BBSpecs\BBSpecs.csproj'
$staging = Join-Path $root "build\staging\$Runtime"
$dist = Join-Path $root 'dist'

Write-Host "BBSpecs - building for $Runtime" -ForegroundColor Cyan

# The version lives in the .csproj, so read it back rather than duplicating it.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "  version  $version"

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Regenerate the icon so a changed logo always reaches the build.
$iconScript = Join-Path $root 'tools\make-icon.ps1'
if (Test-Path $iconScript) { & $iconScript | Write-Host }

Write-Host "  publishing..." -ForegroundColor DarkGray
dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --output $staging `
    -p:BBSpecsTarget=net9.0-windows `
    --nologo `
    --verbosity minimal

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $staging 'BBSpecs.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it wasn't produced." }

$target = Join-Path $dist "BBSpecs-$version-$Runtime.exe"
Copy-Item $exe $target -Force

# A single-file publish still drops the debug symbols beside the exe; they are
# not part of what people download.
Get-ChildItem $staging -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

# ---- code signing ----------------------------------------------------------
#
# Windows takes the publisher shown on the UAC prompt from the Authenticode
# signature, not from the CompanyName in the file's metadata. An unsigned build
# reads "Publisher: Unknown" however the version resource is filled in, so the
# only way to show BOSSx there is to sign with a certificate issued to BOSSx.
#
# Signing also clears the SmartScreen "unrecognised app" warning over time,
# which matters more for a download than the UAC line does.

if ($SignThumbprint) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' `
                    -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending | Select-Object -First 1

    if (-not $signtool) {
        Write-Warning "signtool.exe not found - install the Windows SDK to sign. Shipping unsigned."
    }
    else {
        Write-Host "  signing with $SignThumbprint..." -ForegroundColor DarkGray
        & $signtool.FullName sign /sha1 $SignThumbprint /fd SHA256 `
            /tr $TimestampUrl /td SHA256 /d 'BBSpecs' $target

        if ($LASTEXITCODE -ne 0) { throw "Signing failed with exit code $LASTEXITCODE" }

        $signature = Get-AuthenticodeSignature $target
        Write-Host "  signed: $($signature.Status) - $($signature.SignerCertificate.Subject)" -ForegroundColor Green
    }
}
else {
    Write-Host "  unsigned - Windows will show 'Publisher: Unknown' on the prompt." -ForegroundColor DarkYellow
    Write-Host "  Pass -SignThumbprint, or set BBSPECS_SIGN_THUMBPRINT, to sign." -ForegroundColor DarkGray
}

$sizeMb = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host ""
Write-Host "  Done: $target ($sizeMb MB)" -ForegroundColor Green
Write-Host "  One file. No .NET install needed. Right-click it and pick 'Run as administrator'," -ForegroundColor DarkGray
Write-Host "  or just double-click and accept the prompt." -ForegroundColor DarkGray
