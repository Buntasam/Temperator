# Génère src/Temperator/Temperator.ico (le logo anneau) à partir de WPF.
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
# Petites tailles en BMP 32 bits (compatibles partout), 256 px en PNG.
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$out = Join-Path $PSScriptRoot '..\src\Temperator\Temperator.ico'

function Render([int]$size) {
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $k = $size / 64.0
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform $k, $k))
    $small = $size -le 24
    # fond : carré arrondi sombre (lisible sur barre des tâches claire ou sombre)
    $bg = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.Color]::FromRgb(0x22, 0x27, 0x30)), ([Windows.Media.Color]::FromRgb(0x0C, 0x0E, 0x12)), 90
    $inset = $(if ($small) { 0 } else { 2 })
    $dc.DrawRoundedRectangle($bg, $null, (New-Object Windows.Rect $inset, $inset, (64 - 2 * $inset), (64 - 2 * $inset)), 15, 15)
    # anneau ouvert en bas (270°), centre 32,32
    $r = $(if ($small) { 19 } else { 18.5 })
    $d = $r * [Math]::Sqrt(0.5)
    $geo = [Windows.Media.Geometry]::Parse([string]::Format([Globalization.CultureInfo]::InvariantCulture, 'M {0},{1} A {2},{2} 0 1 1 {3},{1}', (32 - $d), (32 + $d), $r, (32 + $d)))
    $pen = New-Object Windows.Media.Pen ((New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0x4E, 0xD8, 0xC0)))), $(if ($small) { 8.5 } else { 6.5 })
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    $dc.DrawGeometry($null, $pen, $geo)
    $dot = $(if ($small) { 6.5 } else { 5.5 })
    $dc.DrawEllipse((New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb(0xF1, 0xF3, 0xF6))), $null, (New-Object Windows.Point 32, 32), $dot, $dot)
    $dc.Pop(); $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $bmp
}

function Dib($bmp, [int]$size) {
    $stride = $size * 4
    $px = New-Object byte[] ($stride * $size)
    $bmp.CopyPixels($px, $stride, 0)
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($stride * $size + $maskStride * $size)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {             # lignes de bas en haut, alpha non prémultiplié
        for ($x = 0; $x -lt $size; $x++) {
            $i = $y * $stride + $x * 4
            $a = $px[$i + 3]
            if ($a -gt 0 -and $a -lt 255) { for ($c = 0; $c -lt 3; $c++) { $px[$i + $c] = [byte][Math]::Min(255, [Math]::Round($px[$i + $c] * 255.0 / $a)) } }
            $w.Write($px, $i, 4)
        }
    }
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush(); return , $ms.ToArray()
}

function Png($bmp) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream; $enc.Save($ms); return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { $b = Render $s; , $(if ($s -ge 256) { Png $b } else { Dib $b $s }) }

$fs = [IO.File]::Create($out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = ([byte[]]$images[$i]).Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$len); $w.Write([int]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Close()
"Icône écrite : $((Resolve-Path $out).Path) ($((Get-Item $out).Length) octets)"
