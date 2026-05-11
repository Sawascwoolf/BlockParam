# End-to-end review pipeline for the PillMultiSelect control:
#   1. (Re)build BlockParam.DevLauncher in Debug.
#   2. Capture employee scenes (01-02) via --capture-pill.
#   3. Capture multi-PLC DB scenes (03-07) via --capture-pill-db.
#   4. Capture grouped-popup scenes (08-10) via --capture-pill-grouped.
#   5. Stitch the 10 PNGs into one labeled composite.
#   6. Open the composite for review.
#
# Designed for "I changed the control, show me what it looks like now" loops.
# Output goes to a temp dir by default so canonical screenshots in
# assets/screenshots/pill/ are not overwritten by accident.
#
# Usage:
#   pwsh -File scripts/review-pill-screenshots.ps1 [-OutDir <dir>] [-NoBuild] [-NoOpen]

param(
    [string] $OutDir = (Join-Path $env:TEMP 'pill-review'),
    [switch] $NoBuild,
    [switch] $NoOpen
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$exe = Join-Path $repoRoot 'src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe'

if (-not $NoBuild) {
    Write-Host '[1/5] Building BlockParam.DevLauncher (Debug)...' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'src/BlockParam.DevLauncher') -c Debug -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

if (-not (Test-Path $exe)) {
    throw "DevLauncher not found at $exe - run without -NoBuild first."
}

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}
Get-ChildItem -Path $OutDir -Filter '*.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force

# Use Start-Process -Wait so the script blocks until each WPF capture exits.
# Plain `& $exe args` does not reliably wait for GUI apps under Windows
# PowerShell 5.1, which causes the captures to run concurrently and the
# stitch step to fire before any PNGs are on disk.
function Invoke-DevLauncher([string]$flag, [string]$dir) {
    $p = Start-Process -FilePath $exe -ArgumentList $flag, $dir -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "DevLauncher $flag exited with $($p.ExitCode)." }
}

Write-Host "[2/5] Capturing pill scenes 01-02 -> $OutDir" -ForegroundColor Cyan
Invoke-DevLauncher '--capture-pill' $OutDir

Write-Host "[3/5] Capturing DB scenes 03-07 -> $OutDir" -ForegroundColor Cyan
Invoke-DevLauncher '--capture-pill-db' $OutDir

Write-Host "[4/5] Capturing grouped scenes 08-10 -> $OutDir" -ForegroundColor Cyan
Invoke-DevLauncher '--capture-pill-grouped' $OutDir

Write-Host '[5/5] Stitching composite...' -ForegroundColor Cyan
# Stitch is a pure-PowerShell script; it throws on failure rather than
# setting $LASTEXITCODE. Don't read $LASTEXITCODE here - it carries over
# from the last external EXE invocation and would spuriously flag success.
& (Join-Path $PSScriptRoot 'stitch-pill-screenshots.ps1') -InputDir $OutDir

$composite = Join-Path $OutDir 'composite.png'
if (-not $NoOpen) {
    Write-Host "Opening $composite ..." -ForegroundColor Green
    Invoke-Item $composite
}
else {
    Write-Host "Done. Composite at: $composite" -ForegroundColor Green
}
