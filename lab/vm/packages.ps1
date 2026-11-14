# Builds the "real program" packages a case run copies into a lab guest (lab/vm/packages.json): an old version of a
# real program with a documented bug, plus the files the scenario needs. One zip per package under
# <vm root>\payload\packages, laid out as it will sit on the guest's C: drive - case.ps1 expands it at C:\.
# Nothing is downloaded here: the sources are unpacked official archives under <vm root>\payload\packages\src.
# ASCII only.
#
#   lab\vm\packages.ps1                      build what is not built yet
#   lab\vm\packages.ps1 -Only ahk-2.0.29 -Force
#   lab\vm\packages.ps1 -List
param(
    [string[]]$Only = @(),
    [switch]$Force,
    [switch]$List,
    [string]$Root
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$def = Get-Content (Join-Path $PSScriptRoot 'packages.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$outDir = Join-Path (Join-Path $cfg.root 'payload') 'packages'
$srcDir = Join-Path $outDir 'src'
$bs = [string][char]92

if ($List) {
    foreach ($p in $def.packages.PSObject.Properties) {
        $zip = Join-Path $outDir "$($p.Name).zip"
        Write-Host ("  {0,-16} {1,-16} {2,-12} {3}" -f $p.Name, $p.Value.product, $p.Value.version, $(if (Test-Path $zip) { 'built' } else { 'not built' }))
    }
    exit 0
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$bad = 0
foreach ($p in $def.packages.PSObject.Properties) {
    $id = $p.Name
    $pk = $p.Value
    if ($Only.Count -and $Only -notcontains $id) { continue }
    $zip = Join-Path $outDir "$id.zip"
    if ((Test-Path $zip) -and -not $Force) { Write-Host "  ${id}: already built"; continue }
    $stage = Join-Path (Join-Path $outDir '_stage') $id
    try {
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        foreach ($c in @($pk.copy)) {
            if (-not $c) { continue }
            $from = Join-Path $srcDir ("$($c.src)".Replace('/', $bs))
            if (-not (Test-Path $from)) { throw "source folder missing: $from (official archive: $($pk.official_url))" }
            $to = Join-Path $stage ("$($c.dest)".Replace('/', $bs))
            New-Item -ItemType Directory -Force -Path $to | Out-Null
            Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
            foreach ($x in @($c.exclude)) {
                if (-not $x) { continue }
                $t = Join-Path $to "$x"
                if (Test-Path $t) { Remove-Item -Recurse -Force $t }
            }
        }
        foreach ($f in @($pk.files)) {
            if (-not $f) { continue }
            $to = Join-Path $stage ("$($f.dest)".Replace('/', $bs))
            New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
            if ($f.from_repo) { Copy-Item -Path (Join-Path $repo ("$($f.from_repo)".Replace('/', $bs))) -Destination $to -Force }
            else { [IO.File]::WriteAllLines($to, [string[]]@($f.lines), (New-Object Text.UTF8Encoding($false))) }
        }
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
        $files = @(Get-ChildItem $stage -Recurse -File)
        [ordered]@{
            id = $id; product = $pk.product; version = $pk.version; official_url = $pk.official_url
            built_at = (Get-Date).ToString('o'); files = $files.Count; bytes = ($files | Measure-Object Length -Sum).Sum
            zip_sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash
        } | ConvertTo-Json | Set-Content -Path (Join-Path $outDir "$id.json") -Encoding UTF8
        Write-Host ("  {0}: built, {1} files, {2:N1} MB" -f $id, $files.Count, ((Get-Item $zip).Length / 1MB))
    } catch {
        Write-Host "  ${id}: FAILED - $($_.Exception.Message)"
        $bad++
    } finally {
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
    }
}
exit $(if ($bad) { 1 } else { 0 })
