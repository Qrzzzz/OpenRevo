# Encode the imagegen master into Windows' physical icon sizes; no artwork is redrawn.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Add-Type -AssemblyName System.Drawing
$source = Join-Path $root 'assets\branding\OpenRevo-1.1.2.png'
$destination = Join-Path $root 'src\OpenRevo.App\Assets'
$picture = [Drawing.Image]::FromFile($source)
$sizes = @(16,20,24,28,32,40,48,64,128,256)
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($picture,[Drawing.Rectangle]::new(0,0,$size,$size),0,0,$picture.Width,$picture.Height,[Drawing.GraphicsUnit]::Pixel,$attributes)
            $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($memory.ToArray())
        }
        finally { $memory.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $picture.Dispose() }
$stream = [IO.File]::Create((Join-Path $destination 'OpenRevo.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally { $writer.Dispose(); $stream.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $destination 'OpenRevo.png'),$frames[$frames.Count - 1])
Write-Output 'Encoded imagegen master as PNG and ten-size Windows ICO.'
