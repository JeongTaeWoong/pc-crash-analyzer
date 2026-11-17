# Runs SteamCMD the way an owner of a game server does: let the client update itself, then download a server package
# with Steam's anonymous login. Used by the scenario bg-steam-update (lab/vm/scenarios.json). ASCII only.
# No account, no password, no key: 'login anonymous' is Steam's own account-less login for server tools.
#
# Why a script and not one command line (2026-11-15, plan real-2, both runs):
#   the first 'app_update' right after SteamCMD has updated itself ended with
#       ERROR! Failed to install app '232250' (Missing configuration)      exit code 8
#   and nothing was downloaded, so the scenario had no busy program to show. The cause is NOT confirmed. Reports of the
#   same message suggest refreshing the app information (app_info_update 1) and simply asking again, so this script does
#   both and writes down what each try returned. If every try fails the script ends with the last exit code and the run
#   is not a case.
#
# Exit codes: 0 = the download finished (it is normally stopped by the lab before that), 3 = steamcmd.exe not found,
#             anything else = SteamCMD's exit code of the last try.
param(
    [string]$SteamCmd = 'C:\Tools\SteamCMD\steamcmd.exe',
    [string]$InstallDir = 'C:\Games\server',
    [int]$AppId = 232250,
    [int]$Tries = 3
)
$ErrorActionPreference = 'Continue'
function Say([string]$text) { Write-Output ("[driver {0}] {1}" -f (Get-Date).ToString('HH:mm:ss'), $text) }

if (-not (Test-Path $SteamCmd)) { Say "steamcmd not found: $SteamCmd"; exit 3 }

Say 'step 1: steamcmd +quit (the first run updates SteamCMD itself)'
& $SteamCmd +quit
Say "step 1 ended with exit code $LASTEXITCODE"

$code = -1
for ($i = 1; $i -le $Tries; $i++) {
    Say "step 2, try $i of ${Tries}: app_update $AppId into $InstallDir"
    & $SteamCmd +force_install_dir $InstallDir +login anonymous +app_info_update 1 +app_update $AppId validate +quit
    $code = $LASTEXITCODE
    Say "try $i ended with exit code $code"
    if ($code -eq 0) { break }
    Start-Sleep -Seconds 5
}
exit $code
