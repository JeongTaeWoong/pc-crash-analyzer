# Spikes 4 and 5 (docs/security-impact-design.md section 9): do the Blender and Godot workloads run headless, and how much
# do their times vary between runs? The first run of each is a warm-up and is left out of the statistics.
#   powershell -ExecutionPolicy Bypass -File lab\spikes\spike-tasks.ps1 [-Runs 5]
# Writes only to the work folder under %LOCALAPPDATA%\Temp (C:, where file-system filters apply) and to lab\tools\godot\editor_data.
param([int]$Runs = 5)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lab = Split-Path -Parent $PSScriptRoot
$blender = (Get-ChildItem -LiteralPath "$lab\tools\blender" -Recurse -Depth 2 -Filter blender.exe | Select-Object -First 1).FullName
$godot = (Get-ChildItem -LiteralPath "$lab\tools\godot" -Filter 'Godot*_console.exe' | Select-Object -First 1).FullName
if (-not $blender -or -not $godot) { throw 'blender.exe or Godot console exe not found under lab\tools' }
$work = Join-Path $env:LOCALAPPDATA 'Temp\pca-lab-spike'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null

function Stats([double[]]$v) {
    $v = @($v)
    if ($v.Count -lt 2) { return 'n<2' }
    $m = ($v | Measure-Object -Average).Average
    $sd = [math]::Sqrt((($v | ForEach-Object { ($_ - $m) * ($_ - $m) }) | Measure-Object -Sum).Sum / ($v.Count - 1))
    return ('mean {0:N2} sd {1:N3} cv {2:N1}% (n={3})' -f $m, $sd, ($sd / $m * 100), $v.Count)
}

"disks: " + ((Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object { "$($_.FriendlyName) [$($_.MediaType)]" }) -join '; ')
"tools on $((Get-Item $lab).PSDrive.Name):, work folder on $((Get-Item $work).PSDrive.Name):"
"games running: " + [bool](Get-Process -Name LOSTARK -ErrorAction SilentlyContinue)

"===== 4. Blender ====="
$rows = @()
for ($i = 0; $i -lt $Runs; $i++) {
    $json = "$work\blender-$i.json"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $blender -b --factory-startup --python "$lab\tasks\blender\bench.py" -- $json "$work\blender-work" 2>&1
    $wall = $sw.Elapsed.TotalSeconds
    if (-not (Test-Path -LiteralPath $json)) { "run ${i}: no result file. exit $LASTEXITCODE. last output: $(($out | Select-Object -Last 3) -join ' / ')"; continue }
    $r = Get-Content -Raw -LiteralPath $json | ConvertFrom-Json
    $s = $r.stages
    "run {0}: wall {1,6:N2} s | build {2,5:N2} render {3,6:N2} physics {4,5:N2} save {5,5:N2} | {6}" -f $i, $wall, $s.build.sec, $s.render.sec, $s.physics.sec, $s.save.sec, $(($s.PSObject.Properties | Where-Object { -not $_.Value.ok } | ForEach-Object { "$($_.Name) FAILED: $($_.Value.error)" }) -join '; ')
    $rows += [pscustomobject]@{ wall = $wall; render = $s.render.sec; physics = $s.physics.sec; save = $s.save.sec; startup = $wall - ($s.build.sec + $s.render.sec + $s.physics.sec + $s.save.sec) }
    if ($i -eq 0) { "  blender $($r.blender), lowest cube z after physics $($r.physics_lowest_z), .blend size $($r.save_bytes) bytes" }
}
if ($rows.Count -gt 2) { $w = $rows | Select-Object -Skip 1; foreach ($k in 'wall', 'startup', 'render', 'physics', 'save') { "  {0,-8} {1}" -f $k, (Stats ($w | ForEach-Object { $_.$k })) } }

"===== 5. Godot ====="
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $godot) '_sc_'), '')   # self-contained mode: editor data stays next to the exe
$proj = "$work\godot-proj"
& "$lab\tasks\godot\make-project.ps1" -Path $proj
$rows = @()
for ($i = 0; $i -lt $Runs; $i++) {
    if (Test-Path -LiteralPath "$proj\.godot") { Remove-Item -LiteralPath "$proj\.godot" -Recurse -Force }
    Get-ChildItem -LiteralPath $proj -Recurse -Filter *.import | Remove-Item -Force
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $o1 = & $godot --headless --path $proj --import 2>&1
    $import = $sw.Elapsed.TotalSeconds; $ec1 = $LASTEXITCODE
    $imported = @(Get-ChildItem -LiteralPath "$proj\.godot\imported" -ErrorAction SilentlyContinue).Count
    $sw.Restart()
    $o2 = & $godot --headless --path $proj --script res://bench.gd -- "$work\godot-io" 2>&1
    $script = $sw.Elapsed.TotalSeconds
    $line = ($o2 | ForEach-Object { "$_" } | Where-Object { $_ -like 'LABRESULT *' } | Select-Object -First 1)
    if (-not $line) { "run ${i}: import {0:N2} s (exit {1}, {2} imported files); script produced no LABRESULT: {3}" -f $import, $ec1, $imported, (($o2 | Select-Object -Last 3) -join ' / '); continue }
    $g = $line.Substring(10) | ConvertFrom-Json
    "run {0}: import {1,6:N2} s ({2} imported files, exit {3}) | script wall {4,5:N2} s | compute {5,7:N1} ms io {6,7:N1} ms" -f $i, $import, $imported, $ec1, $script, $g.compute_ms, $g.io_ms
    $rows += [pscustomobject]@{ import = $import; script = $script; compute = $g.compute_ms; io = $g.io_ms }
}
if ($rows.Count -gt 2) { $w = $rows | Select-Object -Skip 1; foreach ($k in 'import', 'script', 'compute', 'io') { "  {0,-8} {1}" -f $k, (Stats ($w | ForEach-Object { $_.$k })) } }
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
"done"
