param(
    [string]$ExtensionRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$extensionRootFull = [IO.Path]::GetFullPath($ExtensionRoot)
$iconsDir = Join-Path $extensionRootFull "icons"
$storeAssetsDir = Join-Path $extensionRootFull "store\assets"
New-Item -ItemType Directory -Path $iconsDir -Force | Out-Null
New-Item -ItemType Directory -Path $storeAssetsDir -Force | Out-Null

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )
    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-BrandMark {
    param(
        [System.Drawing.Graphics]$Graphics,
        [float]$X,
        [float]$Y,
        [float]$Size
    )
    $scale = $Size / 128.0
    $background = New-RoundedRectanglePath `
        ([System.Drawing.RectangleF]::new($X + 16 * $scale, $Y + 16 * $scale, 96 * $scale, 96 * $scale)) `
        (22 * $scale)
    $darkBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 16, 17, 20))
    $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 245, 158, 11), [Math]::Max(1.2, 2.5 * $scale))
    $Graphics.FillPath($darkBrush, $background)
    $Graphics.DrawPath($borderPen, $background)

    $orbBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 158, 11))
    $Graphics.FillEllipse(
        $orbBrush,
        $X + 32 * $scale,
        $Y + 33 * $scale,
        48 * $scale,
        48 * $scale)

    $whitePen = [System.Drawing.Pen]::new(
        [System.Drawing.Color]::FromArgb(245, 255, 255, 255),
        [Math]::Max(1.4, 4.2 * $scale))
    $whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($line in @(
        @(43, 58, 43, 68),
        @(52, 52, 52, 74),
        @(61, 56, 61, 70)
    )) {
        $Graphics.DrawLine(
            $whitePen,
            $X + $line[0] * $scale,
            $Y + $line[1] * $scale,
            $X + $line[2] * $scale,
            $Y + $line[3] * $scale)
    }

    $cursor = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $cursor.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($X + 68 * $scale, $Y + 66 * $scale),
        [System.Drawing.PointF]::new($X + 96 * $scale, $Y + 79 * $scale),
        [System.Drawing.PointF]::new($X + 83 * $scale, $Y + 84 * $scale),
        [System.Drawing.PointF]::new($X + 90 * $scale, $Y + 98 * $scale),
        [System.Drawing.PointF]::new($X + 82 * $scale, $Y + 102 * $scale),
        [System.Drawing.PointF]::new($X + 75 * $scale, $Y + 87 * $scale),
        [System.Drawing.PointF]::new($X + 65 * $scale, $Y + 96 * $scale)
    ))
    $cursor.CloseFigure()
    $cursorBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $cursorOutline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 16, 17, 20), [Math]::Max(1, 2 * $scale))
    $Graphics.FillPath($cursorBrush, $cursor)
    $Graphics.DrawPath($cursorOutline, $cursor)

    $cursorOutline.Dispose()
    $cursorBrush.Dispose()
    $cursor.Dispose()
    $whitePen.Dispose()
    $orbBrush.Dispose()
    $borderPen.Dispose()
    $darkBrush.Dispose()
    $background.Dispose()
}

function Write-Icon {
    param([int]$Size)
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    Draw-BrandMark $graphics 0 0 $Size
    $path = Join-Path $iconsDir "icon-$Size.png"
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
}

foreach ($size in @(16, 32, 48, 128)) {
    Write-Icon $size
}
Copy-Item `
    -LiteralPath (Join-Path $iconsDir "icon-128.png") `
    -Destination (Join-Path $storeAssetsDir "icon-128.png") `
    -Force

$promo = [System.Drawing.Bitmap]::new(440, 280)
$promoGraphics = [System.Drawing.Graphics]::FromImage($promo)
$promoGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$promoGraphics.Clear([System.Drawing.Color]::FromArgb(255, 16, 17, 20))

$glowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(36, 245, 158, 11))
$promoGraphics.FillEllipse($glowBrush, -70, -120, 330, 330)
$promoGraphics.FillEllipse($glowBrush, 265, 115, 250, 250)
$glowBrush.Dispose()

$windowPath = New-RoundedRectanglePath ([System.Drawing.RectangleF]::new(177, 54, 214, 150)) 14
$windowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 31, 33, 39))
$windowPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 74, 77, 87), 2)
$promoGraphics.FillPath($windowBrush, $windowPath)
$promoGraphics.DrawPath($windowPen, $windowPath)
$linePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 74, 77, 87), 2)
$promoGraphics.DrawLine($linePen, 177, 84, 391, 84)
foreach ($x in @(192, 204, 216)) {
    $promoGraphics.FillEllipse(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 92, 96, 108)),
        $x,
        67,
        7,
        7)
}
$highlightPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 245, 158, 11), 4)
$promoGraphics.DrawRectangle($highlightPen, 219, 114, 122, 42)
$controlBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 56, 59, 68))
$promoGraphics.FillRectangle($controlBrush, 230, 126, 100, 18)

Draw-BrandMark $promoGraphics 35 70 128

$controlBrush.Dispose()
$highlightPen.Dispose()
$linePen.Dispose()
$windowPen.Dispose()
$windowBrush.Dispose()
$windowPath.Dispose()
$promo.Save(
    (Join-Path $storeAssetsDir "small-promo-440x280.png"),
    [System.Drawing.Imaging.ImageFormat]::Png)
$promoGraphics.Dispose()
$promo.Dispose()

Write-Host "Generated extension icons in $iconsDir"
Write-Host "Generated Store promo image in $storeAssetsDir"
