<#
  extract-timing.ps1  (DEV-ONLY — parked on chore/open-timing-instrumentation)

  Incrementally extracts SCENARIO / OPEN-TIMING lines from the BlockParam
  diagnostic logs into a sanitized, share-safe file.

  Sanitization model (defense in depth):
    * ALLOWLIST: only lines tagged SCENARIO or OPEN-TIMING are kept.
      Everything else (exception bodies, parsed-DB lines, etc.) is dropped.
    * Lines carrying real names/values (member=..., SilentUserPrompt ...)
      are dropped entirely — not needed for timing analysis.
    * db=<name> / plc=<name> are replaced with a stable salted hash so the
      same DB correlates across runs without exposing the real name.
  The raw log is never modified and never leaves the machine; only the
  sanitized output is intended to be shared.

  Usage:  pwsh ./extract-timing.ps1            # extract new lines since last run
          pwsh ./extract-timing.ps1 -Reset     # forget the cursor (re-extract all)
#>
param(
  [string]$LogDir  = "$env:APPDATA\BlockParam\logs",
  [string]$OutDir  = "$env:TEMP\BlockParam\timing-analysis",
  [switch]$Reset
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$statePath = Join-Path $OutDir 'cursor.json'
$outPath   = Join-Path $OutDir 'timing-sanitized.log'

# --- state: per-file byte offsets + a per-machine salt -----------------------
if ((Test-Path $statePath) -and -not $Reset) {
  $state = Get-Content $statePath -Raw | ConvertFrom-Json
} else {
  $state = [pscustomobject]@{ salt = [guid]::NewGuid().ToString('N'); offsets = @{} }
}
# offsets is an empty hashtable on first run, or a PSCustomObject from JSON on
# later runs — normalize both to a hashtable.
$offsets = @{}
if ($state.offsets -is [System.Collections.IDictionary]) {
  foreach ($k in @($state.offsets.Keys)) { $offsets[$k] = [int]$state.offsets[$k] }
} elseif ($state.offsets) {
  $state.offsets.PSObject.Properties | ForEach-Object { $offsets[$_.Name] = [int]$_.Value }
}
$salt = [string]$state.salt

$sha = [System.Security.Cryptography.SHA256]::Create()
function Hash([string]$v) {
  $b = [Text.Encoding]::UTF8.GetBytes($salt + '|' + $v)
  return 'h:' + (([BitConverter]::ToString($sha.ComputeHash($b)) -replace '-').Substring(0,8).ToLower())
}

$keptThisRun = New-Object System.Collections.Generic.List[string]

Get-ChildItem -Path $LogDir -Filter 'bulkchange-v*.log' -ErrorAction SilentlyContinue |
  Sort-Object Name | ForEach-Object {
    $file = $_
    $prev = if ($offsets.ContainsKey($file.Name)) { $offsets[$file.Name] } else { 0 }
    $all  = Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue
    if ($null -eq $all) { return }
    if ($all.Count -lt $prev) { $prev = 0 }   # rotated / truncated
    if ($all.Count -le $prev) { $offsets[$file.Name] = $all.Count; return }

    $all[$prev..($all.Count-1)] | ForEach-Object {
      $line = $_
      if ($line -notmatch '(SCENARIO|OPEN-TIMING)') { return }
      if ($line -match 'member=|SilentUserPrompt')  { return }
      $line = [regex]::Replace($line, '(\bdb=)(\S+)',  { param($m) $m.Groups[1].Value + (Hash $m.Groups[2].Value) })
      $line = [regex]::Replace($line, '(\bplc=)(\S+)', { param($m) $m.Groups[1].Value + (Hash $m.Groups[2].Value) })
      $keptThisRun.Add($line)
    }
    $offsets[$file.Name] = $all.Count
  }

# --- persist state + append sanitized output ---------------------------------
$state.offsets = $offsets
($state | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $statePath -Encoding utf8
if ($keptThisRun.Count -gt 0) {
  Add-Content -LiteralPath $outPath -Value $keptThisRun -Encoding utf8
}

Write-Output "=== extract-timing: $($keptThisRun.Count) new sanitized line(s) ==="
Write-Output "out:   $outPath"
Write-Output "----- new lines this run -----"
$keptThisRun | ForEach-Object { Write-Output $_ }
