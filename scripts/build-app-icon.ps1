param(
  [Parameter(Mandatory = $true)]
  [string]$SourcePng,

  [Parameter(Mandatory = $true)]
  [string]$OutputIco
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing.Common

$sourcePath = [IO.Path]::GetFullPath($SourcePng)
$outputPath = [IO.Path]::GetFullPath($OutputIco)
$outputDirectory = [IO.Path]::GetDirectoryName($outputPath)
if (-not [IO.File]::Exists($sourcePath)) {
  throw "Icon source image does not exist: $sourcePath"
}
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
  [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = [Collections.Generic.List[byte[]]]::new()
$source = [Drawing.Image]::FromFile($sourcePath)
try {
  foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $graphics = [Drawing.Graphics]::FromImage($bitmap)
      try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, 0, 0, $size, $size)
      }
      finally {
        $graphics.Dispose()
      }

      $memory = [IO.MemoryStream]::new()
      try {
        $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
        $images.Add($memory.ToArray())
      }
      finally {
        $memory.Dispose()
      }
    }
    finally {
      $bitmap.Dispose()
    }
  }
}
finally {
  $source.Dispose()
}

$stream = [IO.File]::Create($outputPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
  $writer.Write([uint16]0)
  $writer.Write([uint16]1)
  $writer.Write([uint16]$images.Count)

  $offset = 6 + (16 * $images.Count)
  for ($index = 0; $index -lt $images.Count; $index++) {
    $size = $sizes[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$index].Length
  }

  foreach ($image in $images) {
    $writer.Write($image)
  }
}
finally {
  $writer.Dispose()
  $stream.Dispose()
}

Write-Host "Created Windows application icon: $outputPath"
