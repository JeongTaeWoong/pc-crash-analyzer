# Writes some tens of thousands of small text documents into the user's Documents folder - what happens when an old
# mail archive or years of notes are unpacked there - and then stays alive until the lab stops it.
# Used by the scenario sys-search-index (lab/vm/scenarios.json). ASCII only.
#
# The point is not this script: Documents is an indexed location, so Windows Search (SearchIndexer.exe and its helper
# processes) has to read every new file. Microsoft describes exactly this load:
#   https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/windows-search-performance-issues
# The script changes no setting and starts or stops no service. It only writes ordinary files, and it writes down
# whether the search service was running, because without it there is nothing to show.
#
# Exit codes: 0 = held until the end, 4 = the Windows Search service is not running (the run is not a case).
param(
    [string]$Folder = 'archive-2019',
    [int]$Count = 30000,
    [int]$HoldSec = 900,
    [string]$Root = ''          # default: the user's Documents folder (an indexed location). Another folder is for trying the script out.
)
$ErrorActionPreference = 'Stop'
function Say([string]$text) { Write-Output ("[driver {0}] {1}" -f (Get-Date).ToString('HH:mm:ss'), $text) }

$svc = Get-Service -Name WSearch -ErrorAction SilentlyContinue
$waited = 0
while ($svc -and $svc.Status -ne 'Running' -and $waited -lt 60) { Start-Sleep -Seconds 5; $waited += 5; $svc.Refresh() }
if (-not $svc -or $svc.Status -ne 'Running') {
    Say ("Windows Search service: " + $(if ($svc) { "$($svc.Status), start type $($svc.StartType)" } else { 'not installed' }) + ' - nothing to show')
    exit 4
}
Say "Windows Search service: Running (start type $($svc.StartType))"

$root = Join-Path $(if ($Root) { $Root } else { [Environment]::GetFolderPath('MyDocuments') }) $Folder
Say "writing $Count documents under $root"
$words = 'meeting report budget schedule customer delivery invoice contract review draft design sample order request ' +
         'estimate project plan weekly monthly summary note change approval payment shipping stock supplier quality ' +
         'inspection drawing revision factory office branch team manager deadline proposal result issue action check ' +
         'confirm update version release install setting printer network backup account training visit agenda minutes'
$vocab = $words.Split(' ')
$rng = New-Object Random 20261115
$enc = New-Object Text.UTF8Encoding($false)
$sb = New-Object Text.StringBuilder
$started = Get-Date
for ($i = 1; $i -le $Count; $i++) {
    if (($i - 1) % 500 -eq 0) {
        $dir = Join-Path $root ('box-{0:D3}' -f [int](($i - 1) / 500))
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    [void]$sb.Clear()
    $n = $rng.Next(150, 400)
    for ($w = 0; $w -lt $n; $w++) { [void]$sb.Append($vocab[$rng.Next($vocab.Length)]).Append($(if ($w % 12 -eq 11) { "`r`n" } else { ' ' })) }
    [IO.File]::WriteAllText((Join-Path $dir ('note-{0:D5}.txt' -f $i)), $sb.ToString(), $enc)
    if ($i % 5000 -eq 0) { Say "$i written" }
}
Say ("all $Count written in {0:N0} s" -f ((Get-Date) - $started).TotalSeconds)

# Stay alive: the lab sends the user's mark while the search indexer works through the new files, then stops this.
$until = (Get-Date).AddSeconds($HoldSec)
while ((Get-Date) -lt $until) { Start-Sleep -Seconds 5 }
exit 0
