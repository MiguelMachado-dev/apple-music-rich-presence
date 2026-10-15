$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\assets\app.ico'))
$sizes = @(16,24,32,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size / 64.0, $size / 64.0)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(0,0,24,24,180,90)
    $shape.AddArc(40,0,24,24,270,90)
    $shape.AddArc(40,40,24,24,0,90)
    $shape.AddArc(0,40,24,24,90,90)
    $shape.CloseFigure()
    $accent = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#FA657E'))
    $ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#201015'))
    $g.FillPath($accent, $shape)
    $g.FillRectangle($ink, 26, 18, 4, 29)
    $g.FillRectangle($ink, 45, 14, 4, 29)
    $points = [Drawing.PointF[]]@([Drawing.PointF]::new(26,18),[Drawing.PointF]::new(49,13),[Drawing.PointF]::new(49,21),[Drawing.PointF]::new(26,26))
    $g.FillPolygon($ink, $points)
    $g.FillEllipse($ink, 16, 40, 14, 11)
    $g.FillEllipse($ink, 35, 36, 14, 11)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    $stream.Dispose(); $g.Dispose(); $bitmap.Dispose(); $shape.Dispose(); $accent.Dispose(); $ink.Dispose()
}
$file = [IO.File]::Create($path)
$writer = [IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Dispose()
Write-Output $path
