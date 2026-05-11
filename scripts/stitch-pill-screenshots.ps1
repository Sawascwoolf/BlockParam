# Stitches the seven PillMultiSelect capture PNGs into one composite review
# image (each scene labeled, padded, vertically stacked).
#
# Prereq: <InputDir> contains 01_pill_closed.png ... 07_db_wrap.png produced by
#   DevLauncher --capture-pill <dir>
#   DevLauncher --capture-pill-db <dir>
# (See scripts/review-pill-screenshots.ps1 for an end-to-end wrapper.)
#
# Usage:
#   pwsh -File scripts/stitch-pill-screenshots.ps1 -InputDir <dir> [-Out <out.png>]

param(
    [Parameter(Mandatory)] [string] $InputDir,
    [string] $Out = (Join-Path $InputDir 'composite.png')
)

Add-Type -AssemblyName System.Drawing

$labels = [ordered]@{
    '01_pill_closed.png'       = 'Closed pill, 3 selected employees'
    '02_pill_open.png'         = 'Open popup, 2 selected'
    '03_db_short.png'          = 'DB pill: 1 selection, full name fits'
    '04_db_chars_overflow.png' = 'DB pill: char threshold trips, switch to DB-numbers'
    '05_db_count_overflow.png' = 'DB pill: count + collapse, ends in "+N more"'
    '06_db_multi_plc.png'      = 'Two PLCs side by side'
    '07_db_wrap.png'           = 'Four PLCs, WrapPanel reflow'
}

$padX = 24
$padY = 16
$labelH = 28
$gap = 12
$bg = [System.Drawing.Color]::FromArgb(255, 246, 247, 249)
$labelBg = [System.Drawing.Color]::FromArgb(255, 232, 236, 242)
$fg = [System.Drawing.Color]::FromArgb(255, 31, 41, 55)

$images = @()
foreach ($f in $labels.Keys) {
    $p = Join-Path $InputDir $f
    if (-not (Test-Path $p)) { throw "Missing: $p" }
    $images += [pscustomobject]@{
        File  = $f
        Label = $labels[$f]
        Image = [System.Drawing.Image]::FromFile($p)
    }
}

# Use ::new() ctors throughout — `New-Object Type a, b` packs args into an
# array which fails to bind to multi-arg constructors.
$cw = [int](($images | ForEach-Object { $_.Image.Width } | Measure-Object -Maximum).Maximum)
$tw = [int]($cw + 2 * $padX)
$th = [int]$padY
foreach ($i in $images) { $th += $labelH + $i.Image.Height + $gap }
$th += $padY - $gap

$canvas = [System.Drawing.Bitmap]::new($tw, $th)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear($bg)

$font = [System.Drawing.Font]::new('Segoe UI Semibold', [single]10)
$bgBrush = [System.Drawing.SolidBrush]::new($labelBg)
$fgBrush = [System.Drawing.SolidBrush]::new($fg)

$y = $padY
foreach ($i in $images) {
    $g.FillRectangle($bgBrush, [System.Drawing.Rectangle]::new($padX, $y, $cw, $labelH))
    $g.DrawString(
        "$($i.File) - $($i.Label)",
        $font,
        $fgBrush,
        [System.Drawing.RectangleF]::new(
            [single]($padX + 10), [single]($y + 6),
            [single]($cw - 20), [single]($labelH - 12)))
    $y += $labelH

    $imgX = [int]($padX + ($cw - $i.Image.Width) / 2)
    $g.DrawImage($i.Image, $imgX, $y)
    $y += $i.Image.Height + $gap
}

$canvas.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$canvas.Dispose()
foreach ($i in $images) { $i.Image.Dispose() }

Write-Host "Composite saved: $Out ($tw x $th px)"
