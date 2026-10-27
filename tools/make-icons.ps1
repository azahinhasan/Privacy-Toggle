# Generates the PrivacyToggle icons as multi-size .ico files (16, 20, 24, 32, 48, 256).
# Small sizes are stored as 32-bit DIBs, 256 as PNG (the standard Windows layout).
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1

Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\src\PrivacyToggle\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null
$sizes = 16, 20, 24, 32, 48, 256

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Neubrutalism: ink (near-black) glyphs and outlines on flat bright fills, hard offset shadows on big sizes.
$Ink = [System.Drawing.Color]::FromArgb(255, 17, 17, 17)

# Draws a microphone glyph in $color. Coordinates are fractions of the icon size.
function Draw-Mic($g, [int]$s, [float]$scale, [System.Drawing.Color]$color = $Ink) {
    $u = $s * $scale
    $ox = ($s - $u) / 2
    $oy = ($s - $u) / 2
    $white = $color
    $stroke = [Math]::Max(1.0, $u * 0.085)

    # capsule
    $capsule = New-RoundedRect ($ox + $u * 0.36) ($oy + $u * 0.08) ($u * 0.28) ($u * 0.50) ($u * 0.14)
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), $capsule)

    $pen = New-Object System.Drawing.Pen $white, $stroke
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    # holder (bottom half of an ellipse)
    $g.DrawArc($pen, $ox + $u * 0.22, $oy + $u * 0.22, $u * 0.56, $u * 0.52, 0, 180)
    # stem + base
    $g.DrawLine($pen, $ox + $u * 0.50, $oy + $u * 0.74, $ox + $u * 0.50, $oy + $u * 0.88)
    $g.DrawLine($pen, $ox + $u * 0.34, $oy + $u * 0.90, $ox + $u * 0.66, $oy + $u * 0.90)
}

# Draws a video-camera glyph in $color: rounded body plus a lens "horn" on the right.
function Draw-Camera($g, [int]$s, [System.Drawing.Color]$color = $Ink) {
    $white = New-Object System.Drawing.SolidBrush $color
    $g.FillPath($white, (New-RoundedRect ($s * 0.21) ($s * 0.34) ($s * 0.40) ($s * 0.32) ($s * 0.07)))
    $g.FillPolygon($white, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.63), ($s * 0.46)),
        (New-Object System.Drawing.PointF ($s * 0.80), ($s * 0.36)),
        (New-Object System.Drawing.PointF ($s * 0.80), ($s * 0.64)),
        (New-Object System.Drawing.PointF ($s * 0.63), ($s * 0.54))))
}

# Draws a globe glyph in $color for the network: outline, one meridian and the equator.
function Draw-Globe($g, [int]$s, [System.Drawing.Color]$color = $Ink) {
    $pen = New-Object System.Drawing.Pen $color, ([Math]::Max(1.0, $s * 0.075))
    $r = $s * 0.27
    $c = $s * 0.5
    $g.DrawEllipse($pen, $c - $r, $c - $r, 2 * $r, 2 * $r)
    $g.DrawEllipse($pen, $c - $r * 0.45, $c - $r, $r * 0.9, 2 * $r)
    $g.DrawLine($pen, $c - $r, $c, $c + $r, $c)
}

# App icon: a white privacy shield with an ink outline and an ink keyhole.
function Draw-Shield($g, [int]$s, [System.Drawing.Color]$hole) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $l = $s * 0.27; $r = $s * 0.73; $t = $s * 0.18; $mid = $s * 0.5
    $p.AddLine($mid, $t, $r, $t + $s * 0.08)
    $p.AddLine($r, $t + $s * 0.08, $r, $s * 0.48)
    $p.AddBezier($r, $s * 0.48, $r, $s * 0.66, $s * 0.62, $s * 0.76, $mid, $s * 0.83)
    $p.AddBezier($mid, $s * 0.83, $s * 0.38, $s * 0.76, $l, $s * 0.66, $l, $s * 0.48)
    $p.AddLine($l, $s * 0.48, $l, $t + $s * 0.08)
    $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $p)
    $outline = New-Object System.Drawing.Pen $Ink, ([Math]::Max(1.0, $s * 0.045))
    $outline.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($outline, $p)

    $brush = New-Object System.Drawing.SolidBrush $hole
    $d = $s * 0.13
    $g.FillEllipse($brush, $mid - $d / 2, $s * 0.38, $d, $d)
    $kw = $s * 0.035
    $g.FillPolygon($brush, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($mid - $kw), ($s * 0.46)),
        (New-Object System.Drawing.PointF ($mid + $kw), ($s * 0.46)),
        (New-Object System.Drawing.PointF ($mid + $kw * 1.6), ($s * 0.62)),
        (New-Object System.Drawing.PointF ($mid - $kw * 1.6), ($s * 0.62))))
}

function Draw-Slash($g, [int]$s, [System.Drawing.Color]$bg) {
    $w = [Math]::Max(1.5, $s * 0.075)
    $a = [float]($s * 0.24); $b = [float]($s * 0.76)
    $gap = New-Object System.Drawing.Pen $bg, ([Math]::Max(2.5, $w * 2.4))
    $gap.StartCap = $gap.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($gap, $a, $a, $b, $b)
    $pen = New-Object System.Drawing.Pen $Ink, $w
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, $a, $a, $b, $b)
}

function Render([string]$kind, [int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $inset = [Math]::Max(0.5, $s * 0.02)

    # Big sizes get a hard offset shadow; small tray sizes skip it to keep the shape as large as possible.
    $shadow = if ($s -ge 32) { $s * 0.07 } else { 0 }
    $d = $s - 2 * $inset - $shadow                       # size of the shape itself
    $outlineW = [Math]::Max(1.0, $s * 0.06)
    $inkBrush = New-Object System.Drawing.SolidBrush $Ink
    $outline = New-Object System.Drawing.Pen $Ink, $outlineW
    $half = $outlineW / 2

    switch ($kind) {
        'app' {
            $yellow = [System.Drawing.Color]::FromArgb(255, 217, 122, 43)   # #D97A2B accent
            if ($shadow -gt 0) { $g.FillPath($inkBrush, (New-RoundedRect ($inset + $shadow) ($inset + $shadow) $d $d ($s * 0.2))) }
            $tile = New-RoundedRect ($inset + $half) ($inset + $half) ($d - $outlineW) ($d - $outlineW) ($s * 0.2)
            $g.FillPath((New-Object System.Drawing.SolidBrush $yellow), $tile)
            $g.DrawPath($outline, $tile)
            # draw the shield inside the tile (scaled to the tile, not the whole canvas)
            $state = $g.Save()
            $g.TranslateTransform($inset, $inset); $g.ScaleTransform($d / $s, $d / $s)
            Draw-Shield $g $s $Ink
            $g.Restore($state)
        }
        default {
            # 'on' / 'off' / 'none' = microphone; 'cam-on' / ... = camera; 'net-on' / ... = network
            $state = $kind -replace '^(cam|net)-', ''
            $color = switch ($state) {
                'on'   { [System.Drawing.Color]::FromArgb(255, 123, 228, 149) }   # LiveFill
                'off'  { [System.Drawing.Color]::FromArgb(255, 255, 107, 107) }   # coral
                'none' { [System.Drawing.Color]::FromArgb(255, 218, 221, 225) }   # NoneFill
            }
            if ($shadow -gt 0) { $g.FillEllipse($inkBrush, $inset + $shadow, $inset + $shadow, $d, $d) }
            $g.FillEllipse((New-Object System.Drawing.SolidBrush $color), $inset + $half, $inset + $half, $d - $outlineW, $d - $outlineW)
            $g.DrawEllipse($outline, $inset + $half, $inset + $half, $d - $outlineW, $d - $outlineW)
            # glyph + slash drawn inside the circle
            $saved = $g.Save()
            $g.TranslateTransform($inset, $inset); $g.ScaleTransform($d / $s, $d / $s)
            # Tray sizes: Windows gives the icon a fixed slot, so enlarge the glyph (not the slash) inside the circle instead.
            $unzoomed = $g.Save()
            if ($s -lt 32) {
                $zoom = if ($kind -like 'cam-*') { 1.1 } else { 1.3 }   # the camera is wider, so it has less room
                $g.TranslateTransform($s / 2, $s / 2); $g.ScaleTransform($zoom, $zoom); $g.TranslateTransform(-$s / 2, -$s / 2)
            }
            if ($kind -like 'cam-*') { Draw-Camera $g $s } elseif ($kind -like 'net-*') { Draw-Globe $g $s } else { Draw-Mic $g $s 0.56 }
            $g.Restore($unzoomed)
            if ($state -eq 'off') { Draw-Slash $g $s $color }
            $g.Restore($saved)
        }
    }
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER (height is doubled: XOR + AND masks)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write([int]($s * $s * 4 + $maskStride * $s))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    # pixel rows, bottom-up (BGRA already in memory order)
    for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $s * 4) }
    # AND mask: all zero (alpha channel does the work)
    $bw.Write((New-Object byte[] ($maskStride * $s)))
    $bw.Flush()
    return , $ms.ToArray()
}

function Write-Ico([string]$kind, [string]$path) {
    $images = foreach ($s in $sizes) {
        $bmp = Render $kind $s
        if ($s -ge 256) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            , $ms.ToArray()
        } else {
            , (Get-DibBytes $bmp)
        }
        $bmp.Dispose()
    }

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $images[$i].Length
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$len); $bw.Write([int]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img) }
    $bw.Close()
    Write-Host "wrote $path"
}

Write-Ico 'app'  (Join-Path $outDir 'app.ico')
Write-Ico 'on'   (Join-Path $outDir 'mic-on.ico')
Write-Ico 'off'  (Join-Path $outDir 'mic-off.ico')
Write-Ico 'none' (Join-Path $outDir 'mic-none.ico')
Write-Ico 'cam-on'   (Join-Path $outDir 'cam-on.ico')
Write-Ico 'cam-off'  (Join-Path $outDir 'cam-off.ico')
Write-Ico 'cam-none' (Join-Path $outDir 'cam-none.ico')
Write-Ico 'net-on'   (Join-Path $outDir 'net-on.ico')
Write-Ico 'net-off'  (Join-Path $outDir 'net-off.ico')
Write-Ico 'net-none' (Join-Path $outDir 'net-none.ico')

# Large PNG previews for the docs
$docs = Join-Path $PSScriptRoot '..\docs'
New-Item -ItemType Directory -Force $docs | Out-Null
foreach ($k in 'app', 'on', 'off', 'none', 'cam-on', 'cam-off', 'cam-none', 'net-on', 'net-off', 'net-none') {
    $bmp = Render $k 256
    $bmp.Save((Join-Path $docs "icon-$k.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
