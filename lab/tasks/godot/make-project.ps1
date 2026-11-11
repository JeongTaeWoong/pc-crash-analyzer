# Generates the fixed Godot project the lab imports: same bytes every time (seeded), no external assets.
#   make-project.ps1 -Path <dir> [-Textures 240] [-Scripts 120]
# 16 distinct 512x512 PNGs are produced from a fixed seed and copied under different names, because the import
# step works per file. bench.gd is copied in from this folder.
param([Parameter(Mandatory = $true)][string]$Path, [int]$Textures = 240, [int]$Scripts = 120)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
New-Item -ItemType Directory -Force "$Path\tex", "$Path\src" | Out-Null

$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText("$Path\project.godot", "config_version=5`n`n[application]`n`nconfig/name=`"lab-bench`"`n", $utf8)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bench.gd') -Destination "$Path\bench.gd"

$rnd = New-Object Random 42
$base = @()
for ($b = 0; $b -lt 16; $b++) {
    $bmp = New-Object Drawing.Bitmap 512, 512, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $rect = New-Object Drawing.Rectangle 0, 0, 512, 512
    $data = $bmp.LockBits($rect, [Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
    $bytes = New-Object byte[] ($data.Stride * 512)
    $rnd.NextBytes($bytes)
    # smooth every other row so the images are not pure noise (closer to real textures for the compressor)
    for ($y = 0; $y -lt 512; $y += 2) { $o = $y * $data.Stride; for ($x = 0; $x -lt $data.Stride; $x++) { $bytes[$o + $x] = [byte](($x + $y + $b * 16) % 256) } }
    [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
    $bmp.UnlockBits($data)
    $file = "$Path\tex\base_$b.png"
    $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $base += $file
}
for ($i = 0; $i -lt $Textures; $i++) { Copy-Item -LiteralPath $base[$i % 16] -Destination ("$Path\tex\tex_{0:D3}.png" -f $i) }
$base | ForEach-Object { Remove-Item -LiteralPath $_ }

for ($i = 0; $i -lt $Scripts; $i++) {
    $body = "extends Node`n`nvar value_$i := $i`n`nfunc compute(n: int) -> int:`n`tvar acc := 0`n`tfor k in range(n):`n`t`tacc += (k * $($i + 3)) % 17`n`treturn acc + value_$i`n"
    [IO.File]::WriteAllText(("$Path\src\unit_{0:D3}.gd" -f $i), $body, $utf8)
}
Write-Output ("project: {0} textures, {1} scripts, {2:N1} MB" -f $Textures, $Scripts, ((Get-ChildItem -LiteralPath $Path -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
