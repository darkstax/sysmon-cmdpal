# Generate high-quality system monitor icons at all required sizes.
# Run: pwsh -File generate_icons.ps1
# Requires: .NET with System.Drawing.Common

param(
    [string]$OutputDir = $PSScriptRoot
)

Add-Type -AssemblyName System.Drawing

# Color palette — modern dark theme
$accent    = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)
$green     = [System.Drawing.Color]::FromArgb(255, 16, 185, 129)
$yellow    = [System.Drawing.Color]::FromArgb(255, 245, 158, 11)
$red       = [System.Drawing.Color]::FromArgb(255, 239, 68, 68)
$white     = [System.Drawing.Color]::FromArgb(255, 248, 250, 252)
$darkBg    = [System.Drawing.Color]::FromArgb(255, 15, 23, 42)
$darkBg2   = [System.Drawing.Color]::FromArgb(255, 30, 41, 59)
$subtle    = [System.Drawing.Color]::FromArgb(200, 148, 163, 184)
$whiteFade = [System.Drawing.Color]::FromArgb(40, 255, 255, 255)
$ringFade  = [System.Drawing.Color]::FromArgb(60, 255, 255, 255)

# ============================================================
# Helper: create bitmap + graphics with antialiasing
# ============================================================
function New-Drawing($w, $h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @{ Bitmap = $bmp; Graphics = $g }
}

function Cleanup-Drawing($res) {
    $res.Graphics.Dispose()
    $res.Bitmap.Dispose()
}

# ============================================================
# Helper: draw gauge icon at (cx, cy) with given radius
# ============================================================
function Draw-GaugeIcon($g, $cx, $cy, $radius) {
    # Subtle background circle
    $bg = New-Object System.Drawing.SolidBrush($whiteFade)
    $g.FillEllipse($bg, $cx-$radius, $cy-$radius, $radius*2, $radius*2)
    $bg.Dispose()

    # Outer ring
    $ringW = [Math]::Max(3, $radius * 0.06)
    $ringPen = New-Object System.Drawing.Pen($ringFade, $ringW)
    $g.DrawEllipse($ringPen, $cx-$radius+$ringW, $cy-$radius+$ringW, ($radius-$ringW)*2, ($radius-$ringW)*2)
    $ringPen.Dispose()

    # Colored arcs (green/yellow/red for 0-40%/40-80%/80-100%)
    $innerR = $radius * 0.75
    $cw = $innerR * 0.22

    $greenPen = New-Object System.Drawing.Pen($green, $cw)
    $greenPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $greenPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($greenPen, $cx-$innerR, $cy-$innerR, $innerR*2, $innerR*2, 135, 180)

    $yellowPen = New-Object System.Drawing.Pen($yellow, $cw)
    $yellowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $yellowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($yellowPen, $cx-$innerR, $cy-$innerR, $innerR*2, $innerR*2, -45, 90)

    $redPen = New-Object System.Drawing.Pen($red, $cw)
    $redPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $redPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($redPen, $cx-$innerR, $cy-$innerR, $innerR*2, $innerR*2, 45, 90)

    $greenPen.Dispose(); $yellowPen.Dispose(); $redPen.Dispose()

    # Needle pointing ~30% (225°)
    $needleLen = $innerR * 0.72
    $needleW = [Math]::Max(2, $radius * 0.04)
    $angle = 225 * [Math]::PI / 180
    $nx = $cx + [Math]::Cos($angle) * $needleLen
    $ny = $cy + [Math]::Sin($angle) * $needleLen
    $needlePen = New-Object System.Drawing.Pen($white, $needleW)
    $needlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($needlePen, $cx, $cy, $nx, $ny)
    $needlePen.Dispose()

    # Center dot
    $dotR = $innerR * 0.12
    $dotBrush = New-Object System.Drawing.SolidBrush($white)
    $g.FillEllipse($dotBrush, $cx-$dotR, $cy-$dotR, $dotR*2, $dotR*2)
    $dotBrush.Dispose()
}

# ============================================================
# Helper: downscale and save
# ============================================================
function Save-ScaledCopy($sourcePath, $targetPath, $w, $h) {
    $src = [System.Drawing.Bitmap]::FromFile($sourcePath)
    $dst = New-Object System.Drawing.Bitmap($src, $w, $h)
    $dst.Save($targetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $src.Dispose(); $dst.Dispose()
}

# ============================================================
# Helper: draw centered text
# ============================================================
function Draw-CenteredText($g, $text, $font, $color, $cx, $cy) {
    $brush = New-Object System.Drawing.SolidBrush($color)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($text, $font, $brush, $cx, $cy, $sf)
    $brush.Dispose()
}

# ============================================================
# 1. StoreLogo.png (300x300)
# ============================================================
Write-Host "Generating StoreLogo.png (300x300)..." -ForegroundColor Cyan
$r = New-Drawing 300 300

$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point(300, 300)),
    $darkBg2, $darkBg
)
$r.Graphics.FillEllipse($bgBrush, 15, 15, 270, 270)
$bgBrush.Dispose()

$ringPen = New-Object System.Drawing.Pen($accent, 8)
$r.Graphics.DrawEllipse($ringPen, 20, 20, 260, 260)
$ringPen.Dispose()

$cx=$cy=150; $r2=100
$greenBrush = New-Object System.Drawing.SolidBrush($green)
$yellowBrush = New-Object System.Drawing.SolidBrush($yellow)
$redBrush = New-Object System.Drawing.SolidBrush($red)
$r.Graphics.FillPie($greenBrush, $cx-$r2, $cy-$r2, $r2*2, $r2*2, 135, 180)
$r.Graphics.FillPie($yellowBrush, $cx-$r2, $cy-$r2, $r2*2, $r2*2, 315, 90)
$r.Graphics.FillPie($redBrush, $cx-$r2, $cy-$r2, $r2*2, $r2*2, 45, 90)
$greenBrush.Dispose(); $yellowBrush.Dispose(); $redBrush.Dispose()

$innerBrush = New-Object System.Drawing.SolidBrush($darkBg)
$r.Graphics.FillEllipse($innerBrush, $cx-65, $cy-65, 130, 130)
$innerBrush.Dispose()

$needlePen = New-Object System.Drawing.Pen($white, 4)
$needlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
$r.Graphics.DrawLine($needlePen, $cx, $cy+30, $cx-40, $cy-60)
$needlePen.Dispose()

$dotBrush = New-Object System.Drawing.SolidBrush($white)
$r.Graphics.FillEllipse($dotBrush, $cx-8, $cy-8, 16, 16)
$dotBrush.Dispose()

$font = New-Object System.Drawing.Font("Segoe UI", 28, [System.Drawing.FontStyle]::Bold)
Draw-CenteredText $r.Graphics "SYS" $font $white $cx $cy
$font.Dispose()

$r.Bitmap.Save("$OutputDir\StoreLogo.png", [System.Drawing.Imaging.ImageFormat]::Png)
Cleanup-Drawing $r
Write-Host "  -> StoreLogo.png OK" -ForegroundColor Green

# ============================================================
# 2. Square150x150Logo (scale-200 = 300x300, scale-100 = 150x150)
# ============================================================
Write-Host "Generating Square150x150Logo..." -ForegroundColor Cyan

$r = New-Drawing 300 300
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point(300, 300)),
    $darkBg2, $darkBg
)
$r.Graphics.FillRectangle($bgBrush, 0, 0, 300, 300)
$bgBrush.Dispose()

Draw-GaugeIcon $r.Graphics 150 130 85

$font = New-Object System.Drawing.Font("Segoe UI", 22, [System.Drawing.FontStyle]::Bold)
Draw-CenteredText $r.Graphics "SYS" $font $white 150 240
$font.Dispose()

$r.Bitmap.Save("$OutputDir\Square150x150Logo.scale-200.png", [System.Drawing.Imaging.ImageFormat]::Png)
Cleanup-Drawing $r
Save-ScaledCopy "$OutputDir\Square150x150Logo.scale-200.png" "$OutputDir\Square150x150Logo.png" 150 150
Write-Host "  -> Square150x150Logo OK" -ForegroundColor Green

# ============================================================
# 3. Square44x44Logo (scale-200 = 88x88, scale-100 = 44x44, targetsize-24)
# ============================================================
Write-Host "Generating Square44x44Logo..." -ForegroundColor Cyan

$r = New-Drawing 88 88
$r.Graphics.Clear($darkBg)
Draw-GaugeIcon $r.Graphics 44 40 30
$r.Bitmap.Save("$OutputDir\Square44x44Logo.scale-200.png", [System.Drawing.Imaging.ImageFormat]::Png)
Cleanup-Drawing $r

Save-ScaledCopy "$OutputDir\Square44x44Logo.scale-200.png" "$OutputDir\Square44x44Logo.png" 44 44
Save-ScaledCopy "$OutputDir\Square44x44Logo.scale-200.png" "$OutputDir\Square44x44Logo.targetsize-24_altform-unplated.png" 24 24
Write-Host "  -> Square44x44Logo OK" -ForegroundColor Green

# ============================================================
# 4. Wide310x150Logo (scale-200 = 620x300, scale-100 = 310x150)
# ============================================================
Write-Host "Generating Wide310x150Logo..." -ForegroundColor Cyan

$r = New-Drawing 620 300
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point(620, 0)),
    $darkBg2, $darkBg
)
$r.Graphics.FillRectangle($bgBrush, 0, 0, 620, 300)
$bgBrush.Dispose()

Draw-GaugeIcon $r.Graphics 110 150 75

$fontLarge = New-Object System.Drawing.Font("Segoe UI", 36, [System.Drawing.FontStyle]::Bold)
$fontSmall = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Regular)
$textBrushW = New-Object System.Drawing.SolidBrush($white)
$g = $r.Graphics
$g.DrawString("System Monitor", $fontLarge, $textBrushW, 220, 85)
$g.DrawString("CPU  Memory  Disk  Network  Battery", $fontSmall, $textBrushW, 220, 160)
$fontLarge.Dispose(); $fontSmall.Dispose(); $textBrushW.Dispose()

$r.Bitmap.Save("$OutputDir\Wide310x150Logo.scale-200.png", [System.Drawing.Imaging.ImageFormat]::Png)
Cleanup-Drawing $r
Save-ScaledCopy "$OutputDir\Wide310x150Logo.scale-200.png" "$OutputDir\Wide310x150Logo.png" 310 150
Write-Host "  -> Wide310x150Logo OK" -ForegroundColor Green

# ============================================================
# 5. SplashScreen (scale-200 = 1240x600, scale-100 = 620x300)
# ============================================================
Write-Host "Generating SplashScreen..." -ForegroundColor Cyan

$r = New-Drawing 1240 600
$r.Graphics.Clear($darkBg)

Draw-GaugeIcon $r.Graphics 620 240 120

$fontLarge = New-Object System.Drawing.Font("Segoe UI", 48, [System.Drawing.FontStyle]::Bold)
$fontSmall = New-Object System.Drawing.Font("Segoe UI", 20, [System.Drawing.FontStyle]::Regular)
Draw-CenteredText $r.Graphics "System Monitor" $fontLarge $white 620 370
Draw-CenteredText $r.Graphics "Real-time system monitoring for Command Palette" $fontSmall $subtle 620 440
$fontLarge.Dispose(); $fontSmall.Dispose()

$r.Bitmap.Save("$OutputDir\SplashScreen.scale-200.png", [System.Drawing.Imaging.ImageFormat]::Png)
Cleanup-Drawing $r
Save-ScaledCopy "$OutputDir\SplashScreen.scale-200.png" "$OutputDir\SplashScreen.png" 620 300
Write-Host "  -> SplashScreen OK" -ForegroundColor Green

Write-Host "`n=== All icons generated successfully in $OutputDir ===" -ForegroundColor Green
