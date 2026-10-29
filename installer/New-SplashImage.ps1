param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $SourcePath,
    [int] $Width = 960,
    [int] $Height = 540
)

$ErrorActionPreference = 'Stop'
if (-not $SourcePath) { $SourcePath = Join-Path $PSScriptRoot 'splash.png' }
Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Image]::FromFile((Resolve-Path $SourcePath))
$bitmap = New-Object System.Drawing.Bitmap $Width, $Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawImage($source, 0, 0, $Width, $Height)

    # Bottom-right, kept above the strip Velopack draws its progress bar over.
    $font = New-Object System.Drawing.Font 'Segoe UI Semibold', 13, ([System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(160, 160, 160))
    $text = "v$Version"
    $size = $graphics.MeasureString($text, $font)
    $graphics.DrawString($text, $font, $brush, $Width - $size.Width - 16, $Height - $size.Height - 24)
    $font.Dispose()
    $brush.Dispose()

    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    $source.Dispose()
}
