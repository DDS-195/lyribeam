param([string] $DesktopRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase,System.Drawing
$assetRoot = $PSScriptRoot
$windowsRoot = Join-Path $DesktopRoot 'windows'
if (-not (Test-Path -LiteralPath (Join-Path $windowsRoot 'LyricBridge.csproj'))) { throw 'Desktop source not found' }
$assetRoot = Join-Path $PSScriptRoot 'lyric-fold-b-soft'
$sourcePath = Join-Path $assetRoot 'source.png'
if (-not (Test-Path -LiteralPath $sourcePath)) { throw 'Approved B icon source not found' }
# Export the opaque visual master at each native icon size. Windows owns the clean shape mask.
$source = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new($sourcePath),
    [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
    [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
if ($source.PixelWidth -ne $source.PixelHeight -or $source.PixelWidth -lt 512) { throw 'Icon source must be a square master of at least 512 px' }
function Render-Icon([int] $size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual,[Windows.Media.BitmapScalingMode]::HighQuality)
    $context = $visual.RenderOpen()
    $padding = $size * 0.03
    $extent = $size - 2 * $padding
    $rect = [Windows.Rect]::new($padding,$padding,$extent,$extent)
    $radius = $extent * 0.23
    $context.PushClip([Windows.Media.RectangleGeometry]::new($rect,$radius,$radius))
    $context.DrawImage($source,$rect)
    $context.Pop()
    $context.Close()
    $render = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $render.Render($visual)
    return $render
}
function Png-Bytes($bitmap) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,$bytes
}
function Dib-Bytes($bitmap,[int] $size) {
    $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap,[Windows.Media.PixelFormats]::Bgra32,$null,0)
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $converted.CopyPixels($pixels,$stride,0)
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write([uint32]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
    $writer.Write([uint32]($stride * $size + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([uint32]0); $writer.Write([uint32]0)
    for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($pixels,$row * $stride,$stride) }
    for ($row = $size - 1; $row -ge 0; $row--) {
        $mask = [byte[]]::new($maskStride)
        for ($column = 0; $column -lt $size; $column++) {
            if ($pixels[$row * $stride + $column * 4 + 3] -eq 0) {
                $slot = [int][Math]::Floor($column / 8.0)
                $mask[$slot] = $mask[$slot] -bor (1 -shl (7 - ($column % 8)))
            }
        }
        $writer.Write($mask)
    }
    $writer.Flush()
    $bytes = $stream.ToArray(); $writer.Dispose(); $stream.Dispose()
    return ,$bytes
}
$sizes = @(16,20,24,32,40,48,64,96,128,256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = Render-Icon $size
    $data = if ($size -eq 256) { Png-Bytes $bitmap } else { Dib-Bytes $bitmap $size }
    $frames += [pscustomobject]@{ Size=$size; Data=$data }
}
$icoPath = Join-Path $windowsRoot 'LyricBridge.ico'
$stream = [IO.File]::Create($icoPath); $writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
} finally { $writer.Dispose(); $stream.Dispose() }
foreach ($size in @(16,20,24,32,40,48,64,96,128,256,512)) {
    [IO.File]::WriteAllBytes((Join-Path $assetRoot ("icon-" + $size + ".png")),(Png-Bytes (Render-Icon $size)))
}
Copy-Item -LiteralPath $icoPath -Destination (Join-Path $assetRoot 'lyric-fold-b.ico') -Force
[pscustomobject]@{ Icon=$icoPath; Source=$sourcePath; Sizes=$sizes; Bytes=(Get-Item -LiteralPath $icoPath).Length } | ConvertTo-Json
