# Generates src/PlayView/Assets/app.ico (multi-size: 16/32/48/256)
# Design: dark rounded square + orange play triangle (VLC-style).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 32, 48, 256
$outPath = Join-Path $PSScriptRoot '..\src\PlayView\Assets\app.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null

function New-PlayBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $s / 256.0
    $inset = [Math]::Max(1, [int](8 * $k))
    $r = [Math]::Max(2, [int](48 * $k))
    $w = $s - 2 * $inset

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($inset, $inset, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($inset + $w - 2 * $r, $inset, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($inset + $w - 2 * $r, $inset + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($inset, $inset + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()

    $dark = [System.Drawing.Color]::FromArgb(255, 29, 29, 29)
    $g.FillPath((New-Object System.Drawing.SolidBrush $dark), $path)

    # Orange play triangle
    $t = 26 * $k
    $p1 = New-Object System.Drawing.PointF -ArgumentList ((98 * $k) + $t), (62 * $k)
    $p2 = New-Object System.Drawing.PointF -ArgumentList ((98 * $k) + $t), (194 * $k)
    $p3 = New-Object System.Drawing.PointF -ArgumentList ((196 * $k) + $t), (128 * $k)
    $orange = [System.Drawing.Color]::FromArgb(255, 255, 140, 26)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $orange), @($p1, $p2, $p3))

    $g.Dispose()
    return $bmp
}

function ConvertTo-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($s * $s * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # BITMAPINFOHEADER (bottom-up rows)
    $bw.Write([uint32]40)        # biSize
    $bw.Write([int32]$s)         # biWidth
    $bw.Write([int32]($s * 2))   # biHeight (XOR + AND mask)
    $bw.Write([uint16]1)         # biPlanes
    $bw.Write([uint16]32)        # biBitCount
    $bw.Write([uint32]0)         # biCompression (BI_RGB)
    $bw.Write([uint32]($s * $s * 4))  # biSizeImage
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # XOR mask: rows bottom-up (LockBits returns top-down)
    for ($row = $s - 1; $row -ge 0; $row--) {
        $bw.Write($pixels, $row * $s * 4, $s * 4)
    }

    # AND mask: all zeros (alpha channel defines transparency), rows padded to 32 bits
    $maskRow = [Math]::Ceiling($s / 32.0) * 4
    $zeroRow = New-Object byte[] $maskRow
    for ($row = 0; $row -lt $s; $row++) { $bw.Write($zeroRow) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

$entries = @()
foreach ($size in $sizes) {
    $bmp = New-PlayBitmap $size
    $entries += @{ Size = $size; Dib = ConvertTo-DibBytes $bmp }
    $bmp.Dispose()
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0)      # reserved
$bw.Write([uint16]1)      # type: icon
$bw.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $bw.Write([byte]($e.Size % 256))
    $bw.Write([byte]($e.Size % 256))
    $bw.Write([byte]0)      # colors
    $bw.Write([byte]0)      # reserved
    $bw.Write([uint16]1)    # planes
    $bw.Write([uint16]32)   # bitcount
    $bw.Write([uint32]$e.Dib.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.Dib.Length
}
foreach ($e in $entries) { $bw.Write($e.Dib) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host "Wrote $outPath ($((Get-Item $outPath).Length) bytes)"
