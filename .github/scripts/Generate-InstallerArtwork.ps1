[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repository = [IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$assets = Join-Path $repository 'Installer\Assets'
[IO.Directory]::CreateDirectory($assets) | Out-Null
$source = [Drawing.Image]::FromFile((Join-Path $repository 'FolderRewind\Assets\Square150x150Logo.scale-200.png'))
try {
    $frames = foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
        $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source,0,0,$size,$size)
            $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
            [PSCustomObject]@{Size=$size;Bytes=$memory.ToArray()}
        } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $stream = [IO.File]::Create((Join-Path $repository 'FolderRewind\Assets\MsiApp.ico'))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if($frame.Size -eq 256){0}else{$frame.Size}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([uint16]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    } finally { $writer.Dispose(); $stream.Dispose() }
    foreach($image in @(@{Name='Banner.bmp';Width=493;Height=58},@{Name='Dialog.bmp';Width=493;Height=312})) {
        $bitmap = [Drawing.Bitmap]::new($image.Width,$image.Height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $font = [Drawing.Font]::new('Segoe UI',16,[Drawing.FontStyle]::Bold)
        $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(27,98,162))
        try {
            $graphics.Clear([Drawing.Color]::FromArgb(245,249,253))
            $graphics.DrawImage($source,12,8,42,42)
            $graphics.DrawString('FolderRewind',$font,$brush,62,14)
            $bitmap.Save((Join-Path $assets $image.Name),[Drawing.Imaging.ImageFormat]::Bmp)
        } finally { $brush.Dispose(); $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $source.Save((Join-Path $assets 'Logo.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $source.Dispose() }
Write-Output 'Installer artwork generated from the existing FolderRewind brand image.'
$license = [IO.File]::ReadAllText((Join-Path $repository 'LICENSE.txt')).Replace('\','\\').Replace('{','\{').Replace('}','\}').Replace("`r",'').Replace("`n",'\par ')
'{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $license + '}' | Set-Content (Join-Path $assets 'License.rtf') -Encoding ascii
