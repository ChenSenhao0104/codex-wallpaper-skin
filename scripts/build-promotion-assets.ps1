[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$logoPath = Join-Path $repositoryRoot 'assets\CWS-logo-v3.0.png'
$outputDirectory = Join-Path $repositoryRoot 'promotion\assets'

foreach ($requiredPath in @($logoPath)) {
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

function Draw-BrandedBackdrop {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Width,
        [int]$Height
    )

    $baseBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new($Width, $Height),
        [System.Drawing.Color]::FromArgb(255, 6, 12, 25),
        [System.Drawing.Color]::FromArgb(255, 16, 92, 176)
    )
    try {
        $blend = [System.Drawing.Drawing2D.ColorBlend]::new(5)
        $blend.Positions = [single[]]@(0.0, 0.42, 0.66, 0.84, 1.0)
        $blend.Colors = [System.Drawing.Color[]]@(
            [System.Drawing.Color]::FromArgb(255, 5, 10, 22),
            [System.Drawing.Color]::FromArgb(255, 10, 20, 45),
            [System.Drawing.Color]::FromArgb(255, 23, 42, 105),
            [System.Drawing.Color]::FromArgb(255, 49, 76, 191),
            [System.Drawing.Color]::FromArgb(255, 8, 158, 172)
        )
        $baseBrush.InterpolationColors = $blend
        $Graphics.FillRectangle($baseBrush, 0, 0, $Width, $Height)
    }
    finally {
        $baseBrush.Dispose()
    }

    $splitPoints = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($Width * 0.64, 0),
        [System.Drawing.PointF]::new($Width, 0),
        [System.Drawing.PointF]::new($Width, $Height),
        [System.Drawing.PointF]::new($Width * 0.48, $Height)
    )
    $splitBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new([int]($Width * 0.54), 0),
        [System.Drawing.Point]::new($Width, $Height),
        [System.Drawing.Color]::FromArgb(110, 108, 71, 255),
        [System.Drawing.Color]::FromArgb(90, 20, 214, 188)
    )
    try {
        $Graphics.FillPolygon($splitBrush, $splitPoints)
    }
    finally {
        $splitBrush.Dispose()
    }

    $gridPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(34, 214, 229, 255), [Math]::Max(1, $Width / 1280))
    try {
        $step = [Math]::Max(48, [int]($Width / 22))
        for ($x = [int]($Width * 0.55); $x -lt $Width; $x += $step) {
            $Graphics.DrawLine($gridPen, $x, 0, $x, $Height)
        }
        for ($y = 0; $y -lt $Height; $y += $step) {
            $Graphics.DrawLine($gridPen, [int]($Width * 0.55), $y, $Width, $y)
        }
    }
    finally {
        $gridPen.Dispose()
    }

    $ringPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(95, 224, 238, 255), [Math]::Max(3, $Width / 420))
    $accentPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(170, 94, 234, 212), [Math]::Max(5, $Width / 260))
    try {
        $centerX = $Width * 0.83
        $centerY = $Height * 0.47
        foreach ($scale in @(0.28, 0.44, 0.62, 0.82)) {
            $ellipseWidth = $Width * $scale
            $ellipseHeight = $Height * $scale
            $Graphics.DrawEllipse($ringPen, $centerX - ($ellipseWidth / 2), $centerY - ($ellipseHeight / 2), $ellipseWidth, $ellipseHeight)
        }
        $Graphics.DrawArc($accentPen, $Width * 0.58, $Height * 0.17, $Width * 0.5, $Height * 0.72, 205, 138)
    }
    finally {
        $accentPen.Dispose()
        $ringPen.Dispose()
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
        [float]$LogoSize
    )

    $logo = [System.Drawing.Image]::FromFile($logoPath)
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        Draw-BrandedBackdrop -Graphics $graphics -Width $Width -Height $Height

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
    }
}

New-CampaignImage `
    -Width 1280 -Height 640 `
    -OutputName 'social-preview.jpg' `
    -Title "Codex Wallpaper`nSkin" `
    -Subtitle "Images · Video · Wallpaper Engine scenes" `
    -Kicker 'MAKE YOUR WORKSPACE YOURS' `
    -TitleSize 64 -SubtitleSize 27 `
    -Left 62 -Top 44 -LogoSize 92

New-CampaignImage `
    -Width 1920 -Height 1080 `
    -OutputName 'bilibili-cover.jpg' `
    -Title "把 Codex 变成`n动态工作台" `
    -Subtitle "图片 · 视频 · Wallpaper Engine Scene" `
    -Kicker 'CODEX × WALLPAPER ENGINE' `
    -TitleSize 102 -SubtitleSize 40 `
    -Left 92 -Top 72 -LogoSize 138

New-CampaignImage `
    -Width 1080 -Height 1920 `
    -OutputName 'vertical-cover.jpg' `
    -Title "把 Codex 变成`n动态工作台" `
    -Subtitle "图片 · 视频 · 动态场景" `
    -Kicker 'CODEX WALLPAPER SKIN' `
    -TitleSize 78 -SubtitleSize 34 `
    -Left 62 -Top 130 -LogoSize 126

New-CampaignImage `
    -Width 1920 -Height 1080 `
    -OutputName 'compatibility-card.jpg' `
    -Title "当前兼容范围" `
    -Subtitle "Windows 11 x64`n官方 x64 OpenAI.Codex Store / MSIX`nPublic Beta · 安装包暂未签名" `
    -Kicker 'BEFORE YOU DOWNLOAD' `
    -TitleSize 88 -SubtitleSize 42 `
    -Left 92 -Top 72 -LogoSize 138

New-CampaignImage `
    -Width 1920 -Height 1080 `
    -OutputName 'end-card.jpg' `
    -Title "开源在 GitHub" `
    -Subtitle "github.com/ChenSenhao0104/codex-wallpaper-skin`n有用的话，欢迎 Star" `
    -Kicker 'CODEX WALLPAPER SKIN' `
    -TitleSize 92 -SubtitleSize 39 `
    -Left 92 -Top 72 -LogoSize 138

Get-ChildItem -LiteralPath $outputDirectory -Filter '*.jpg' |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTime
