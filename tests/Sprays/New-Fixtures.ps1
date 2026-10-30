param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$fixtureDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(3, 2)
try {
    $bitmap.SetPixel(0, 0, [System.Drawing.Color]::Red)
    $bitmap.SetPixel(1, 0, [System.Drawing.Color]::Green)
    $bitmap.Save((Join-Path $fixtureDirectory 'indexed.gif'), [System.Drawing.Imaging.ImageFormat]::Gif)
    $bitmap.Save((Join-Path $fixtureDirectory 'jpeg-as-gif.gif'), [System.Drawing.Imaging.ImageFormat]::Jpeg)
} finally { $bitmap.Dispose() }
# Two-color, two-frame GIF: red/green canvas, then a green 1x1 subframe.
# Delays are 20 ms and 30 ms; the second frame must retain the other pixel.
[byte[]]$gif = @(71,73,70,56,57,97,2,0,1,0,128,0,0,255,0,0,0,255,0,
    33,249,4,0,2,0,0,0,44,0,0,0,0,2,0,1,0,0,2,2,68,10,0,
    33,249,4,0,3,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59)
[IO.File]::WriteAllBytes((Join-Path $fixtureDirectory 'partial-frames.gif'), $gif)
