# notepad-plus-plus#15144 on v8.6.7: Column Editor, Number to Insert, increase 9999999, Hex, 28+ lines of column selection.
# Drives only the Notepad++ window it started. Exits with Notepad++'s exit code so the runner can tell a crash from a clean exit.
param([Parameter(Mandatory = $true)][string]$Npp, [string]$Work = "$env:TEMP\repro-npp")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, Microsoft.VisualBasic
Add-Type -Name F -Namespace W -MemberDefinition @'
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint p);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern System.IntPtr SendMessage(System.IntPtr h, uint m, System.IntPtr w, string l);
[DllImport("user32.dll")] public static extern System.IntPtr PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
'@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$WM_SETTEXT = 0x000C; $BM_CLICK = 0x00F5
New-Item -ItemType Directory -Force $Work | Out-Null
$file = Join-Path $Work 'lines.txt'
[IO.File]::WriteAllLines($file, [string[]](1..40 | ForEach-Object { 'abcdefghij' }))
$p = Start-Process $Npp -ArgumentList '-multiInst', '-nosession', "`"$file`"" -PassThru
[void]$p.Handle
try {
    [void]$p.WaitForInputIdle(30000)
    Start-Sleep -Seconds 15
    [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id)
    Start-Sleep -Milliseconds 800
    $fgPid = [uint32]0; [void][W.F]::GetWindowThreadProcessId([W.F]::GetForegroundWindow(), [ref]$fgPid)
    if ($fgPid -ne $p.Id) { Write-Output "foreground is not notepad++ ($fgPid vs $($p.Id)); aborting"; $p.Kill(); exit 3 }
    [System.Windows.Forms.SendKeys]::SendWait('^{HOME}')
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('%+{DOWN 30}')
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('%c')
    Start-Sleep -Seconds 2
    $p.Refresh()
    $root = $AE::FromHandle($p.MainWindowHandle)
    $dlg = $null
    foreach ($e in $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($e.Current.ClassName -eq '#32770' -and $e.Current.Name -match 'Column') { $dlg = $e; break } }
    if (-not $dlg) { Write-Output 'column editor dialog not found'; $p.Kill(); exit 4 }
    function Get-Ctl([string]$id) {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
        $el = $dlg.FindFirst($TS::Children, $c)
        if (-not $el) { throw "control $id not found" }
        return [IntPtr]$el.Current.NativeWindowHandle
    }
    [void][W.F]::SendMessage((Get-Ctl '2033'), $BM_CLICK, [IntPtr]::Zero, $null)   # Number to Insert
    Start-Sleep -Milliseconds 300
    [void][W.F]::SendMessage((Get-Ctl '2026'), $BM_CLICK, [IntPtr]::Zero, $null)   # Hex
    [void][W.F]::SendMessage((Get-Ctl '2021'), $WM_SETTEXT, [IntPtr]::Zero, '0')       # Initial number
    [void][W.F]::SendMessage((Get-Ctl '2022'), $WM_SETTEXT, [IntPtr]::Zero, '9999999') # Increase by
    [void][W.F]::SendMessage((Get-Ctl '2037'), $WM_SETTEXT, [IntPtr]::Zero, '1')       # Repeat
    Start-Sleep -Milliseconds 400
    Write-Output 'controls set, pressing OK'
    # Posted, not sent: if the handler crashes the app, this script must not be blocked inside the call
    [void][W.F]::PostMessage((Get-Ctl '1'), $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $p.WaitForExit(25000)) { Write-Output 'notepad++ still running 25s after OK'; $p.Kill(); [void]$p.WaitForExit(5000); exit 0 }
    Write-Output ('notepad++ exit 0x{0:X8}' -f $p.ExitCode)
    exit $p.ExitCode
}
catch { Write-Output ("driver error: " + $_.Exception.Message); try { if (-not $p.HasExited) { $p.Kill() } } catch { }; exit 5 }
