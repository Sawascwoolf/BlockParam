# Stitches the thirteen PillMultiSelect capture PNGs into one composite
# review image using a masonry-style grid: each column stacks independently
# and the packer places every next scene under whichever column is currently
# shortest. This eliminates the per-row whitespace a strict left-to-right
# grid produces when short cells share a row with tall popup screenshots.
#
# Prereq: <InputDir> contains 01_pill_closed.png ... 13_pill_grouped_bundled_mixed.png
# produced by
#   DevLauncher --capture-pill <dir>
#   DevLauncher --capture-pill-db <dir>
#   DevLauncher --capture-pill-grouped <dir>
#   DevLauncher --capture-pill-grouped-bundled <dir>
# (See scripts/review-pill-screenshots.ps1 for an end-to-end wrapper.)
#
# Usage:
#   pwsh -File scripts/stitch-pill-screenshots.ps1 -InputDir <dir>
#       [-Out <out.png>] [-Columns <n>] [-CellWidth <px>]

param(
    [Parameter(Mandatory)] [string] $InputDir,
    [string] $Out = (Join-Path $InputDir 'composite.png'),
    [int] $Columns = 4,
    [int] $CellWidth = 600
)

Add-Type -AssemblyName System.Drawing

$labels = [ordered]@{
    '01_pill_closed.png'           = 'Closed pill, 3 selected employees'
    '02_pill_open.png'             = 'Open popup, 2 selected'
    '03_db_short.png'              = 'DB pill: 1 selection, full name fits'
    '04_db_chars_overflow.png'     = 'DB pill: char threshold, switch to DB-numbers'
    '05_db_count_overflow.png'     = 'DB pill: count + collapse, "+N more"'
    '06_db_multi_plc.png'          = 'Two PLCs side by side'
    '07_db_wrap.png'               = 'Four PLCs, WrapPanel reflow'
    '08_pill_grouped_open.png'     = 'Grouped popup: tri-state, checked, unchecked'
    '09_pill_grouped_collapsed.png'= 'Grouped popup: user collapsed Operations'
    '10_pill_grouped_search.png'   = 'Grouped popup: search expands collapsed group'
    '11_pill_grouped_bundled_one_group.png'  = 'Bundling: 5/5 Engineering selected -> pill shows "Engineering"'
    '12_pill_grouped_bundled_all_groups.png' = 'Bundling: all 10 selected -> pill shows three group names'
    '13_pill_grouped_bundled_mixed.png'      = 'Bundling: full group + partial group -> "Engineering, BSC, DLN"'
}

$padX = 20
$padY = 14
$labelH = 26
$gapX = 16
$gapY = 10
$bg = [System.Drawing.Color]::FromArgb(255, 246, 247, 249)
$labelBg = [System.Drawing.Color]::FromArgb(255, 232, 236, 242)
$fg = [System.Drawing.Color]::FromArgb(255, 31, 41, 55)

# Load every image and pre-compute its rendered size: clamp width to $CellWidth
# (preserving aspect ratio) so the masonry stacks cleanly without wider scenes
# punching out of their cell.
$items = @()
foreach ($f in $labels.Keys) {
    $p = Join-Path $InputDir $f
    if (-not (Test-Path $p)) { throw "Missing: $p" }
    $img = [System.Drawing.Image]::FromFile($p)

    if ($img.Width -gt $CellWidth) {
        $scale = $CellWidth / [double]$img.Width
        $renderW = $CellWidth
        $renderH = [int]([Math]::Round($img.Height * $scale))
    }
    else {
        $renderW = $img.Width
        $renderH = $img.Height
    }

    $items += [pscustomobject]@{
        File    = $f
        Label   = $labels[$f]
        Image   = $img
        RenderW = $renderW
        RenderH = $renderH
        CellH   = $labelH + $renderH
    }
}

# Masonry placement: keep a running Y for each column. For every item (in
# narrative order 01..10) pick the column whose running Y is smallest right
# now. This packs scenes tightly and keeps the four columns close in height.
$colY = New-Object 'int[]' $Columns
for ($i = 0; $i -lt $Columns; $i++) { $colY[$i] = $padY }

$placements = @()
foreach ($item in $items) {
    # Argmin over $colY.
    $bestCol = 0
    $bestY = $colY[0]
    for ($c = 1; $c -lt $Columns; $c++) {
        if ($colY[$c] -lt $bestY) {
            $bestCol = $c
            $bestY = $colY[$c]
        }
    }
    $placements += [pscustomobject]@{
        Item = $item
        Col  = $bestCol
        Y    = $bestY
    }
    $colY[$bestCol] = $bestY + $item.CellH + $gapY
}

$tw = [int](2 * $padX + $Columns * $CellWidth + ($Columns - 1) * $gapX)
$th = [int]((($colY | Measure-Object -Maximum).Maximum) + $padY - $gapY)

$canvas = [System.Drawing.Bitmap]::new($tw, $th)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear($bg)

$font = [System.Drawing.Font]::new('Segoe UI Semibold', [single]10)
$bgBrush = [System.Drawing.SolidBrush]::new($labelBg)
$fgBrush = [System.Drawing.SolidBrush]::new($fg)

foreach ($p in $placements) {
    $item = $p.Item
    $x = $padX + $p.Col * ($CellWidth + $gapX)
    $y = $p.Y

    # Label strip spans the full cell width so the grid lines stay visually aligned.
    $g.FillRectangle($bgBrush, [System.Drawing.Rectangle]::new($x, $y, $CellWidth, $labelH))
    $g.DrawString(
        "$($item.File) - $($item.Label)",
        $font,
        $fgBrush,
        [System.Drawing.RectangleF]::new(
            [single]($x + 10), [single]($y + 5),
            [single]($CellWidth - 20), [single]($labelH - 10)))

    # Image: centred horizontally inside the cell, top-aligned under the label.
    $imgX = [int]($x + ($CellWidth - $item.RenderW) / 2)
    $imgY = $y + $labelH
    $g.DrawImage($item.Image,
        [System.Drawing.Rectangle]::new($imgX, $imgY, $item.RenderW, $item.RenderH))
}

$canvas.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$canvas.Dispose()
foreach ($i in $items) { $i.Image.Dispose() }

Write-Host "Composite saved: $Out ($tw x $th px, $Columns cols x ${CellWidth}px)"
