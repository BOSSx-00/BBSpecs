<#
.SYNOPSIS
    Finds out why BBSpecs can't read temperatures on this machine.

.DESCRIPTION
    Runs the sensor library's own diagnostic as Administrator and saves it to
    driver-report.txt in the repository root. The "Ring0" section of that report
    says exactly why the kernel driver did or didn't load, which beats guessing
    from the outside.

    Also lists other software that takes exclusive low-level hardware access:
    motherboard vendor suites are the usual reason a sensor driver can't start.

.EXAMPLE
    .\tools\driver-report.ps1
#>
[CmdletBinding()]
param(
    [string]$Output
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'driver-report.txt' }

$project = Join-Path $root 'tools\SnapshotDump\SnapshotDump.csproj'
$exe = Join-Path $root 'tools\SnapshotDump\bin\Debug\net9.0-windows\SnapshotDump.exe'

if (-not (Test-Path $exe)) {
    Write-Host "Building the diagnostic tool..." -ForegroundColor DarkGray
    dotnet build $project -c Debug --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Couldn't build the diagnostic tool." }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    # The driver only loads for an elevated process, so the report is worthless
    # without it. Re-launch ourselves and let Windows ask.
    Write-Host "Asking for Administrator (the sensor driver needs it)..." -ForegroundColor Cyan

    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-Output', "`"$Output`""
    )
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -ArgumentList $arguments

    if (Test-Path $Output) {
        Write-Host ""
        Write-Host "Report saved to: $Output" -ForegroundColor Green
    } else {
        Write-Host "No report was produced: the elevation prompt was probably declined." -ForegroundColor Yellow
    }
    return
}

# ---- running elevated from here -------------------------------------------------

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("BBSpecs sensor driver report")
$lines.Add("Generated $(Get-Date -Format 'u')")
$lines.Add("")

$lines.Add("=== Core isolation / driver blocking ===")
$ciPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity'
try {
    $hvci = (Get-ItemProperty $ciPath -ErrorAction Stop).Enabled
    $lines.Add("Memory integrity (HVCI) enabled : $hvci   (0 = off)")
} catch { $lines.Add("Memory integrity (HVCI) enabled : not configured") }

$blPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config'
try {
    $bl = (Get-ItemProperty $blPath -ErrorAction Stop).VulnerableDriverBlocklistEnable
    $lines.Add("Vulnerable Driver Blocklist     : $bl   (1 = on)")
} catch { $lines.Add("Vulnerable Driver Blocklist     : not configured (defaults to on)") }

try { $lines.Add("Secure Boot                     : $(Confirm-SecureBootUEFI)") }
catch { $lines.Add("Secure Boot                     : could not query") }
$lines.Add("")

$lines.Add("=== Software that may be holding the hardware ===")
$rivals = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match 'Armoury|AsusFan|AISuite|atkex|GameSDK|Afterburner|Dragon_Center|MSI|aida64|HWiNFO|HWMonitor|OpenHardware|LibreHardware|RGBFusion|iCUE|Corsair|SignalRgb|ThrottleStop'
}
if ($rivals) { $rivals | ForEach-Object { $lines.Add("  $($_.Name) (pid $($_.Id))") } }
else { $lines.Add("  (none detected)") }
$lines.Add("")

$lines.Add("=== Kernel driver services ===")
$drivers = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match 'WinRing|LibreHardware|Ols|AsIO|AsUpIO|EIO|HWiNFO'
}
if ($drivers) { $drivers | ForEach-Object { $lines.Add("  $($_.Name)  state=$($_.State)  path=$($_.PathName)") } }
else { $lines.Add("  (none registered)") }
$lines.Add("")

$lines.Add("=== Recent driver blocks (CodeIntegrity 3077) ===")
try {
    $blocked = Get-WinEvent -FilterHashtable @{
        LogName = 'Microsoft-Windows-CodeIntegrity/Operational'; Id = 3077
        StartTime = (Get-Date).AddDays(-14)
    } -ErrorAction Stop
    $blocked | Select-Object -First 10 | ForEach-Object {
        $lines.Add("  $($_.TimeCreated)  $(($_.Message -replace "`r?`n", ' '))")
    }
} catch { $lines.Add("  (no blocked-driver events in the last 14 days)") }
$lines.Add("")

$lines.Add("=== Sensor library report ===")
$lines.Add("")

$lines | Set-Content -Path $Output -Encoding utf8
& $exe --report *>> $Output

Write-Host "Report written to $Output" -ForegroundColor Green
Write-Host "Look for the 'Ring0' section: it says why the driver did or didn't load."
