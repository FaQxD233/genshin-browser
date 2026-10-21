param(
    [string]$StickerPath = (Join-Path $PSScriptRoot "..\Resources\AppIcon\keqing-sticker.png"),
    [string]$HeadPath = (Join-Path $PSScriptRoot "..\Resources\AppIcon\keqing-head.png"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\Resources\AppIcon\app.ico")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Large frames use the full sticker. At 40 px and below its face becomes unreadable,
# so the small frames (taskbar, title bar) use the head close-up instead.
$frameSpecs = @(
    @{ Size = 256; Source = "sticker" },
    @{ Size = 128; Source = "sticker" },
    @{ Size = 64; Source = "sticker" },
    @{ Size = 48; Source = "sticker" },
    @{ Size = 40; Source = "head" },
    @{ Size = 32; Source = "head" },
    @{ Size = 24; Source = "head" },
    @{ Size = 20; Source = "head" },
    @{ Size = 16; Source = "head" }
)

function ConvertTo-PngFrame([System.Drawing.Image]$Image, [int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        # Without TileFlipXY the resampler blends the area outside the source into the outer pixels.
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage(
            $Image,
            [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
            0, 0, $Image.Width, $Image.Height,
            [System.Drawing.GraphicsUnit]::Pixel,
            $attributes)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $attributes.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sources = @{
    sticker = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $StickerPath).Path)
    head = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $HeadPath).Path)
}
try {
    $frames = @(foreach ($spec in $frameSpecs) {
        [pscustomobject]@{
            Size = $spec.Size
            Data = ConvertTo-PngFrame $sources[$spec.Source] $spec.Size
        }
    })
}
finally {
    foreach ($image in $sources.Values) {
        $image.Dispose()
    }
}

# ICO container: ICONDIR, one ICONDIRENTRY per frame, then the PNG payloads.
# PNG-compressed frames are supported by Windows Vista and later.
$buffer = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($buffer)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Data)
    }
    $writer.Flush()
    $outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
    [System.IO.File]::WriteAllBytes($outputFullPath, $buffer.ToArray())
}
finally {
    $writer.Dispose()
    $buffer.Dispose()
}

$sizeKiB = [math]::Round((Get-Item -LiteralPath $outputFullPath).Length / 1KB, 1)
Write-Host "Icon written: $outputFullPath ($($frames.Count) frames, $sizeKiB KiB)"
