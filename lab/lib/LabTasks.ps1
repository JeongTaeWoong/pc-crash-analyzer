# Foreground workloads and background browser for the lab. Dot-source after LabNative.ps1.
# Every function takes $Lab (from Get-LabContext in LabCommon.ps1) and returns an ordered hashtable of metrics.
# A step that cannot be measured is recorded as {status:'unavailable', reason:...} instead of throwing.

function New-LabUnavailable([string]$reason) { return [ordered]@{ status = 'unavailable'; reason = $reason } }

# Starts a GUI process and waits until its main window exists and answers two WM_NULL probes in under 100 ms.
# Returns process, window handle and the time from launch to that point.
function Start-LabGuiApp {
    param([string]$Exe, [string]$Arguments, [int]$TimeoutSec = 60, [string]$TempDir)
    $psi = New-Object Diagnostics.ProcessStartInfo $Exe, $Arguments
    $psi.UseShellExecute = $false
    if ($TempDir) {
        # keep what the app drops in its temp folder (Blender writes quit.blend on exit) inside the lab work folder
        if (-not (Test-Path -LiteralPath $TempDir)) { New-Item -ItemType Directory -Force $TempDir | Out-Null }
        $psi.EnvironmentVariables['TEMP'] = $TempDir; $psi.EnvironmentVariables['TMP'] = $TempDir
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    $h = [IntPtr]::Zero; $good = 0
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec -and -not $p.HasExited) {
        if ($h -eq [IntPtr]::Zero) { $h = [LabNative]::MainWindowOf($p.Id) }
        if ($h -ne [IntPtr]::Zero) {
            $ok = $false; $ms = [LabNative]::ProbeMs($h, 500, [ref]$ok)
            if ($ok -and $ms -lt 100) { $good++ } else { $good = 0 }
            if ($good -ge 2) { break }
        }
        Start-Sleep -Milliseconds 20
    }
    return [pscustomobject]@{ Process = $p; Window = $h; ReadyMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1); Ready = ($good -ge 2) }
}

function Stop-LabApp([Diagnostics.Process]$p, [IntPtr]$hwnd, [int]$WaitSec = 10) {
    if ($p.HasExited) { return 'exited' }
    if ($hwnd -ne [IntPtr]::Zero) { [LabNative]::CloseWindow($hwnd) }
    if ($p.WaitForExit($WaitSec * 1000)) { return 'closed' }
    try { $p.Kill(); [void]$p.WaitForExit(5000) } catch { }
    return 'killed'
}

# ---- Blender ---------------------------------------------------------------------------------------------------------
function Invoke-LabBlender($Lab) {
    $m = [ordered]@{}
    $exe = $Lab.Tools.blender
    $work = Join-Path $Lab.Work 'blender'
    # 1) GUI start: launch -> window answers. The app quits itself 4 s after its Python starts.
    $expr = 'import bpy; bpy.app.timers.register(lambda: bpy.ops.wm.quit_blender() and None, first_interval=4.0)'
    $g = Start-LabGuiApp -Exe $exe -Arguments ('--factory-startup --window-geometry 60 60 1280 800 --python-expr "{0}"' -f $expr) -TimeoutSec 90 -TempDir (Join-Path $Lab.Work 'blender-tmp')
    if ($g.Ready) {
        $m.gui_start_ms = $g.ReadyMs
        [LabNative]::StartProbe($g.Window, 100)
        if (-not $g.Process.WaitForExit(30000)) { $m.gui_close = Stop-LabApp $g.Process $g.Window 5 } else { $m.gui_close = 'self' }
        $m.gui_probe = Get-LabProbeSummary ([LabNative]::StopProbe()) ([LabNative]::ProbeMisses())
    }
    else { $m.gui_start_ms = New-LabUnavailable 'window did not become ready'; [void](Stop-LabApp $g.Process $g.Window 2) }

    # 2) headless workload
    $json = Join-Path $Lab.Work 'blender-result.json'
    Remove-Item -LiteralPath $json -ErrorAction SilentlyContinue
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'      # see the note at Invoke-LabGodot
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $exe -b --factory-startup --python (Join-Path $Lab.Root 'tasks\blender\bench.py') -- $json $work 2>&1
    $wall = $sw.Elapsed.TotalSeconds
    $ErrorActionPreference = $eap
    if (Test-Path -LiteralPath $json) {
        $r = Get-Content -Raw -LiteralPath $json | ConvertFrom-Json
        $sum = 0.0
        foreach ($s in $r.stages.PSObject.Properties) {
            if ($s.Value.ok) { $m["$($s.Name)_s"] = $s.Value.sec; $sum += $s.Value.sec } else { $m["$($s.Name)_s"] = New-LabUnavailable $s.Value.error }
        }
        $m.bench_wall_s = [math]::Round($wall, 3)
        $m.headless_startup_s = [math]::Round($wall - $sum, 3)
    }
    else { $m.bench_wall_s = New-LabUnavailable ("no result file, exit ${LASTEXITCODE}: " +(($out | Select-Object -Last 2) -join ' / ')) }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    return $m
}

# ---- Godot -----------------------------------------------------------------------------------------------------------
function Initialize-LabGodotProject($Lab) {
    $proj = Join-Path $Lab.Work 'godot-proj'
    if (-not (Test-Path -LiteralPath (Join-Path $proj 'project.godot'))) { & (Join-Path $Lab.Root 'tasks\godot\make-project.ps1') -Path $proj | Out-Null }
    $marker = Join-Path (Split-Path -Parent $Lab.Tools.godot) '_sc_'
    if (-not (Test-Path -LiteralPath $marker)) { [IO.File]::WriteAllText($marker, '') }   # editor data stays next to the exe
    return $proj
}

function Invoke-LabGodot($Lab) {
    $m = [ordered]@{}
    $proj = Initialize-LabGodotProject $Lab
    $io = Join-Path $Lab.Work 'godot-io'
    if (Test-Path -LiteralPath "$proj\.godot") { Remove-Item -LiteralPath "$proj\.godot" -Recurse -Force }
    Get-ChildItem -LiteralPath $proj -Recurse -Filter *.import | Remove-Item -Force

    # A line on stderr must not lose the run. With $ErrorActionPreference = 'Stop', Windows PowerShell 5.1 turns the first
    # stderr line of "2>&1" into a terminating error; that happened once in 67 Godot runs (2026-11-11, s0-04 run 38:
    # "Unicode parsing error: Unpaired trail surrogate", cause unknown). The timed commands themselves are unchanged.
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'

    # 1) import the project from scratch (texture compression, script parsing, many small file writes)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $o1 = & $Lab.Tools.godot_console --headless --path $proj --import 2>&1
    $imp = $sw.Elapsed.TotalSeconds
    $importExit = $LASTEXITCODE
    $count = @(Get-ChildItem -LiteralPath "$proj\.godot\imported" -ErrorAction SilentlyContinue).Count
    if ($importExit -eq 0 -and $count -gt 0) { $m.import_s = [math]::Round($imp, 3); $m.imported_files = $count }
    else { $m.import_s = New-LabUnavailable ("exit $importExit, $count imported files") }

    # 2) script workload
    $sw.Restart()
    $o2 = & $Lab.Tools.godot_console --headless --path $proj --script res://bench.gd -- $io 2>&1
    $wall = $sw.Elapsed.TotalSeconds
    $ErrorActionPreference = $eap
    $said = @(@($o1) + @($o2) | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" } | Where-Object { $_.Trim() } | Select-Object -First 3)
    if ($said.Count) { $m.console_stderr = $said }
    $line = $o2 | ForEach-Object { "$_" } | Where-Object { $_ -like 'LABRESULT *' } | Select-Object -First 1
    if ($line) { $g = $line.Substring(10) | ConvertFrom-Json; $m.script_wall_s = [math]::Round($wall, 3); $m.compute_ms = [math]::Round($g.compute_ms, 1); $m.io_ms = [math]::Round($g.io_ms, 1) }
    else { $m.script_wall_s = New-LabUnavailable ('no LABRESULT: ' + (($o2 | Select-Object -Last 2) -join ' / ')) }

    # 3) editor GUI start on the imported project; the editor quits itself after a fixed number of frames
    $e = Start-LabGuiApp -Exe $Lab.Tools.godot -Arguments ('--path "{0}" --editor --quit-after 400' -f $proj) -TimeoutSec 90
    if ($e.Ready) {
        $m.gui_start_ms = $e.ReadyMs
        [LabNative]::StartProbe($e.Window, 100)
        if (-not $e.Process.WaitForExit(25000)) { $m.gui_close = Stop-LabApp $e.Process $e.Window 5 } else { $m.gui_close = 'self' }
        $m.gui_probe = Get-LabProbeSummary ([LabNative]::StopProbe()) ([LabNative]::ProbeMisses())
    }
    else { $m.gui_start_ms = New-LabUnavailable 'window did not become ready'; [void](Stop-LabApp $e.Process $e.Window 2) }
    Get-Process -Name 'Godot*' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Lab.Tools.godot -or $_.Path -eq $Lab.Tools.godot_console } | ForEach-Object { try { $_.Kill() } catch { } }
    Remove-Item -LiteralPath $io -Recurse -Force -ErrorAction SilentlyContinue
    return $m
}

# ---- Notepad++ -------------------------------------------------------------------------------------------------------
function Initialize-LabNotepadFile($Lab) {
    $master = Join-Path $Lab.Work 'npp-master.txt'
    $bytes = [int64]$Lab.Config.notepad.file_mb * 1MB
    if (-not (Test-Path -LiteralPath $master) -or (Get-Item -LiteralPath $master).Length -ne $bytes) {
        $line = [Text.Encoding]::ASCII.GetBytes(('The quick brown fox jumps over the lazy dog 0123456789 ' * 2).Substring(0, 99) + "`n")   # 100 bytes
        $chunk = New-Object byte[] (1MB)
        for ($i = 0; $i -lt 1MB; $i += 100) { $n = [math]::Min(100, 1MB - $i); [Array]::Copy($line, 0, $chunk, $i, $n) }
        $fs = [IO.File]::Create($master)
        try { for ($i = 0; $i -lt $Lab.Config.notepad.file_mb; $i++) { $fs.Write($chunk, 0, $chunk.Length) } } finally { $fs.Dispose() }
    }
    return $master
}

# One Notepad++ session: launch with $File, wait until the whole file is in the editor, type $Chars characters at the
# end (measuring each one when $MeasureTyping), save with Ctrl+S, close.
function Invoke-LabNotepadSession($Lab, [string]$File, [int]$Chars, [bool]$MeasureTyping) {
    $m = [ordered]@{}
    $expected = (Get-Item -LiteralPath $File).Length
    $g = Start-LabGuiApp -Exe $Lab.Tools.notepadpp -Arguments ('-multiInst -nosession "{0}"' -f $File) -TimeoutSec 60
    if (-not $g.Ready) { [void](Stop-LabApp $g.Process $g.Window 2); $m.open_ms = New-LabUnavailable 'window did not become ready'; return $m }
    $p = $g.Process
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew(); $sci = [IntPtr]::Zero; $loaded = $false
        while ($sw.Elapsed.TotalSeconds -lt 60 -and -not $p.HasExited) {
            if ($sci -eq [IntPtr]::Zero) { $sci = [LabNative]::ChildByClass($g.Window, 'Scintilla') }
            if ($sci -ne [IntPtr]::Zero) { $ok = $false; $len = [LabNative]::SciLength($sci, [ref]$ok); if ($ok -and $len -ge $expected) { $loaded = $true; break } }
            Start-Sleep -Milliseconds 10
        }
        if (-not $loaded) { $m.open_ms = New-LabUnavailable 'file did not finish loading'; return $m }
        $m.window_ms = $g.ReadyMs
        $m.open_ms = [math]::Round($g.ReadyMs + $sw.Elapsed.TotalMilliseconds, 1)
        [LabNative]::StartProbe($g.Window, 100)

        # keyboard input goes to whatever is in front, so type only when that is our own window
        if ([LabNative]::BringToFront($g.Window) -and [LabNative]::ForegroundPid() -eq $p.Id) {
            [LabNative]::SendCtrlEnd(); Start-Sleep -Milliseconds 400
            $lat = [LabNative]::TypeAndMeasure($sci, $p.Id, $Chars, 2000, [int]$Lab.Config.notepad.type_gap_ms)
            if ($MeasureTyping) {
                if ($lat.Count -ge [math]::Min(10, $Chars)) {
                    $s = @($lat | Sort-Object)
                    $m.type = [ordered]@{ n = $s.Count; requested = $Chars; p50_ms = [math]::Round($s[[int][math]::Floor(($s.Count - 1) * 0.5)], 3); p95_ms = [math]::Round($s[[int][math]::Floor(($s.Count - 1) * 0.95)], 3); max_ms = [math]::Round($s[-1], 2); over50ms = @($s | Where-Object { $_ -ge 50 }).Count }
                }
                else { $m.type = New-LabUnavailable "only $($lat.Count) characters registered (lost focus or timed out)" }
            }
            # save through the file system: done when the editor reports "not modified" and the file on disk changed
            $before = (Get-Item -LiteralPath $File).LastWriteTimeUtc
            $sw.Restart(); [LabNative]::SendCtrlS(); $saved = $false
            while ($sw.Elapsed.TotalSeconds -lt 60) {
                $ok = $false; $mod = [LabNative]::SciModified($sci, [ref]$ok)
                if ($ok -and -not $mod -and (Get-Item -LiteralPath $File).LastWriteTimeUtc -ne $before) { $saved = $true; break }
                Start-Sleep -Milliseconds 5
            }
            if ($saved) { $m.save_ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) } else { $m.save_ms = New-LabUnavailable 'save not confirmed in 60 s' }
        }
        else { $m.type = New-LabUnavailable 'could not bring the window to the foreground'; $m.save_ms = New-LabUnavailable 'skipped: no foreground' }
    }
    finally {
        $Lab.LastScriptInputTick = [LabNative]::LastInputTick()     # the keys above were ours, not a person's
        $m.close = Stop-LabApp $p $g.Window 10
        $m.probe = Get-LabProbeSummary ([LabNative]::StopProbe()) ([LabNative]::ProbeMisses())
    }
    return $m
}

# Two sessions. Typing latency is measured in a small file: in a 50 MB file Notepad++ itself pauses about 0.6 s every
# few characters (11/10 test: 62 of 300), which would hide the input path we want to see. The large file is used for
# what it is good at: open time and a 50 MB save through the file system.
function Invoke-LabNotepad($Lab) {
    $m = [ordered]@{}
    $small = Join-Path $Lab.Work 'npp-small.txt'
    [IO.File]::WriteAllText($small, (("The quick brown fox jumps over the lazy dog 0123456789`n") * 200), (New-Object Text.ASCIIEncoding))
    $s = Invoke-LabNotepadSession $Lab $small ([int]$Lab.Config.notepad.type_chars) $true
    $m.start_ms = $s.window_ms; $m.type = $s.type; $m.small_save_ms = $s.save_ms; $m.small_close = $s.close; $m.probe_typing = $s.probe
    if ($s.open_ms -is [System.Collections.IDictionary]) { $m.start_ms = $s.open_ms }

    $big = Join-Path $Lab.Work 'npp-big.txt'
    Copy-Item -LiteralPath (Initialize-LabNotepadFile $Lab) -Destination $big -Force
    $b = Invoke-LabNotepadSession $Lab $big 3 $false
    $m.big_open_ms = $b.open_ms; $m.big_save_ms = $b.save_ms; $m.big_close = $b.close; $m.probe_big = $b.probe
    Remove-Item -LiteralPath $small, $big -Force -ErrorAction SilentlyContinue
    return $m
}
# ---- background browser ------------------------------------------------------------------------------------------------
function Get-LabChromeProcesses($Lab) {
    $needle = $Lab.ChromeProfile
    return @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($needle) })
}

function Start-LabBackground($Lab, [string]$Kind) {
    $bg = [ordered]@{ kind = $Kind; started = (Get-Date).ToString('o') }
    if ($Kind -eq 'B0') { return $bg }
    $cfg = $Lab.Config.backgrounds.$Kind
    # Flags apply to the lab's own profile only; the user's Chrome and system policy are untouched.
    #  - LocalNetworkAccessChecks off: since Chrome 142 a bank page needs a permission click to reach the security
    #    modules on localhost (seen 11/10: the tab title became a permission request from www.kebhana.com). Nobody is there to click, and the
    #    banks' own FAQ lists turning this check off as the fix.
    #  - Translate off: its prompt covers the page title we use to confirm the page loaded.
    #  - occlusion off: keeps a covered browser window working as if it were visible, so the load does not depend on window order.
    $common = '--user-data-dir="{0}" --no-first-run --no-default-browser-check --disable-search-engine-choice-screen --hide-crash-restore-bubble --autoplay-policy=no-user-gesture-required --mute-audio --disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion,Translate,TranslateUI,LocalNetworkAccessChecks --window-position=40,40 --window-size=1280,800' -f $Lab.ChromeProfile
    $urls = @($cfg.urls)
    if ($cfg.kind -eq 'browse') {
        Start-Process -FilePath $Lab.Config.chrome -ArgumentList ($common + ' --new-window "' + $urls[0] + '"')
        $stop = Join-Path $Lab.Work 'rotator.stop'
        Remove-Item -LiteralPath $stop -ErrorAction SilentlyContinue
        $rest = ($urls | Select-Object -Skip 1) -join '|'
        $rot = Start-Process -FilePath powershell -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f (Join-Path $Lab.Root 'lib\url-rotator.ps1')), '-Chrome', ('"{0}"' -f $Lab.Config.chrome), '-ProfileDir', ('"{0}"' -f $Lab.ChromeProfile), '-Urls', ('"{0}"' -f $rest), '-IntervalSec', $cfg.interval_sec, '-StopFile', ('"{0}"' -f $stop)
        $bg.rotator = $rot.Id; $bg.stopFile = $stop
    }
    else {
        Start-Process -FilePath $Lab.Config.chrome -ArgumentList ($common + ' --new-window ' + (($urls | ForEach-Object { '"' + $_ + '"' }) -join ' '))
        if ($cfg.kind -eq 'bank') { $Lab.BankLoads += $urls.Count }
    }
    $bg.io0 = (Get-LabChromeIo $Lab)
    return $bg
}

function Get-LabChromeIo($Lab) {
    $pids = @(Get-LabChromeProcesses $Lab | ForEach-Object { [int]$_.ProcessId })
    $sum = 0L
    foreach ($r in [LabNative]::Snapshot()) { if ($pids -contains $r.Pid) { $sum += $r.ReadBytes + $r.WriteBytes } }
    return $sum
}

# After the settle time: is the background really there? Titles show which pages loaded; I/O shows that a video is streaming.
function Test-LabBackground($Lab, $bg) {
    if ($bg.kind -eq 'B0') { return [ordered]@{ ok = $true } }
    $procs = Get-LabChromeProcesses $Lab
    $titles = @($procs | ForEach-Object { [LabNative]::WindowTitlesOf([int]$_.ProcessId) } | Where-Object { $_ } | Sort-Object -Unique)
    $io = Get-LabChromeIo $Lab
    $secs = ((Get-Date) - [datetime]$bg.started).TotalSeconds
    $check = [ordered]@{ ok = ($procs.Count -gt 0); chromeProcesses = $procs.Count; titles = $titles; ioMB = [math]::Round(($io - $bg.io0) / 1MB, 1); afterSec = [math]::Round($secs, 0) }
    if ($bg.kind -eq 'B2') {
        $rx = $Lab.Config.security_modules
        $mods = @([LabNative]::Snapshot() | Where-Object { $_.Name -match $rx } | ForEach-Object Name | Sort-Object -Unique)
        $check.securityModules = $mods          # empty before the install; must be non-empty for a post-install B2 run to count
    }
    return $check
}

function Stop-LabBackground($Lab, $bg) {
    if ($bg.kind -eq 'B0') { return }
    if ($bg.stopFile) { [IO.File]::WriteAllText($bg.stopFile, 'stop'); if ($bg.rotator) { try { (Get-Process -Id $bg.rotator -ErrorAction Stop).WaitForExit(3000) | Out-Null } catch { }; Stop-Process -Id $bg.rotator -Force -ErrorAction SilentlyContinue } }
    for ($i = 0; $i -lt 3; $i++) {
        $procs = Get-LabChromeProcesses $Lab
        if (-not $procs.Count) { break }
        foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }
}
