# Draws the Work List icon (blue ball with a white "list" mark) and writes app.ico
# with 16, 24, 32, 48, 64 and 256 px images.
param([string]$Out = (Join-Path $PSScriptRoot "app.ico"))
Add-Type -AssemblyName System.Drawing

function Draw-Ball([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $m = [Math]::Max(0.5, $size * 0.04)
  $r = New-Object System.Drawing.RectangleF $m, $m, ($size - 2 * $m), ($size - 2 * $m)
  $top = [System.Drawing.Color]::FromArgb(104, 130, 245)
  $bot = [System.Drawing.Color]::FromArgb(70, 96, 225)
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, $top, $bot, 90
  $g.FillEllipse($br, $r)
  $cx = $size / 2; $cy = $size / 2
  $w = $size * 0.20; $gap = $size * 0.14
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.2, $size * 0.075))
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
  foreach ($i in -1, 0, 1) {
    $y = $cy + $i * $gap
    $short = if ($i -eq 1) { $size * 0.10 } else { 0 }
    $g.DrawLine($pen, [single]($cx - $w), [single]$y, [single]($cx + $w - $short), [single]$y)
  }
  $g.Dispose()
  return $bmp
}

function To-Dib([System.Drawing.Bitmap]$bmp) {
  # 32-bit BGRA DIB (bottom-up) + 1-bit AND mask, the classic ICO image format
  $s = $bmp.Width
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter $ms
  $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
  $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
  for ($y = $s - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $s; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
    }
  }
  $rowBytes = [int]([Math]::Ceiling($s / 32.0) * 4)
  for ($y = 0; $y -lt $s; $y++) { $bw.Write((New-Object byte[] $rowBytes)) }
  $bw.Flush()
  return , $ms.ToArray()   # leading comma stops PowerShell from unrolling the byte array
}

$sizes = 16, 24, 32, 48, 64, 256
$images = @()
foreach ($s in $sizes) {
  $bmp = Draw-Ball $s
  if ($s -eq 256) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , $ms.ToArray()
  } else {
    $images += , (To-Dib $bmp)
  }
  $bmp.Dispose()
}

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([int16]1); $bw.Write([int16]32)
  $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
"Wrote $Out"
