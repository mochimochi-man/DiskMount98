# DiskMount98.ico を生成する (フロッピーディスクの絵、PNG埋め込みICO)
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($s / 16.0, $s / 16.0)
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddLines([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 1, 1), (New-Object System.Drawing.PointF 13, 1),
        (New-Object System.Drawing.PointF 15, 3), (New-Object System.Drawing.PointF 15, 15),
        (New-Object System.Drawing.PointF 1, 15)))
    $body.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 62, 110))), $body)
    # シャッター
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 205, 215))), 4, 1, 8, 5)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 62, 110))), 9, 2, 2, 3)
    # ラベル
    $g.FillRectangle([System.Drawing.Brushes]::White, 3, 8, 10, 7)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$off)
    $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'DiskMount98.ico'), $out.ToArray())
