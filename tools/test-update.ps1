# End-to-end test of the participant app's self-update (0.9~). ASCII only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
#
#   powershell -ExecutionPolicy Bypass -File tools\test-update.ps1                 # standard-user path (no UAC; uses __COMPAT_LAYER=RunAsInvoker)
#   powershell -ExecutionPolicy Bypass -File tools\test-update.ps1 -Mode admin     # administrator path: scheduled task + protected-style install copy.
#                                                                                  # From a normal PowerShell it builds first, then asks for elevation once (UAC "Yes").
#                                                                                  # A window that starts in C:\WINDOWS\system32 is not necessarily elevated: the title must say "Administrator".
#
# What it does
#   builds two test builds (127.0.0.1 receiver settings, so --test-* options are allowed): <Old> and <New>,
#   serves the <New> update package from http://127.0.0.1:8788/ and runs the <Old> app against it.
#   The update manifest is signed with the REAL update key (%USERPROFILE%\.pca-study\update-signing.private.pem):
#   the app has no test-only trust setting.
# Scenarios
#   1 update      old app finds, downloads, verifies, probes, swaps, restarts as new; study.json and participant id kept
#   2 downgrade   new app is offered the old version -> ignores it
#   3 rollback    new version "fails to start" (PCBB_TEST_FAIL_VERSION) -> old version is put back and runs; version is remembered as skipped
#   4 skip        the skipped version is not offered again
#   5 tampered    manifest edited after signing -> rejected, nothing downloaded
#   6 bad hash    package changed (same size) -> rejected before unpacking
# Leaves: %LOCALAPPDATA%\pcbb-update-test (delete freely). Touches nothing of a real installation: own data folder, own install folder.
#   -Mode admin additionally creates and removes the scheduled task PcBlackBox\Autostart-<SID>-TEST.
param(
    [ValidateSet('user', 'admin')][string]$Mode = 'user',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'pcbb-update-test'),
    [string]$Old = '0.9.0',
    [string]$New = '0.9.1',
    [int]$Port = 8788,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $env:DOTNET_ROLL_FORWARD) { $env:DOTNET_ROLL_FORWARD = 'Major' }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$needElevation = ($Mode -eq 'admin' -and -not $isAdmin)   # handled after the build below: build as the normal user, then relaunch elevated
if ($Mode -eq 'user') {
    if ($isAdmin) { throw 'Mode user must run from a normal (not elevated) PowerShell.' }
    $env:__COMPAT_LAYER = 'RunAsInvoker'   # the app manifest asks for highestAvailable; this keeps it at the caller's level without a UAC prompt
}

$tools = Join-Path $repo 'participant\StudyTools\bin\Release\net8.0\StudyTools.exe'
$cfg = Join-Path $WorkDir 'cfg\study.test.json'
$data = Join-Path $WorkDir "data-$Mode"
$startup = Join-Path $WorkDir 'startup'
$src = Join-Path $WorkDir 'app'                    # where the "unzipped" old build lives
$install = if ($Mode -eq 'admin') { Join-Path $startup 'install' } else { $src }   # the folder the updater replaces
$feeds = Join-Path $WorkDir 'feeds'
$logs = Join-Path $WorkDir 'logs'
$script:fails = 0
$script:results = @()

$resultFile = Join-Path $logs "result-$Mode.txt"
function Say($m) { Write-Host $m; try { Add-Content -Path $resultFile -Value $m -Encoding UTF8 } catch { } }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if (-not $ok) { $script:fails++ }
    $line = ('  [{0}] {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($detail) { " -- $detail" } else { '' }))
    $script:results += $line
    Say $line
}
function ExeVersion($dir) {
    $exe = Join-Path $dir 'PcBlackBox.exe'
    if (-not (Test-Path $exe)) { return '(none)' }
    return ((Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', '')
}
function TestApps { @(Get-Process PcBlackBox -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($WorkDir, [StringComparison]::OrdinalIgnoreCase) }) }
function Stop-TestApps {
    foreach ($p in (TestApps)) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((TestApps).Count -gt 0 -and $sw.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 300 }
}
function State { $p = Join-Path $data 'update\state.json'; if (Test-Path $p) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } }
function Reset-UpdateState { Remove-Item (Join-Path $data 'update') -Recurse -Force -ErrorAction SilentlyContinue }
function Reset-Install {
    Stop-TestApps
    foreach ($d in @($src, "$src.update-old", "$src.update-new", "$src.update-failed", $install, "$install.update-old", "$install.update-new", "$install.update-failed")) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
    Copy-Item $script:oldApp $src -Recurse
    if ($Mode -eq 'admin') { Copy-Item $script:oldApp $install -Recurse }   # as if autostart had installed the old version earlier
}
function Start-Feed($dir) {
    $p = Start-Process $tools -ArgumentList @('serve', '--dir', "`"$dir`"", '--port', "$Port", '--max-seconds', '900') -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 800
    return $p
}
function Start-App([string]$tag, [string[]]$extra, [string]$exeDir = $src) {
    $out = Join-Path $logs "$tag.out.txt"; $err = Join-Path $logs "$tag.err.txt"
    Remove-Item $out, $err -ErrorAction SilentlyContinue
    $a = @('--data-dir', "`"$data`"", '--test-consent', '--update-feed', "http://127.0.0.1:$Port/update.json") + $extra
    if ($Mode -eq 'admin') { $a += @('--startup-dir', "`"$startup`"") }
    return Start-Process (Join-Path $exeDir 'PcBlackBox.exe') -ArgumentList $a -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
}
function Log([string]$tag) {
    $t = ''
    foreach ($f in @("$tag.out.txt", "$tag.err.txt")) { $p = Join-Path $logs $f; if (Test-Path $p) { $t += (Get-Content $p -Raw -Encoding UTF8) } }
    return "$t"
}
function Wait-Result([int]$sec) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $sec) {
        $s = State
        if ($s -and $s.lastResult) { return $s }
        Start-Sleep -Seconds 2
    }
    return (State)
}
function Sha($f) { (Get-FileHash $f -Algorithm SHA256).Hash }
# Any unexpected error: record it where the caller can see it (the elevated window closes when the script ends), stop test apps, give a distinct exit code.
trap { Say ("ERROR: " + $_ + ' (line ' + $_.InvocationInfo.ScriptLineNumber + ')'); try { Stop-TestApps } catch { }; exit 3 }

# ---------- build ----------
New-Item -ItemType Directory -Force $WorkDir, $logs, $feeds, (Split-Path $cfg) | Out-Null
if (-not $needElevation) { Remove-Item $resultFile -ErrorAction SilentlyContinue; Stop-TestApps }
if (-not $SkipBuild) {
    Say "== build ($Old and $New test builds; about 2 minutes)"
    dotnet build (Join-Path $repo 'participant\StudyTools\StudyTools.csproj') -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'StudyTools build failed' }
    if (-not (Test-Path $cfg)) {
        & $tools keygen --config $cfg --key-dir (Join-Path $WorkDir 'cfg\keys') --endpoint 'http://127.0.0.1:8787/' --contact 'update test' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'test study config failed' }
    }
    $pub = Join-Path $repo 'tools\publish-participant.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $pub -Version $Old -Config $cfg -AllowLoopback -UpdateBaseUrl "http://127.0.0.1:$Port/" -Notes 'old build (downgrade test)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish $Old failed" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $pub -Version $New -Config $cfg -AllowLoopback -UpdateBaseUrl "http://127.0.0.1:$Port/" -Notes 'update test build' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish $New failed" }
}
if ($needElevation) {
    # Not elevated: the builds above were made as the normal user. Run the scenarios in an elevated copy of this script (one UAC prompt).
    Remove-Item $resultFile -ErrorAction SilentlyContinue
    Write-Host 'This window is not elevated. A UAC prompt will appear: choose "Yes". A second window runs the test (about 5 minutes); results are printed here afterwards.'
    $elevArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Mode', 'admin', '-SkipBuild', '-WorkDir', "`"$WorkDir`"", '-Old', $Old, '-New', $New, '-Port', "$Port")
    try { $ep = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $elevArgs }
    catch { Write-Host "Elevation was not granted ($($_.Exception.Message)). Nothing was run."; exit 2 }
    if (Test-Path $resultFile) { Get-Content $resultFile -Encoding UTF8 } else { Write-Host 'The elevated run left no result file.' }
    exit $ep.ExitCode
}
$script:oldApp = (Get-ChildItem (Join-Path $repo "dist\PcBlackBox-$Old-win-x64-LOCALTEST") -Directory | Select-Object -First 1).FullName
$feedNew = Join-Path $repo "dist\update\PcBlackBox-$New-win-x64-LOCALTEST"
$feedOld = Join-Path $repo "dist\update\PcBlackBox-$Old-win-x64-LOCALTEST"
foreach ($p in @($script:oldApp, $feedNew, $feedOld)) { if (-not $p -or -not (Test-Path $p)) { throw "missing build output: $p" } }
$zipNew = (Get-ChildItem $feedNew -Filter '*.zip' | Select-Object -First 1).FullName
Say ("   old app {0} | update package {1} ({2:N1} MB) | mode {3} | elevated {4}" -f (ExeVersion $script:oldApp), (Split-Path $zipNew -Leaf), ((Get-Item $zipNew).Length / 1MB), $Mode, $isAdmin)
& $tools update-verify --dir $feedNew --allow-loopback | Out-Null
Check 'update package verifies (signature, size, hash) and holds no study settings' ($LASTEXITCODE -eq 0) "update-verify exit $LASTEXITCODE"

# ---------- 1. update ----------
Say '== 1. update'
Reset-Install; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
$studyBefore = Sha (Join-Path $install 'study.json')
$feed = Start-Feed $feedNew
$t0 = Get-Date
$p = Start-App 's1' @('--test-update-after', '25')
$s = Wait-Result 420
$took = [int]((Get-Date) - $t0).TotalSeconds
Check 'result is "updated"' ($s -and $s.lastResult -eq 'updated') ("lastResult=$($s.lastResult) detail=$($s.lastDetail) after $took s")
Check "installed version is $New" ((ExeVersion $install) -eq $New) (ExeVersion $install)
Check 'study.json carried over unchanged' ((Test-Path (Join-Path $install 'study.json')) -and (Sha (Join-Path $install 'study.json')) -eq $studyBefore)
Start-Sleep -Seconds 3
$running = TestApps
Check 'new version is running from the install folder' (@($running | Where-Object { $_.Path -eq (Join-Path $install 'PcBlackBox.exe') }).Count -eq 1) (($running | ForEach-Object { $_.Path }) -join '; ')
Check 'previous folder (.update-old) removed after confirmation' (-not (Test-Path "$install.update-old"))
$pidFile = Join-Path $data 'participant.json'
$pid1 = (Get-Content $pidFile -Raw | ConvertFrom-Json).participantId
Check 'participant id present' ([bool]$pid1) $pid1
$h = Join-Path $data 'update\health.json'
Check 'new version reported collector start' ((Test-Path $h) -and (Get-Content $h -Raw | ConvertFrom-Json).ok -eq $true)
Check 'old version verified and probed the package before handing over' ((Log 's1') -match 'probe=ok') ''
Stop-TestApps

# ---------- 2. downgrade ----------
Say '== 2. downgrade offer is ignored'
Stop-Process -Id $feed.Id -ErrorAction SilentlyContinue
Reset-UpdateState
$feed = Start-Feed $feedOld
$p = Start-App 's2' @('--test-update-after', '15', '--test-exit-after', '45') $install
$p.WaitForExit(120000) | Out-Null
Check "still $New" ((ExeVersion $install) -eq $New) (ExeVersion $install)
Check 'nothing downloaded' (-not ((Log 's2') -match '\[update\].*MB\)')) ''
Check 'the app did check and saw only the lower version' ((Log 's2') -match "current=$New offered=$Old") ''
Check 'participant id unchanged across update' ((Get-Content $pidFile -Raw | ConvertFrom-Json).participantId -eq $pid1)
Stop-TestApps
Stop-Process -Id $feed.Id -ErrorAction SilentlyContinue

# ---------- 3. rollback ----------
Say '== 3. new version fails to start -> rollback'
Reset-Install; Reset-UpdateState
$feed = Start-Feed $feedNew
$env:PCBB_TEST_FAIL_VERSION = $New
$p = Start-App 's3' @('--test-update-after', '25')
$s = Wait-Result 420
$env:PCBB_TEST_FAIL_VERSION = $null
Check 'result is "rolled-back"' ($s -and $s.lastResult -eq 'rolled-back') ("lastResult=$($s.lastResult) detail=$($s.lastDetail)")
Check "installed version is back to $Old" ((ExeVersion $install) -eq $Old) (ExeVersion $install)
Check "version $New remembered as skipped" ($s -and $s.skipVersion -eq $New) "skipVersion=$($s.skipVersion)"
Start-Sleep -Seconds 4
Check 'old version is running again' (@(TestApps | Where-Object { $_.Path -eq (Join-Path $install 'PcBlackBox.exe') }).Count -eq 1) ((TestApps | ForEach-Object { $_.Path }) -join '; ')
Check 'no leftover folders (.update-old, -failed, -new)' (-not ((Test-Path "$install.update-old") -or (Test-Path "$install.update-failed") -or (Test-Path "$install.update-new")))
Check 'study.json still there' ((Sha (Join-Path $install 'study.json')) -eq $studyBefore)
Stop-TestApps

# ---------- 4. skipped version is not offered again ----------
Say '== 4. skipped version is not offered again'
$p = Start-App 's4' @('--test-update-after', '15', '--test-exit-after', '45') $(if ($Mode -eq 'admin') { $install } else { $src })
$p.WaitForExit(120000) | Out-Null
Check "still $Old" ((ExeVersion $install) -eq $Old) (ExeVersion $install)
Check 'nothing downloaded' (-not ((Log 's4') -match '\[update\].*MB\)')) ''
Check 'the app did check and skipped that version' ((Log 's4') -match "current=$Old offered=$New skip=$New") ''
Stop-TestApps
Stop-Process -Id $feed.Id -ErrorAction SilentlyContinue

# ---------- 5. tampered manifest ----------
Say '== 5. manifest edited after signing'
Reset-UpdateState
$bad = Join-Path $feeds 'tampered'; Remove-Item $bad -Recurse -Force -ErrorAction SilentlyContinue; Copy-Item $feedNew $bad -Recurse
$mf = Join-Path $bad 'update.json'
$txt = [IO.File]::ReadAllText($mf); [IO.File]::WriteAllText($mf, $txt.Replace("`"$New`"", '"9.9.9"'), (New-Object Text.UTF8Encoding($false)))
$feed = Start-Feed $bad
$p = Start-App 's5' @('--test-update-after', '15', '--test-exit-after', '45') $(if ($Mode -eq 'admin') { $install } else { $src })
$p.WaitForExit(120000) | Out-Null
Check "still $Old" ((ExeVersion $install) -eq $Old) (ExeVersion $install)
Check 'rejected at the signature check' ((Log 's5') -match 'CryptographicException') ''
Check 'nothing downloaded' (-not (Test-Path (Join-Path $data 'update\9.9.9')) -and -not ((Log 's5') -match '\[update\].*MB\)'))
Stop-TestApps
Stop-Process -Id $feed.Id -ErrorAction SilentlyContinue

# ---------- 6. package changed, same size ----------
Say '== 6. package with a flipped byte'
Reset-UpdateState
$bad = Join-Path $feeds 'badhash'; Remove-Item $bad -Recurse -Force -ErrorAction SilentlyContinue; Copy-Item $feedNew $bad -Recurse
$bz = (Get-ChildItem $bad -Filter '*.zip' | Select-Object -First 1).FullName
$fs = [IO.File]::Open($bz, 'Open', 'ReadWrite'); $fs.Position = [long]($fs.Length / 2); $b = $fs.ReadByte(); $fs.Position -= 1; $fs.WriteByte(($b -bxor 0xFF)); $fs.Close()
$feed = Start-Feed $bad
$p = Start-App 's6' @('--test-update-after', '15', '--test-exit-after', '40') $(if ($Mode -eq 'admin') { $install } else { $src })   # exits before the app's own 45 s cleanup, so the check below is about the failed staging itself
$p.WaitForExit(150000) | Out-Null
Check "still $Old" ((ExeVersion $install) -eq $Old) (ExeVersion $install)
Check 'rejected at the SHA-256 check' ((Log 's6') -match 'SHA-256') ''
Check 'never unpacked or probed, and the rejected download was deleted' (-not ((Log 's6') -match 'probe=ok') -and -not (Test-Path (Join-Path $data "update\$New")))
Stop-TestApps
Stop-Process -Id $feed.Id -ErrorAction SilentlyContinue

# ---------- cleanup ----------
if ($Mode -eq 'admin') {
    Say '== cleanup: withdraw (removes the -TEST scheduled task and the test install copy)'
    $p = Start-Process (Join-Path $src 'PcBlackBox.exe') -PassThru -ArgumentList @('--data-dir', "`"$data`"", '--startup-dir', "`"$startup`"", '--test-withdraw-after', '15')
    $p.WaitForExit(120000) | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    # through cmd: schtasks writes "ERROR: ..." to stderr when the task is gone, and PowerShell 5.1 turns a redirected stderr line
    # into a terminating error under $ErrorActionPreference = 'Stop' (this stopped the first elevated run on 2026-11-11 after all checks had passed)
    cmd /c "schtasks /Query /TN `"PcBlackBox\Autostart-$sid-TEST`" >nul 2>&1"
    Check 'test scheduled task removed' ($LASTEXITCODE -ne 0) "schtasks query exit $LASTEXITCODE"
    Check 'test install copy and test data removed' (-not (Test-Path $install) -and -not (Test-Path (Join-Path $data 'participant.json')))
}
Stop-TestApps
Say ''
Say ("RESULT: {0} checks, {1} failed  (mode {2}, logs in {3})" -f $script:results.Count, $script:fails, $Mode, $logs)
exit $(if ($script:fails -eq 0) { 0 } else { 1 })
