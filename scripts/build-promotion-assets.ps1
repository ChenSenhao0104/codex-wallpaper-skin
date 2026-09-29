[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$backgroundPath = Join-Path $repositoryRoot 'docs\images\showcase\workspace-background.png'
$logoPath = Join-Path $repositoryRoot 'assets\CWS-logo-v3.0.png'
$outputDirectory = Join-Path $repositoryRoot 'promotion\assets'

foreach ($requiredPath in @($backgroundPath, $logoPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required promotion asset not found: $requiredPath"
    }
}

[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-CoverImage {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Image]$Image,
        [int]$Width,
        [int]$Height,
        [float]$FocusX = 0.5,
        [float]$FocusY = 0.5
    )

    $sourceRatio = $Image.Width / $Image.Height
    $targetRatio = $Width / $Height
    if ($sourceRatio -gt $targetRatio) {
        $sourceHeight = $Image.Height
        $sourceWidth = [int]($sourceHeight * $targetRatio)
        $sourceX = [int](($Image.Width - $sourceWidth) * $FocusX)
        $sourceY = 0
    }
    else {
        $sourceWidth = $Image.Width
        $sourceHeight = [int]($sourceWidth / $targetRatio)
        $sourceX = 0
        $sourceY = [int](($Image.Height - $sourceHeight) * $FocusY)
    }

    $sourceX = [Math]::Max(0, [Math]::Min($sourceX, $Image.Width - $sourceWidth))
    $sourceY = [Math]::Max(0, [Math]::Min($sourceY, $Image.Height - $sourceHeight))
    $destination = [System.Drawing.Rectangle]::new(0, 0, $Width, $Height)
    $source = [System.Drawing.Rectangle]::new($sourceX, $sourceY, $sourceWidth, $sourceHeight)
    $Graphics.DrawImage($Image, $destination, $source, [System.Drawing.GraphicsUnit]::Pixel)
}

function Draw-GradientOverlay {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Width,
        [int]$Height,
        [int]$OpaqueWidthPercent = 58
    )

    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new($Width, 0),
        [System.Drawing.Color]::FromArgb(247, 10, 16, 30),
        [System.Drawing.Color]::FromArgb(40, 10, 16, 30)
    )
    try {
        $blend = [System.Drawing.Drawing2D.ColorBlend]::new(4)
        $blend.Positions = [single[]]@(0.0, ($OpaqueWidthPercent / 100.0), 0.82, 1.0)
        $blend.Colors = [System.Drawing.Color[]]@(
            [System.Drawing.Color]::FromArgb(249, 10, 16, 30),
            [System.Drawing.Color]::FromArgb(232, 10, 16, 30),
            [System.Drawing.Color]::FromArgb(105, 10, 16, 30),
            [System.Drawing.Color]::FromArgb(30, 10, 16, 30)
        )
        $brush.InterpolationColors = $blend
        $Graphics.FillRectangle($brush, 0, 0, $Width, $Height)
    }
    finally {
        $brush.Dispose()
    }
}

function Draw-Badge {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [float]$X,
        [float]$Y,
        [float]$FontSize,
        [System.Drawing.Color]$FillColor
    )

    $font = [System.Drawing.Font]::new('Segoe UI Semibold', $FontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $measured = $Graphics.MeasureString($Text, $font)
        $rectangle = [System.Drawing.RectangleF]::new($X, $Y, $measured.Width + 34, $measured.Height + 16)
        $path = New-RoundedRectanglePath -Rectangle $rectangle -Radius 14
        $fill = [System.Drawing.SolidBrush]::new($FillColor)
        $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        try {
            $Graphics.FillPath($fill, $path)
            $Graphics.DrawString($Text, $font, $textBrush, $X + 17, $Y + 7)
        }
        finally {
            $textBrush.Dispose()
            $fill.Dispose()
            $path.Dispose()
        }
        return $rectangle.Right
    }
    finally {
        $font.Dispose()
    }
}

function Save-Jpeg {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [string]$Path,
        [long]$Quality = 88
    )

    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object { $_.MimeType -eq 'image/jpeg' } |
        Select-Object -First 1
    $encoderParameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
    try {
        $encoderParameters.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new(
            [System.Drawing.Imaging.Encoder]::Quality,
            $Quality
        )
        $Bitmap.Save($Path, $codec, $encoderParameters)
    }
    finally {
        $encoderParameters.Dispose()
    }
}

function New-CampaignImage {
    param(
        [int]$Width,
        [int]$Height,
        [string]$OutputName,
        [string]$Title,
        [string]$Subtitle,
        [string]$Kicker,
        [float]$TitleSize,
        [float]$SubtitleSize,
        [float]$Left,
        [float]$Top,
        [float]$LogoSize,
        [float]$FocusX = 0.5,
        [float]$FocusY = 0.5
    )

    $background = [System.Drawing.Image]::FromFile($backgroundPath)
    $logo = [System.Drawing.Image]::FromFile($logoPath)
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        Draw-CoverImage -Graphics $graphics -Image $background -Width $Width -Height $Height -FocusX $FocusX -FocusY $FocusY
        Draw-GradientOverlay -Graphics $graphics -Width $Width -Height $Height

        $graphics.DrawImage($logo, $Left, $Top, $LogoSize, $LogoSize)

        $kickerFont = [System.Drawing.Font]::new('Segoe UI Semibold', [Math]::Max(22, $SubtitleSize * 0.86), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $titleFont = [System.Drawing.Font]::new('Microsoft YaHei UI', $TitleSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $subtitleFont = [System.Drawing.Font]::new('Microsoft YaHei UI', $SubtitleSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $muted = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(230, 219, 229, 245))
        $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 103, 153, 255))
        try {
            $textX = $Left
            $kickerY = $Top + $LogoSize + ($Height * 0.035)
            $graphics.DrawString($Kicker, $kickerFont, $accent, $textX, $kickerY)
            $titleY = $kickerY + ($kickerFont.Size * 1.8)
            $graphics.DrawString($Title, $titleFont, $white, $textX, $titleY)
            $titleMeasure = $graphics.MeasureString($Title, $titleFont, [int]($Width * 0.58))
            $subtitleY = $titleY + $titleMeasure.Height + ($Height * 0.025)
            $graphics.DrawString($Subtitle, $subtitleFont, $muted, $textX, $subtitleY)

            $badgeY = $Height - [Math]::Max(74, $Height * 0.11)
            $badgeEnd = Draw-Badge -Graphics $graphics -Text 'Windows 11 x64' -X $textX -Y $badgeY -FontSize ([Math]::Max(19, $SubtitleSize * 0.72)) -FillColor ([System.Drawing.Color]::FromArgb(225, 37, 99, 235))
            $null = Draw-Badge -Graphics $graphics -Text 'Open Source' -X ($badgeEnd + 16) -Y $badgeY -FontSize ([Math]::Max(19, $SubtitleSize * 0.72)) -FillColor ([System.Drawing.Color]::FromArgb(225, 13, 148, 136))
        }
        finally {
            $accent.Dispose()
            $muted.Dispose()
            $white.Dispose()
            $subtitleFont.Dispose()
            $titleFont.Dispose()
            $kickerFont.Dispose()
        }

        Save-Jpeg -Bitmap $bitmap -Path (Join-Path $outputDirectory $OutputName)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
        $logo.Dispose()
        $background.Dispose()
    }
}

New-CampaignImage `
    -Width 1280 -Height 640 `
    -OutputName 'social-preview.jpg' `
    -Title "Codex Wallpaper`nSkin" `
    -Subtitle "Images · Video · Wallpaper Engine scenes" `
    -Kicker 'MAKE YOUR WORKSPACE YOURS' `
    -TitleSize 64 -SubtitleSize 27 `
    -Left 62 -Top 44 -LogoSize 92 `
    -FocusX 0.68 -FocusY 0.48

New-CampaignImage `
    -Width 1920 -Height 1080 `
    -OutputName 'bilibili-cover.jpg' `
    -Title "把 Codex 变成`n动态工作台" `
    -Subtitle "图片 · 视频 · Wallpaper Engine Scene" `
    -Kicker 'CODEX × WALLPAPER ENGINE' `
    -TitleSize 102 -SubtitleSize 40 `
    -Left 92 -Top 72 -LogoSize 138 `
    -FocusX 0.68 -FocusY 0.48

New-CampaignImage `
    -Width 1080 -Height 1920 `
    -OutputName 'vertical-cover.jpg' `
    -Title "把 Codex 变成`n动态工作台" `
    -Subtitle "图片 · 视频 · 动态场景" `
    -Kicker 'CODEX WALLPAPER SKIN' `
    -TitleSize 78 -SubtitleSize 34 `
    -Left 62 -Top 130 -LogoSize 126 `
    -FocusX 0.62 -FocusY 0.48

New-CampaignImage `
    -Width 1920 -Height 1080 `
    -OutputName 'compatibility-card.jpg' `
    -Title "当前兼容范围" `
    -Subtitle "Windows 11 x64`n官方 x64 OpenAI.Codex Store / MSIX`nPublic Beta · 安装包暂未签名" `
    -Kicker 'BEFORE YOU DOWNLOAD' `
    -TitleSize 88 -SubtitleSize 42 `
    -Left 92 -Top 72 -LogoSize 138 `
    -FocusX 0.68 -FocusY 0.48

New-CampaignImage `
    -Width 1920 -Height 1080 `
    -OutputName 'end-card.jpg' `
    -Title "开源在 GitHub" `
    -Subtitle "github.com/ChenSenhao0104/codex-wallpaper-skin`n有用的话，欢迎 Star" `
    -Kicker 'CODEX WALLPAPER SKIN' `
    -TitleSize 92 -SubtitleSize 39 `
    -Left 92 -Top 72 -LogoSize 138 `
    -FocusX 0.68 -FocusY 0.48

Get-ChildItem -LiteralPath $outputDirectory -Filter '*.jpg' |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTime
