# Converts a PNG to raw RGBA8 bytes (rows from the top, no header), the format of Context.createTexture: gfx does not decode images.
#   .\png_to_rgba.ps1                       Font.png -> Font.rgba (next to this script)
#   .\png_to_rgba.ps1 -In a.png -Out a.rgba
# The size is printed: give it to createTexture (the file has no header).

param(
  [string]$In = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Font.png'),
  [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if ($Out -eq '') { $Out = [System.IO.Path]::ChangeExtension($In, '.rgba') }

$bitmap = [System.Drawing.Bitmap]::FromFile((Resolve-Path $In))
try {
  $rect = New-Object System.Drawing.Rectangle(0, 0, $bitmap.Width, $bitmap.Height)
  $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $stride = $data.Stride
  $raw = New-Object byte[] ($stride * $bitmap.Height)
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
  $bitmap.UnlockBits($data)

  # GDI+ gives B, G, R, A per pixel: reorder to R, G, B, A and drop the padding of each row
  $width = $bitmap.Width
  $rgba = New-Object byte[] ($width * $bitmap.Height * 4)
  for ($y = 0; $y -lt $bitmap.Height; $y++) {
    $from = $y * $stride
    $to = $y * $width * 4
    for ($x = 0; $x -lt $width; $x++) {
      $s = $from + $x * 4
      $d = $to + $x * 4
      $rgba[$d] = $raw[$s + 2]
      $rgba[$d + 1] = $raw[$s + 1]
      $rgba[$d + 2] = $raw[$s]
      $rgba[$d + 3] = $raw[$s + 3]
    }
  }
  [System.IO.File]::WriteAllBytes($Out, $rgba)
  Write-Host "$Out written: $width x $($bitmap.Height), $($rgba.Length) bytes"
} finally {
  $bitmap.Dispose()
}
