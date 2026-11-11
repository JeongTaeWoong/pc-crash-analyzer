# Checks auto-click.ps1 against a harmless three-page wizard made for the test (labels in test-autoclick.labels.json):
#   page 1: licence with "accept" / "do not accept" options; Next is disabled until "accept" is chosen
#   page 2: Install starts a 2 s fake progress;  page 3: Finish closes.  Cancel is on every page and must never be pressed.
# The wizard writes what was pressed to a file; the test passes if it ended through Finish and Cancel was never pressed.
# This file is ASCII-only on purpose: Windows PowerShell 5.1 would garble Korean literals in a script without a byte-order mark.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lab = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $env:LOCALAPPDATA 'Temp\pca-lab-autoclick'
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $dir | Out-Null
$result = "$dir\wizard-result.txt"
$labels = "$dir\labels.json"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-autoclick.labels.json') -Destination $labels
$wizard = @'
Add-Type -AssemblyName System.Windows.Forms
$L = Get-Content -Raw -Encoding UTF8 -LiteralPath '__LABELS__' | ConvertFrom-Json
$log = '__RESULT__'
$f = New-Object Windows.Forms.Form; $f.Text = $L.title; $f.Width = 420; $f.Height = 220; $f.StartPosition = 'CenterScreen'
$lbl = New-Object Windows.Forms.Label; $lbl.Left = 15; $lbl.Top = 15; $lbl.Width = 380; $lbl.Text = $L.question; $f.Controls.Add($lbl)
$yes = New-Object Windows.Forms.RadioButton; $yes.Text = $L.accept; $yes.Left = 20; $yes.Top = 45; $yes.Width = 220; $f.Controls.Add($yes)
$no = New-Object Windows.Forms.RadioButton; $no.Text = $L.refuse; $no.Left = 20; $no.Top = 70; $no.Width = 220; $no.Checked = $true; $f.Controls.Add($no)
$next = New-Object Windows.Forms.Button; $next.Text = $L.next; $next.Left = 200; $next.Top = 130; $next.Enabled = $false; $f.Controls.Add($next)
$cancel = New-Object Windows.Forms.Button; $cancel.Text = $L.cancel; $cancel.Left = 300; $cancel.Top = 130; $f.Controls.Add($cancel)
$script:page = 1
$yes.add_CheckedChanged({ $next.Enabled = $yes.Checked })
$cancel.add_Click({ Add-Content $log 'CANCEL'; $f.Close() })
$next.add_Click({
  Add-Content $log ('page' + $script:page)
  if ($script:page -eq 1) { $script:page = 2; $lbl.Text = $L.ready; $yes.Visible = $false; $no.Visible = $false; $next.Text = $L.install }
  elseif ($script:page -eq 2) { $script:page = 3; $next.Enabled = $false; $lbl.Text = $L.working; $script:t = New-Object Windows.Forms.Timer; $script:t.Interval = 2000; $script:t.add_Tick({ $script:t.Stop(); $lbl.Text = $L.complete; $next.Text = $L.finish; $next.Enabled = $true }); $script:t.Start() }
  else { Add-Content $log 'FINISHED'; $f.Close() }
})
[Windows.Forms.Application]::Run($f)
'@
$wizard = $wizard.Replace('__LABELS__', $labels).Replace('__RESULT__', $result)
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wizard))
$w = Start-Process powershell -ArgumentList '-NoProfile', '-EncodedCommand', $enc -WindowStyle Minimized -PassThru
$sw = [Diagnostics.Stopwatch]::StartNew()
$out = & powershell -NoProfile -ExecutionPolicy Bypass -File "$lab\lib\auto-click.ps1" -ProcessId $w.Id -TimeoutSec 60
$code = $LASTEXITCODE
$out | ForEach-Object { "  $_" }
$lines = if (Test-Path $result) { @(Get-Content $result) } else { @() }
"wizard log: " + ($lines -join ' | ')
"clicker exit $code in {0:N1} s; wizard exited: {1}" -f $sw.Elapsed.TotalSeconds, $w.HasExited
if ($code -eq 0 -and ($lines -join ',') -eq 'page1,page2,page3,FINISHED') { 'PASS  wizard completed: accept -> Next -> Install -> Finish, Cancel never pressed' } else { 'FAIL' }
if (-not $w.HasExited) { $w.Kill() }
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
