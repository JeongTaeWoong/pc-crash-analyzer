# Dry run of the whole session logic without restarting, locking or installing anything.
# "Next boot" is played by calling run-next.ps1 again. Uses a shortened copy of lab.json and a throwaway runs folder.
# Takes about 15 minutes; Notepad++ windows come to the front. Prints PASS/FAIL per expectation.
param([string]$Apps = 'notepadpp', [string]$Sections = '1,2,3,4,5,6')   # -Sections 5,6 reuses the runs of an earlier full pass
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lab = Split-Path -Parent $PSScriptRoot
$root = Join-Path $env:LOCALAPPDATA 'Temp\pca-lab-sm-test'
$do = @($Sections -split ',' | ForEach-Object { [int]$_ })
if ($do -contains 2) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $root | Out-Null
$cfg = Get-Content -Raw -Encoding UTF8 "$lab\lab.json" | ConvertFrom-Json
$cfg.pilot.boots = 2; $cfg.k_min = 2; $cfg.k_max = 2; $cfg.default_plan.k = 2
$cfgPath = "$root\lab.test.json"
[IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
$env:PCA_LAB_CONFIG = $cfgPath
$runs = "$root\runs"
$script:fail = 0
function Check([string]$what, [bool]$ok, [string]$detail) { if ($ok) { "PASS  $what" } else { $script:fail++; "FAIL  $what  <- $detail" } }
function State { Get-Content -Raw -Encoding UTF8 "$runs\state.json" | ConvertFrom-Json }
function Step { & powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\run-next.ps1" -RunsRoot $runs -Simulate -Test -TestApps $Apps | Out-Null; return $LASTEXITCODE }
function Drive([int]$max = 30) { $codes = @(); for ($i = 0; $i -lt $max; $i++) { $c = Step; $codes += $c; if ($c -ne 10) { break } }; return $codes }
$startupFile = Join-Path ([Environment]::GetFolderPath('Startup')) 'pca-lab-run-next.cmd'

if ($do -contains 1) {
"=== 1. no session: run-next does nothing ==="
New-Item -ItemType Directory -Force $runs | Out-Null
$c = Step; Check 'exits 0 without state' ($c -eq 0) "exit $c"

}
if ($do -contains 2) {
"=== 2. session without -AllowInstall: pilot -> plan -> baseline -> stops before install ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\start-session.ps1" -RunsRoot $runs -Simulate -Test -Hours 100 | Out-Null
Check 'start-session (simulate) exit 0' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
Check 'simulate does not create the logon launcher' (-not (Test-Path -LiteralPath $startupFile)) 'file exists'
$codes = Drive
$s = State
Check 'ended by itself' ($codes[-1] -eq 0) ("codes " + ($codes -join ','))
Check 'pilot boots = 2' (@($s.boots | Where-Object { $_.phase -eq 'pilot' -and $_.status -eq 'complete' }).Count -eq 2) ''
Check 'plan written from pilot' ($s.plan -and (Test-Path "$runs\plan.json")) "plan=$($s.plan | ConvertTo-Json -Compress -Depth 3)"
Check 'baseline boots = k' (@($s.boots | Where-Object { $_.phase -eq 's0' -and $_.status -eq 'complete' }).Count -eq [int]$s.plan.k) "k=$($s.plan.k)"
Check 'phase waits at install' ($s.phase -eq 'install') "phase=$($s.phase)"
Check 'session closed, reason mentions -AllowInstall' ((-not $s.session.active) -and $s.lastMessage -like '*-AllowInstall*') $s.lastMessage
Check 'nothing installed' (-not (Test-Path "$runs\install.done")) 'marker exists'
Check 'one block per simulated boot' (@($s.boots | Group-Object bootTime | Where-Object Count -gt 1).Count -eq 0) 'two blocks share a boot'
"   plan: k=$($s.plan.k) reps_main=$($s.plan.reps_main) reps_sub=$($s.plan.reps_sub) source=$($s.plan.source)"

}
if ($do -contains 3) {
"=== 3a. the install step reports failure: session ends, nothing is measured as 'after' ==="
$env:PCA_LAB_SIM_INSTALL = 'fail'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\start-session.ps1" -RunsRoot $runs -Simulate -Test -Hours 100 -AllowInstall | Out-Null
$codes = Drive
Remove-Item Env:\PCA_LAB_SIM_INSTALL
$s = State
Check 'session ended with the failure reason' ((-not $s.session.active) -and $s.lastMessage -like 'install step failed*') $s.lastMessage
Check 'phase stays at install, no settling boot' (($s.phase -eq 'install') -and [int]$s.settleBootsDone -eq 0) "phase=$($s.phase) settle=$($s.settleBootsDone)"
"=== 3. session with -AllowInstall but only 5 minutes: install (simulated) + settling boots, then no time for a block ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\start-session.ps1" -RunsRoot $runs -Simulate -Test -Hours 0.08 -AllowInstall | Out-Null
$codes = Drive
$s = State
Check 'install marker is the simulated one' ((Test-Path "$runs\install.done") -and (Get-Content -Raw "$runs\install.done") -like '*simulated*') ''
Check 'settling boots done = 2' ([int]$s.settleBootsDone -eq 2) "$($s.settleBootsDone)"
Check 'phase is after-install' ($s.phase -eq 's1') $s.phase
Check 'ended for lack of time, no after-install block' (($s.lastMessage -like 'not enough time*') -and @($s.boots | Where-Object phase -eq 's1').Count -eq 0) $s.lastMessage

}
if ($do -contains 4) {
"=== 4. long session: after-install blocks until complete ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\start-session.ps1" -RunsRoot $runs -Simulate -Test -Hours 100 -AllowInstall | Out-Null
Check 'start-session accepted the post-install state as the new reference' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
$codes = Drive
$s = State
Check 'after-install boots = k' (@($s.boots | Where-Object { $_.phase -eq 's1' -and $_.status -eq 'complete' }).Count -eq [int]$s.plan.k) ''
Check 'phase done' ($s.phase -eq 'done') $s.phase
Check 'four sessions recorded' (@($s.sessions).Count -eq 4) "$(@($s.sessions).Count)"
$ErrorActionPreference = 'Continue'      # the refusal below is the expected result, not a test error
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\start-session.ps1" -RunsRoot $runs -Simulate -Test -Hours 1 2>$null | Out-Null
$refused = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
Check 'a finished experiment refuses a new session' ($refused -ne 0) "exit $refused"
}
if ($do -contains 5) {

"=== 5. stop in the middle of a block ==="
$runs2 = "$root\runs-stop"; Remove-Item -LiteralPath $runs2 -Recurse -Force -ErrorAction SilentlyContinue
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\start-session.ps1" -RunsRoot $runs2 -Simulate -Test -Hours 100 | Out-Null
$p = Start-Process powershell -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$lab\run-next.ps1`"", '-RunsRoot', "`"$runs2`"", '-Simulate', '-Test', '-TestApps', $Apps
Start-Sleep -Seconds 25
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\stop-session.ps1" -RunsRoot $runs2 | Out-Null
$exited = $p.WaitForExit(120000)
$s2 = Get-Content -Raw -Encoding UTF8 "$runs2\state.json" | ConvertFrom-Json
Check 'run-next ended within 2 minutes of the stop request' $exited ''
Check 'session closed as stopped' ((-not $s2.session.active) -and $s2.lastMessage -like 'stopped on request*') $s2.lastMessage
Check 'unfinished block not counted and its folder removed' ((@($s2.boots).Count -eq 0) -and -not (Test-Path "$runs2\boots\pilot-01")) "boots=$(@($s2.boots).Count)"
Check 'STOP flag cleared' (-not (Test-Path "$runs2\STOP")) ''
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\start-session.ps1" -RunsRoot $runs2 -Simulate -Test -Hours 100 | Out-Null
$c = & powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\run-next.ps1" -RunsRoot $runs2 -Simulate -Test -TestApps $Apps | Out-Null; $c = $LASTEXITCODE
$s2 = Get-Content -Raw -Encoding UTF8 "$runs2\state.json" | ConvertFrom-Json
Check 'next session measures pilot-01 again' (($c -eq 10) -and @($s2.boots | Where-Object { $_.id -eq 'pilot-01' -and $_.status -eq 'complete' }).Count -eq 1) "exit $c"

}
if ($do -contains 6) {
"=== 6. analysis ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\analyze.ps1" -RunsRoot $runs
Check 'report written' (Test-Path "$runs\report.md") ''
Check 'no lab browser or app left running' (@(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like '*pca-lab*' -and $_.Name -in 'chrome.exe', 'notepad++.exe', 'blender.exe' }).Count -eq 0) ''
Check 'logon launcher absent' (-not (Test-Path -LiteralPath $startupFile)) ''
}
Remove-Item Env:\PCA_LAB_CONFIG
"`nRESULT: $(if ($script:fail) { "$script:fail FAILED" } else { 'all passed' })"
