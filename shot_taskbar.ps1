param(
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$X = 0, [int]$Y = 0, [int]$W = 0, [int]$H = 0
)
# Screenshot the desktop area (bottom strip by default) to inspect the real taskbar.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($W -le 0) { $W = $b.Width }
if ($H -le 0) { $H = 160 }
if ($Y -eq 0) { $Y = $b.Height - $H }

$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "screen=$($b.Width)x$($b.Height) captured ${W}x${H} at $X,$Y -> $Out"
