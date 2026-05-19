<#
.SYNOPSIS
  Build BlockParam (TIA V20, Release) and package the .addin into $env:TEMP,
  optionally suffixed per branch so multiple builds coexist.

.DESCRIPTION
  Dev-iteration counterpart to bump-version.sh. Unlike that script it does
  NOT deploy to C:\Program Files\...\Portal V20\AddIns (admin-only) and does
  NOT touch V21 — it drops a freshly packaged .addin straight into $env:TEMP
  (root, not a subfolder) so a single deploy robocopy globbing
  BlockParam*.addin from $env:TEMP picks it up without an elevated prompt.

  With a suffix (default: the current git branch), the output file becomes
  BlockParam-<suffix>.addin and the manifest's <Product><Name>/<Id> are
  rewritten on a STAGED copy only — the tracked addin-publisher-v20.xml is
  never modified, so branches stay clean. This lets you keep several branch
  builds side by side in TEMP and install/swap whichever you want.

  CAVEAT — running two branch builds in TIA at the SAME time: a distinct file
  name + Product Id stops TIA from deduping the .addin entries, but both
  packages still ship an assembly with identity "BlockParam, <Version>".
  If two builds share that identity the CLR loads only the first and silently
  ignores the second, so you'd test the wrong code. To genuinely load two at
  once, give each a distinct -Version (cheapest) or rename the assembly
  itself (cascades into csproj AssemblyName + manifest FeatureAssembly + the
  de\ satellite path — not done here). For install-one-at-a-time iteration
  the suffix alone is enough.

.PARAMETER Version
  Optional. If given (e.g. 0.155.0), bumps <Version> in
  src\BlockParam\BlockParam.csproj and src\BlockParam\addin-publisher-v20.xml
  before building. If omitted, whatever version is already in those files is
  used as-is.

.PARAMETER Suffix
  Optional. Distinguishes the packaged file and the TIA add-in entry.
  - omitted        -> derived from the current git branch, auto-shortened to
                      the issue number when present
                      (claude/implement-issue-155-AJiG6 -> 155)
  - "none"/"plain" -> no suffix; plain BlockParam.addin (legacy behavior)
  - any string     -> used verbatim, sanitized to [A-Za-z0-9._-]

.EXAMPLE
  .\package-to-temp.ps1                       # BlockParam-<branch>.addin
  .\package-to-temp.ps1 -Suffix none          # plain BlockParam.addin
  .\package-to-temp.ps1 -Version 0.155.0 -Suffix issue155
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Position = 1)]
    [string]$Suffix
)

$ErrorActionPreference = 'Stop'

$root         = $PSScriptRoot
$csproj       = Join-Path $root 'src\BlockParam\BlockParam.csproj'
$manifestSrc  = Join-Path $root 'src\BlockParam\addin-publisher-v20.xml'
$outDir       = Join-Path $root 'src\BlockParam\bin\Release\net48'
$publisherExe = 'C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.AddIn\Siemens.Engineering.AddIn.Publisher.exe'
$targetDir    = $env:TEMP   # root, so the deploy robocopy globs it directly

if (-not (Test-Path $publisherExe)) {
    throw "Siemens Publisher not found: $publisherExe (is TIA Portal V20 installed?)"
}

# --- Resolve suffix --------------------------------------------------------
if (-not $PSBoundParameters.ContainsKey('Suffix')) {
    $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $branch) {
        # Detached HEAD / not a repo: no branch to derive from.
        $Suffix = ''
    } elseif ($branch -match '(?i)issue[-_]?(\d+)') {
        # claude/implement-issue-155-AJiG6 -> 155
        $Suffix = $Matches[1]
    } else {
        # No explicit issue token: take the last >=2-digit run so version-ish
        # noise (v21) loses to the real number (claude/implement-154-x -> 154,
        # claude/v21-build-130 -> 130). Single digits from random branch
        # suffixes (AJiG6, 0O8VT) are ignored. No number at all -> full branch.
        $nums = [regex]::Matches($branch, '\d{2,}')
        if ($nums.Count -gt 0) { $Suffix = $nums[$nums.Count - 1].Value }
        else { $Suffix = $branch }
    }
}
if ($Suffix -in @('none', 'plain')) { $Suffix = '' }
# Sanitize to a filesystem/identifier-safe token; collapse runs of separators.
$Suffix = ([regex]::Replace($Suffix, '[^A-Za-z0-9._-]+', '-')).Trim('-.')

if ($Suffix) {
    $displayName = "BlockParam ($Suffix)"
    $productId   = "BlockParam.$Suffix"
    $fileName    = "BlockParam-$Suffix.addin"
} else {
    $displayName = 'BlockParam'
    $productId   = 'BlockParam'
    $fileName    = 'BlockParam.addin'
}
$target = Join-Path $targetDir $fileName

# --- Optional version bump (BOM-free UTF-8 so we don't dirty the file) ------
if ($Version) {
    Write-Host "=== Bumping version to $Version ===" -ForegroundColor Cyan
    $enc = New-Object System.Text.UTF8Encoding($false)
    foreach ($f in @($csproj, $manifestSrc)) {
        $text = [System.IO.File]::ReadAllText($f)
        $text = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
        [System.IO.File]::WriteAllText($f, $text, $enc)
        Write-Host "  Updated $f"
    }
}

# --- Build -----------------------------------------------------------------
Write-Host '=== [V20] Building Release ===' -ForegroundColor Cyan
& dotnet build (Join-Path $root 'src\BlockParam') -c Release -p:TiaVersion=20 --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
Write-Host "  Build OK -> $outDir"

# --- Package ---------------------------------------------------------------
Write-Host "=== [V20] Packaging '$displayName' -> $fileName ===" -ForegroundColor Cyan

# Publisher reads the manifest relative to the assembly dir, so stage it there
# (same as bump-version.sh does). The Name/Id rewrite happens ONLY on this
# staged copy — the tracked manifest is never touched, keeping branches clean.
$manifestStaged = Join-Path $outDir (Split-Path $manifestSrc -Leaf)
$mtext = [System.IO.File]::ReadAllText($manifestSrc)
$mtext = $mtext.Replace('<Name>BlockParam</Name>', "<Name>$displayName</Name>")
$mtext = $mtext.Replace('<Id>BlockParam</Id>', "<Id>$productId</Id>")
[System.IO.File]::WriteAllText($manifestStaged, $mtext, (New-Object System.Text.UTF8Encoding($false)))

& $publisherExe -f $manifestStaged -o $target -c
if ($LASTEXITCODE -ne 0) { throw "Publisher failed (exit $LASTEXITCODE)" }

$size = [math]::Round((Get-Item $target).Length / 1KB)
Write-Host ''
Write-Host "=== Packaged -> $target ($size KB) ===" -ForegroundColor Green
Write-Host 'Copy it into a TIA AddIns folder to load it, then restart TIA Portal.'
