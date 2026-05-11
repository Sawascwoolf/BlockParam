# End-to-end review pipeline for the PillMultiSelect control:
#   1. (Re)build BlockParam.DevLauncher in Debug.
#   2. Capture employee scenes (01-02) via --capture-pill.
#   3. Capture multi-PLC DB scenes (03-07) via --capture-pill-db.
#   4. Stitch the 7 PNGs into one labeled composite.
#   5. Open the composite for review.
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
    Write-Host '[1/4] Building BlockParam.DevLauncher (Debug)...' -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'src/BlockParam.DevLauncher') -c Debug -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

if (-not (Test-Path $exe)) {
    throw "DevLauncher not found at $exe — run without -NoBuild first."
}

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}
Get-ChildItem -Path $OutDir -Filter '*.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "[2/4] Capturing pill scenes 01-02 -> $OutDir" -ForegroundColor Cyan
& $exe '--capture-pill' $OutDir
if ($LASTEXITCODE -ne 0) { throw 'Capture --capture-pill failed.' }

Write-Host "[3/4] Capturing DB scenes 03-07 -> $OutDir" -ForegroundColor Cyan
& $exe '--capture-pill-db' $OutDir
if ($LASTEXITCODE -ne 0) { throw 'Capture --capture-pill-db failed.' }

Write-Host '[4/4] Stitching composite...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'stitch-pill-screenshots.ps1') -InputDir $OutDir
if ($LASTEXITCODE -ne 0) { throw 'Stitch failed.' }

$composite = Join-Path $OutDir 'composite.png'
if (-not $NoOpen) {
    Write-Host "Opening $composite ..." -ForegroundColor Green
    Invoke-Item $composite
}
else {
    Write-Host "Done. Composite at: $composite" -ForegroundColor Green
}
