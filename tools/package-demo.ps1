# Demo package: one folder that runs on a PC without .NET, without the Visual C++ runtime and without the repository
# (a teammate's laptop at a presentation). ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File tools\package-demo.ps1               # build, self-check, zip
#   powershell -ExecutionPolicy Bypass -File tools\package-demo.ps1 -Rehearse     # + on this PC: collector -> crash -> automatic diagnosis
#   powershell -ExecutionPolicy Bypass -File tools\package-demo.ps1 -DemoKit      # + demo\ : Godot 4.3 and the project of a documented bug
#   other switches: -Version 2026.11.20  -FixedGodot <folder>  -DemoSource <folder>  -SkipEngineBuild  -NoZip
#
# Result: dist\PcCrashAnalyzer-<Version>-win-x64\ and its .zip + .zip.sha256.txt (dist\ is not committed).
#
# The folder repeats the repository's layout (engine\build\Release, collector\BlackBox\bin\Release\net8.0-windows)
# because the app finds the engine and the collector by walking up to a folder that holds 'engine' and 'collector'
# (ui/wpf/CrashAnalyzer.UI/Services/RepoPaths.cs). So nothing in the app had to change for the package.
#
# The script registers nothing, installs nothing and puts no API key into the package. The self-check runs the
# packaged programs with DOTNET_ROLL_FORWARD, CE_ENGINE and CE_DUMPS removed, so a pass means the folder stands alone.
# -Rehearse crashes collector\experiments\CrashDummy (our own test program) once on this PC; the record it leaves in
# the package folder holds this PC's process names and is deleted again before the zip is made.
# -DemoKit copies Godot 4.3 (MIT licence, official build, signature checked) and the sample of lab scenario
# real-godot-svg from the lab's package sources (<vm root>\payload\packages\src). Nothing is downloaded here.
# -FixedGodot adds a Godot build that has the fix (run it in a lab guest first: lab\vm\plans\fix-1.json).
param(
    [string]$Version = (Get-Date).ToString('yyyy.MM.dd'),
    [switch]$Rehearse,
    [switch]$DemoKit,
    [string]$DemoSource,
    [string]$FixedGodot,
    [switch]$SkipEngineBuild,
    [switch]$NoZip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tpl = Join-Path $PSScriptRoot 'demo-package'
$name = "PcCrashAnalyzer-$Version-win-x64"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
$utf8Bom = New-Object Text.UTF8Encoding($true)
function Step([string]$m) { Write-Host ("[{0}] {1}" -f (Get-Date).ToString('HH:mm:ss'), $m) }
function Find-VsPath {
    $vsw = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vsw)) { return $null }
    return @(& $vsw -latest -prerelease -products * -property installationPath) | Where-Object { $_ } | Select-Object -First 1
}
function Assert-MicrosoftSigned([string]$file) {
    $sig = Get-AuthenticodeSignature $file
    if ($sig.Status -ne 'Valid' -or "$($sig.SignerCertificate.Subject)" -notmatch 'O=Microsoft Corporation') { throw "not a valid Microsoft signature: $file ($($sig.Status))" }
}
function Publish-Project([string]$proj, [string]$to) {
    $a = @('publish', (Join-Path $root $proj), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $to, '-p:PublishSingleFile=false', '-p:DebugType=none', '--nologo', '-v', 'q')
    if ($Version -match '^\d+(\.\d+){1,3}$') { $a += "-p:Version=$Version" }
    $log = & dotnet @a | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "dotnet publish failed ($LASTEXITCODE): $proj" }
    $cfg = Get-ChildItem $to -Filter *.runtimeconfig.json | Select-Object -First 1
    if (-not $cfg -or -not (Get-Content $cfg.FullName -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks) { throw "not a self-contained publish: $proj" }
}

$busy = @(Get-Process CrashAnalyzerUI, BlackBox -ErrorAction SilentlyContinue | Where-Object { "$($_.Path)" -like "$out\*" })
if ($busy.Count) { throw "close the app first: $($busy.Name -join ', ') is running from $out" }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$appDir = Join-Path $out 'app'
$engDir = Join-Path $out 'engine\build\Release'
$colDir = Join-Path $out 'collector\BlackBox\bin\Release\net8.0-windows'
$cliDir = Join-Path $out 'tools\ce-ai'
New-Item -ItemType Directory -Force -Path $appDir, $engDir, $colDir, $cliDir, (Join-Path $out 'engine\data\samples') | Out-Null

# ---- engine (C++) and the Visual C++ runtime DLLs it imports --------------------------------------------------------
$engineExe = Join-Path $root 'engine\build\Release\crash_engine.exe'
if (-not $SkipEngineBuild) {
    Step 'building the engine (Release)'
    $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
    if (-not $cmake) { $vs = Find-VsPath; if ($vs) { $c = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'; if (Test-Path $c) { $cmake = $c } } }
    if (-not $cmake) { throw 'cmake not found (PATH or Visual Studio). Build the engine by hand and pass -SkipEngineBuild.' }
    $build = Join-Path $root 'engine\build'
    if (-not (Test-Path (Join-Path $build 'CMakeCache.txt'))) {
        $log = & $cmake -S (Join-Path $root 'engine') -B $build | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "cmake configure failed: $LASTEXITCODE" }
    }
    $log = & $cmake --build $build --config Release --target crash_engine | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "engine build failed: $LASTEXITCODE" }
}
if (-not (Test-Path $engineExe)) { throw "engine not built: $engineExe" }
Copy-Item $engineExe $engDir
# A fresh Windows has no VCRUNTIME140.dll / MSVCP140.dll (a lab guest ended a /MD program with 0xC0000135 on 2026-11-12).
# Microsoft's own DLLs are put next to the exe (app-local), taken from the Visual Studio redistributable folder.
$imageText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($engineExe))
$crtNames = 'MSVCP140.dll', 'MSVCP140_1.dll', 'MSVCP140_2.dll', 'MSVCP140_ATOMIC_WAIT.dll', 'MSVCP140_CODECVT_IDS.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'VCRUNTIME140_THREADS.dll', 'CONCRT140.dll'
$crtNeeded = @($crtNames | Where-Object { $imageText.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
$crtFrom = $null
if ($crtNeeded.Count) {
    $vs = Find-VsPath
    $dirs = @()
    if ($vs) {
        $dirs = @(Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
            ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue })
    }
    foreach ($d in $dirs) { if (-not @($crtNeeded | Where-Object { -not (Test-Path (Join-Path $d.FullName $_)) }).Count) { $crtFrom = $d.FullName; break } }
    if (-not $crtFrom) { throw "crash_engine.exe imports $($crtNeeded -join ', ') and no Visual Studio redistributable folder has them: the package would not run on a fresh Windows" }
    foreach ($n in $crtNeeded) { $src = Join-Path $crtFrom $n; Assert-MicrosoftSigned $src; Copy-Item $src $engDir }
    Step "engine: crash_engine.exe + $($crtNeeded.Count) runtime DLLs (app-local)"
} else { Step 'engine: crash_engine.exe (static runtime)' }
Copy-Item (Join-Path $root 'engine\data\samples\*.json') (Join-Path $out 'engine\data\samples')

# ---- the .NET programs, self-contained ------------------------------------------------------------------------------
Step 'publishing the app'
Publish-Project 'ui\wpf\CrashAnalyzer.UI\CrashAnalyzer.UI.csproj' $appDir
Step 'publishing the collector'
Publish-Project 'collector\BlackBox\BlackBox.csproj' $colDir
Step 'publishing ce-ai'
Publish-Project 'ui\wpf\CrashAnalyzer.Ai.Cli\CrashAnalyzer.Ai.Cli.csproj' $cliDir

# ---- demo kit (optional) --------------------------------------------------------------------------------------------
$demoInfo = $null
$demoSection = ''
if ($DemoKit) {
    Step 'adding the demo kit'
    if (-not $DemoSource) { $DemoSource = Join-Path (Get-Content (Join-Path $root 'lab\vm\vm.json') -Raw -Encoding UTF8 | ConvertFrom-Json).root 'payload\packages\src' }
    $godotSrc = Join-Path $DemoSource 'godot-4.3'
    $sampleSrc = Join-Path $DemoSource 'godot-sample'
    $godotExe = 'Godot_v4.3-stable_win64.exe'
    if (-not (Test-Path (Join-Path $godotSrc $godotExe)) -or -not (Test-Path (Join-Path $sampleSrc 'project.godot'))) { throw "demo sources missing under $DemoSource (godot-4.3, godot-sample)" }
    $sig = Get-AuthenticodeSignature (Join-Path $godotSrc $godotExe)
    if ($sig.Status -ne 'Valid') { throw "Godot 4.3: signature is $($sig.Status)" }
    $demo = Join-Path $out 'demo'
    New-Item -ItemType Directory -Force -Path (Join-Path $demo 'godot'), (Join-Path $demo 'project-original') | Out-Null
    Copy-Item (Join-Path $godotSrc '*') (Join-Path $demo 'godot') -Recurse
    Copy-Item (Join-Path $sampleSrc '*') (Join-Path $demo 'project-original') -Recurse
    $cache = Join-Path $demo 'project-original\.godot'                  # the import cache is left out, as in the lab package
    if (Test-Path $cache) { Remove-Item -Recurse -Force $cache }
    Copy-Item (Join-Path $tpl 'demo\1-Crash.bat') $demo
    $demoInfo = [ordered]@{ scenario = 'real-godot-svg'; issue = 'https://github.com/godotengine/godot/issues/98341'; godot = $godotExe; godot_signer = "$($sig.SignerCertificate.Subject)"; fixed = $null }
    # the guide's demo part has one paragraph for a package with the fixed Godot and one for a package without it
    $demoSection = [IO.File]::ReadAllText((Join-Path $tpl 'README-demo-section.txt'), [Text.Encoding]::UTF8)
    $drop = 'FIXED_YES'; $keep = 'FIXED_NO'
    if ($FixedGodot) {
        $fx = @(Get-ChildItem $FixedGodot -Filter 'Godot_v*_win64.exe' | Select-Object -First 1)
        if (-not $fx.Count) { throw "no Godot_v*_win64.exe under $FixedGodot" }
        $fsig = Get-AuthenticodeSignature $fx[0].FullName
        if ($fsig.Status -ne 'Valid') { throw "fixed Godot: signature is $($fsig.Status)" }
        New-Item -ItemType Directory -Force -Path (Join-Path $demo 'godot-fixed') | Out-Null
        Copy-Item (Join-Path $FixedGodot '*') (Join-Path $demo 'godot-fixed') -Recurse
        $bat = (Get-Content (Join-Path $tpl 'demo\2-Fixed.bat') -Raw).Replace('{FIXED_EXE}', $fx[0].Name)
        [IO.File]::WriteAllText((Join-Path $demo '2-Fixed.bat'), $bat, [Text.Encoding]::ASCII)
        $demoInfo.fixed = $fx[0].Name
        $drop = 'FIXED_NO'; $keep = 'FIXED_YES'
        $demoSection = $demoSection.Replace('{FIXED_EXE}', $fx[0].Name)
    }
    $demoSection = [regex]::Replace($demoSection, "(?s)\{$drop\}.*?\{/$drop\}", '').Replace("{$keep}", '').Replace("{/$keep}", '')
}

# ---- launchers, guide -----------------------------------------------------------------------------------------------
Copy-Item (Join-Path $tpl 'Start.bat'), (Join-Path $tpl 'Start-Admin.bat'), (Join-Path $tpl 'Check.bat') $out
$commit = (& git -c safe.directory=* -C $root rev-parse --short HEAD | Out-String).Trim()
$dirty = [bool](& git -c safe.directory=* -C $root status --porcelain -- collector engine ui | Out-String).Trim()
$builtAt = Get-Date
$readme = [IO.File]::ReadAllText((Join-Path $tpl 'README.txt'), [Text.Encoding]::UTF8)
$readme = $readme.Replace('{VERSION}', $Version).Replace('{BUILT_AT}', $builtAt.ToString('yyyy-MM-dd HH:mm')).Replace('{COMMIT}', $(if ($dirty) { "$commit + uncommitted changes" } else { $commit })).Replace('{DEMO_SECTION}', $demoSection)
[IO.File]::WriteAllText((Join-Path $out 'README.txt'), $readme.Replace("`r`n", "`n").Replace("`n", "`r`n"), $utf8Bom)

# ---- self-check: the packaged programs, with nothing borrowed from this PC's developer setup --------------------------
Step 'self-check'
$saved = @{}
foreach ($v in 'DOTNET_ROLL_FORWARD', 'DOTNET_ROOT', 'CE_ENGINE', 'CE_DUMPS') { $saved[$v] = [Environment]::GetEnvironmentVariable($v, 'Process'); [Environment]::SetEnvironmentVariable($v, $null, 'Process') }
$checks = [ordered]@{}
try {
    $txt = Join-Path $env:TEMP ("pca_pkg_selftest_{0}.txt" -f $PID)
    if (Test-Path $txt) { Remove-Item -Force $txt }
    $p = Start-Process (Join-Path $appDir 'CrashAnalyzerUI.exe') -ArgumentList '--selftest', "`"$txt`"" -WorkingDirectory $out -PassThru
    $null = $p.Handle                                                # without this the exit code is not kept
    if (-not $p.WaitForExit(120000)) { try { $p.Kill() } catch { }; throw 'self-check: the app did not end in 120 s' }
    $line = $(if (Test-Path $txt) { (Get-Content $txt -Raw -Encoding UTF8).Trim() } else { '(no result file: the app did not start)' })
    $checks.app_selftest = $line
    if ($p.ExitCode -ne 0 -or $line -notmatch '^OK ') { throw "self-check failed: $line" }
    Remove-Item -Force $txt
    Step "  app + engine: $line"

    $log = & (Join-Path $colDir 'BlackBox.exe') --verify-eventlog 1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "self-check failed: the collector ended with $LASTEXITCODE" }
    $checks.collector = 'starts and reads the event log'
    Step '  collector: starts and reads the event log'

    $log = & (Join-Path $cliDir 'ce-ai.exe') bundle (Join-Path $out 'engine\data\samples\s19_silent_exit.json') | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "self-check failed: ce-ai bundle ended with $LASTEXITCODE" }
    $checks.ce_ai = 'builds the evidence bundle of an example record'
    Step '  ce-ai: builds the evidence bundle of an example record (nothing is sent)'

    if ($Rehearse) {
        # End to end on this PC: the packaged app starts the packaged collector, our own test program crashes,
        # the collector writes a record and the app shows its diagnosis. Off-screen; no input is sent to anything.
        $other = @(Get-Process PcBlackBox, BlackBox, CrashAnalyzerUI -ErrorAction SilentlyContinue)
        if ($other.Count) { throw "rehearsal not started: $($other.Name -join ', ') is running (two collectors would both record the crash)" }
        $tmp = Join-Path $env:TEMP 'pca_pkg_rehearse'
        if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
        New-Item -ItemType Directory -Force -Path $tmp | Out-Null
        Step '  rehearsal: building the test program'
        $log = & dotnet publish (Join-Path $root 'collector\experiments\CrashDummy\CrashDummy.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $tmp 'dummy') -p:DebugType=none --nologo -v q | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $log; throw 'rehearsal: CrashDummy did not build' }
        $png = Join-Path $dist "$name-rehearsal.png"
        $rtxt = Join-Path $tmp 'rehearse.txt'
        Step '  rehearsal: app -> collector -> crash in 15 s -> diagnosis (up to 2 minutes)'
        $ui = Start-Process (Join-Path $appDir 'CrashAnalyzerUI.exe') -ArgumentList '--rehearse', "`"$png`"", "`"$rtxt`"" -WorkingDirectory $out -PassThru
        Start-Sleep -Seconds 5
        Start-Process (Join-Path $tmp 'dummy\CrashDummy.exe') -ArgumentList 'c' -WindowStyle Minimized | Out-Null
        if (-not $ui.WaitForExit(150000)) { try { $ui.Kill() } catch { }; throw 'rehearsal: the app did not end in 150 s' }
        # plain strings only: a line from Get-Content carries its file path, and the other lines name what ran on this PC
        $lines = $(if (Test-Path $rtxt) { @(Get-Content $rtxt -Encoding UTF8 | ForEach-Object { "$_" }) } else { @('(no result file)') })
        $checks.rehearsal = @($lines | Where-Object { $_ -match '^(collector_started|auto_diagnosed|trigger_in_log|mark_in_log|dump_in_log|error)=' })
        $lines | ForEach-Object { Step "    $_" }
        Get-Process BlackBox -ErrorAction SilentlyContinue | Where-Object { "$($_.Path)" -like "$out\*" } | ForEach-Object { try { $_.Kill() } catch { } }
        if ($lines -notcontains 'auto_diagnosed=True') { throw 'rehearsal failed: the crash was not diagnosed automatically' }
        Step "  rehearsal: passed (screen: $png)"
    }
} finally {
    foreach ($v in $saved.Keys) { [Environment]::SetEnvironmentVariable($v, $saved[$v], 'Process') }
    # records made by a check hold this PC's process names: they never travel in the package
    foreach ($d in (Join-Path $out 'collector\BlackBox\dumps'), (Join-Path $out 'collector\BlackBox\logs')) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
}
$left = @(Get-ChildItem $out -Recurse -Force | Where-Object { $_.Name -in 'dumps', 'logs', 'engine-snapshot.json', 'ai-key.bin' -or $_.Extension -in '.dmp', '.etl', '.pem', '.apikey' })
if ($left.Count) { throw "the package holds files that must not travel: $(@($left | ForEach-Object { $_.FullName }) -join ', ')" }

# ---- package.json, zip ----------------------------------------------------------------------------------------------
$files = @(Get-ChildItem $out -Recurse -File)
[ordered]@{
    name = $name; version = $Version; built_at = $builtAt.ToString('o'); commit = $commit; uncommitted_changes = $dirty
    engine_runtime_dlls = $crtNeeded; checks = $checks; demo_kit = $demoInfo
    files = $files.Count; mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
} | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $out 'package.json') -Encoding UTF8
# nothing written by this script may name this PC's user (2026-11-17: the first package.json held a profile path)
foreach ($f in 'package.json', 'README.txt') {
    $t = [IO.File]::ReadAllText((Join-Path $out $f))
    if ($t.IndexOf($env:USERNAME, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $t.IndexOf($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "$f names this PC's user: not packaged" }
}
if ($NoZip) { Step "done: $out ($($files.Count) files, no zip)"; exit 0 }
Step 'zipping'
$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $name.zip" | Set-Content -Path "$zip.sha256.txt" -Encoding ASCII
Step ("done: {0} ({1:N1} MB, {2} files, sha256 {3})" -f $zip, ((Get-Item $zip).Length / 1MB), ($files.Count + 1), $hash.Substring(0, 16))
