# 生成应用图标 app.ico（红色圆角方块 + 白色声波）
# 用法：powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 -Out src\ShengJi\Assets\app.ico
param([Parameter(Mandatory = $true)][string]$Out)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $s / 20.0
    # 圆角矩形
    $r = 5 * $k
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 255, 99, 112)), ([System.Drawing.Color]::FromArgb(255, 236, 52, 72)), 90
    $g.FillPath($brush, $path)

    # 声波竖线
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single](1.9 * $k))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bars = @(@(5.5, 9.3, 10.7), @(8, 7.2, 12.8), @(10.5, 5, 15), @(13, 8, 12), @(15.2, 9.3, 10.7))
    foreach ($b in $bars) {
        $g.DrawLine($pen, [single]($b[0] * $k), [single]($b[1] * $k), [single]($b[0] * $k), [single]($b[2] * $k))
    }
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER（高度 = 2 倍，含 AND 掩码）
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([Math]::Ceiling($s / 32.0) * 4)
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $rowBytes
        for ($x = 0; $x -lt $s; $x++) {
            if ($bmp.GetPixel($x, $y).A -eq 0) { $bi = [Math]::Floor($x / 8); $row[$bi] = [byte]($row[$bi] -bor (0x80 -shr ($x % 8))) }
        }
        $w.Write($row)
    }
    $w.Flush()
    return ,$ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$images = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , $ms.ToArray()
    } else {
        $images += , (Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush(); $fs.Close()
Write-Output "已生成 $Out"
