<#
.SYNOPSIS
    Draws the application icons for the four Shubbak executables.

.DESCRIPTION
    The binaries shipped with no icon at all, which Alt-Tab, the taskbar, Task
    Manager's Startup tab and any shortcut an installer creates all render as the
    generic placeholder. That is the first thing a user sees of a window manager, and
    it looked unfinished.

    The icons are drawn rather than committed as opaque binaries so that a change is a
    diff somebody can read and argue with, in keeping with everything else here. Run
    this after editing, and commit the .ico files it writes.

        pwsh tools/make-icons.ps1

    Each icon is written as a Vista-style ICO: a directory followed by PNG-compressed
    images at 16, 32, 48, 64, 128 and 256 pixels. PNG entries rather than DIB entries
    because they carry a real alpha channel, which is what stops the corners from
    being a grey box on a dark taskbar.

.NOTES
    The glyphs are deliberately simple. They have to survive being drawn at 16 pixels
    in a taskbar, where anything with detail becomes a smudge.
#>

[CmdletBinding()]
param(
    # Where the .ico files go. Defaults to the icons folder beside the sources.
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\src\icons')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# The sizes Windows actually asks for. 256 is what the "extra large icons" view and
# most installers use; 16 is the taskbar and the title bar.
$Sizes = @(16, 32, 48, 64, 128, 256)

function New-RoundedPath {
    param(
        [float] $X, [float] $Y, [float] $Width, [float] $Height, [float] $Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2

    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    return $path
}

function New-Canvas {
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    return @{ Bitmap = $bitmap; Graphics = $g }
}

<#
    A rounded tile with a coloured field, used as the backdrop of every icon so the
    four read as one family at a glance.
#>
function Add-Backdrop {
    param($Graphics, [int] $Size, [string] $Top, [string] $Bottom)

    $inset = $Size * 0.05
    $side = $Size - ($inset * 2)
    $radius = $Size * 0.22

    $path = New-RoundedPath -X $inset -Y $inset -Width $side -Height $side -Radius $radius

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $Size)),
        [System.Drawing.ColorTranslator]::FromHtml($Top),
        [System.Drawing.ColorTranslator]::FromHtml($Bottom))

    $Graphics.FillPath($brush, $path)

    $brush.Dispose()
    $path.Dispose()
}

<#
    Shubbak: the window manager, and the CLI that drives it.

    A tiling layout - one tall pane and two stacked beside it - which is the arrangement
    every screenshot of a tiling window manager opens with, and is legible at 16 pixels
    because it is three rectangles.
#>
function Add-TilesGlyph {
    param($Graphics, [int] $Size)

    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))

    $margin = $Size * 0.24
    $field = $Size - ($margin * 2)
    $gap = [Math]::Max(1.0, $Size * 0.05)
    $radius = [Math]::Max(1.0, $Size * 0.035)

    $leftWidth = ($field * 0.5) - ($gap / 2)
    $rightWidth = $field - $leftWidth - $gap
    $rightHeight = ($field * 0.5) - ($gap / 2)

    $panes = @(
        @{ X = $margin;                        Y = $margin;                          W = $leftWidth;  H = $field }
        @{ X = $margin + $leftWidth + $gap;    Y = $margin;                          W = $rightWidth; H = $rightHeight }
        @{ X = $margin + $leftWidth + $gap;    Y = $margin + $rightHeight + $gap;    W = $rightWidth; H = $rightHeight }
    )

    foreach ($pane in $panes) {
        $path = New-RoundedPath -X $pane.X -Y $pane.Y -Width $pane.W -Height $pane.H -Radius $radius
        $Graphics.FillPath($brush, $path)
        $path.Dispose()
    }

    $brush.Dispose()
}

<#
    Taj - the crown the bar is named for.

    Drawn as a filled polygon rather than an outline: at 16 pixels an outlined crown
    closes up into a blob, and a solid one keeps its silhouette.
#>
function Add-CrownGlyph {
    param($Graphics, [int] $Size)

    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))

    $left = $Size * 0.22
    $right = $Size * 0.78
    $top = $Size * 0.30
    $bottom = $Size * 0.66
    $middle = ($left + $right) / 2
    $dip = $top + (($bottom - $top) * 0.42)

    # Each coordinate is computed before it is used in the PointF argument list.
    # Inline arithmetic there is a trap: PowerShell binds the comma tighter than the
    # division, so `(a + b) / 2, $c` divides by the array @(2, $c) and fails with a
    # missing op_Division rather than anything that names the real problem.
    $peakY = $top - ($Size * 0.04)
    $leftSpikeX = ($left + $middle) / 2
    $rightSpikeX = ($right + $middle) / 2

    $points = @(
        (New-Object System.Drawing.PointF($left, $bottom)),
        (New-Object System.Drawing.PointF($left, $top)),
        (New-Object System.Drawing.PointF($leftSpikeX, $dip)),
        (New-Object System.Drawing.PointF($middle, $peakY)),
        (New-Object System.Drawing.PointF($rightSpikeX, $dip)),
        (New-Object System.Drawing.PointF($right, $top)),
        (New-Object System.Drawing.PointF($right, $bottom))
    )

    $Graphics.FillPolygon($brush, [System.Drawing.PointF[]] $points)

    # The band, set off by a transparent gap so the crown does not become one mass.
    $bandTop = $bottom + ($Size * 0.06)
    $bandHeight = $Size * 0.10
    $path = New-RoundedPath -X $left -Y $bandTop -Width ($right - $left) -Height $bandHeight `
        -Radius ([Math]::Max(1.0, $Size * 0.03))
    $Graphics.FillPath($brush, $path)

    $path.Dispose()
    $brush.Dispose()
}

<#
    Dalil - the guide, so a magnifying glass.

    The ring is stroked and the handle is a rounded line; both scale down without
    filling in, which a lens drawn as two filled circles does not.
#>
function Add-SearchGlyph {
    param($Graphics, [int] $Size)

    $colour = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $thickness = [Math]::Max(1.5, $Size * 0.09)

    $pen = New-Object System.Drawing.Pen($colour, $thickness)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $diameter = $Size * 0.42
    $x = $Size * 0.22
    $y = $Size * 0.22

    $Graphics.DrawEllipse($pen, $x, $y, $diameter, $diameter)

    $from = $x + ($diameter * 0.85)
    $to = $Size * 0.78
    $Graphics.DrawLine($pen, $from, $from, $to, $to)

    $pen.Dispose()
}

<#
    Assembles PNG frames into an ICO.

    The format is a six-byte directory header, then one sixteen-byte entry per image,
    then the image data. A dimension of 256 is stored as zero, which is the whole
    reason the format tops out there.
#>
function Write-Icon {
    param([hashtable] $Frames, [string] $Path)

    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)

    try {
        $writer.Write([UInt16] 0)                 # reserved
        $writer.Write([UInt16] 1)                 # 1 = icon
        $writer.Write([UInt16] $Sizes.Count)

        # Data begins after the header and the whole directory.
        $offset = 6 + (16 * $Sizes.Count)

        foreach ($size in $Sizes) {
            $bytes = $Frames[$size]

            $writer.Write([Byte] ($(if ($size -ge 256) { 0 } else { $size })))
            $writer.Write([Byte] ($(if ($size -ge 256) { 0 } else { $size })))
            $writer.Write([Byte] 0)               # palette entries; 0 for truecolour
            $writer.Write([Byte] 0)               # reserved
            $writer.Write([UInt16] 1)             # colour planes
            $writer.Write([UInt16] 32)            # bits per pixel
            $writer.Write([UInt32] $bytes.Length)
            $writer.Write([UInt32] $offset)

            $offset += $bytes.Length
        }

        foreach ($size in $Sizes) {
            $writer.Write($Frames[$size])
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Build-Icon {
    param(
        [string] $Name,
        [string] $Top,
        [string] $Bottom,
        [scriptblock] $Glyph
    )

    $frames = @{}

    foreach ($size in $Sizes) {
        $canvas = New-Canvas -Size $size

        try {
            Add-Backdrop -Graphics $canvas.Graphics -Size $size -Top $Top -Bottom $Bottom
            & $Glyph $canvas.Graphics $size

            $memory = New-Object System.IO.MemoryStream
            $canvas.Bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames[$size] = $memory.ToArray()
            $memory.Dispose()
        }
        finally {
            $canvas.Graphics.Dispose()
            $canvas.Bitmap.Dispose()
        }
    }

    $path = Join-Path $OutputDirectory "$Name.ico"
    Write-Icon -Frames $frames -Path $path

    $size = (Get-Item $path).Length
    Write-Output ("  {0,-14} {1,7:N0} bytes" -f "$Name.ico", $size)
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

Write-Output "Writing icons to $OutputDirectory"

# The window manager and its CLI share a glyph and differ in shade: they are the same
# program from the user's point of view, and two unrelated icons would suggest
# otherwise. The daemon takes the stronger colour because it is the one that appears
# in Task Manager and in the Startup tab.
Build-Icon -Name 'shubbak-wm' -Top '#2E7D8F' -Bottom '#1B4A57' -Glyph ${function:Add-TilesGlyph}
Build-Icon -Name 'shubbak'    -Top '#4A5A63' -Bottom '#2B3940' -Glyph ${function:Add-TilesGlyph}
Build-Icon -Name 'taj'        -Top '#C99A2E' -Bottom '#8A6416' -Glyph ${function:Add-CrownGlyph}
Build-Icon -Name 'dalil'      -Top '#4F7BA8' -Bottom '#2C4A68' -Glyph ${function:Add-SearchGlyph}

Write-Output "Done."
