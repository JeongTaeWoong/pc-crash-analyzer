# Release helper for the participant app (0.9~). ASCII only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# Procedure and reasons: docs\study-operations.md section 2-5.
#
#   powershell -ExecutionPolicy Bypass -File tools\release-participant.ps1 -Notes "what changed"      # check + build + show what to upload
#   powershell -ExecutionPolicy Bypass -File tools\release-participant.ps1 -Verify                     # after uploading: compare GitHub with the local files
#
# It does NOT commit, push or upload by itself. It refuses to build unless the sources that go into the build are committed,
# because the file version information records the commit hash (a build from uncommitted sources would point at different source).
#   -Tag      also create the local tag v<Version> on HEAD (not pushed)
#   -Publish  only if the GitHub CLI (gh) is installed and signed in: create the release and upload the three files
# What goes to GitHub Releases: exactly three files (update zip, update.json, update.json.sig). They hold no study settings.
# What must NEVER go there: dist\PcBlackBox-<Version>-win-x64.zip (it holds the receiver address and token) and any build with an API key.
param(
    [string]$Version = '',
    [string]$Notes = '',
    [switch]$Tag,
    [switch]$Publish,
    [switch]$Verify,
    [switch]$NoOpen      # do not open the folder and the GitHub page at the end
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $env:DOTNET_ROLL_FORWARD) { $env:DOTNET_ROLL_FORWARD = 'Major' }
$safe = $repo -replace '\\', '/'
function G { & git -C $repo -c "safe.directory=$safe" @args }
function Ok($m) { Write-Host "  [ok] $m" }
function Stop-With($m) { Write-Host "  [stop] $m" -ForegroundColor Yellow; exit 1 }

$csproj = Join-Path $repo 'participant\BlackBox.Participant\BlackBox.Participant.csproj'
$csVersion = ([regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $Version) { $Version = $csVersion }
$slug = 'JeongTaeWoong/pc-crash-analyzer'
$name = "PcBlackBox-$Version-win-x64"
$updDir = Join-Path $repo "dist\update\$name"
$assets = @("$name-update.zip", 'update.json', 'update.json.sig')
$tools = Join-Path $repo 'participant\StudyTools\bin\Release\net8.0\StudyTools.exe'

if ($Verify) {
    Write-Host "== verify: what GitHub serves as the latest release vs. $updDir"
    foreach ($f in $assets) { if (-not (Test-Path (Join-Path $updDir $f))) { Stop-With "local file missing: $f (run without -Verify first)" } }
    $tmp = Join-Path $env:TEMP ("pcbb-release-verify-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $bad = 0
    try {
        foreach ($f in $assets) {
            # the app uses exactly this address for update.json and update.json.sig; the zip address is the one written in update.json
            $url = if ($f -like '*.zip') { "https://github.com/$slug/releases/download/v$Version/$f" } else { "https://github.com/$slug/releases/latest/download/$f" }
            try { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile (Join-Path $tmp $f) -TimeoutSec 600 }
            catch { Write-Host "  [FAIL] $f : $($_.Exception.Message)  ($url)"; $bad++; continue }
            $same = (Get-FileHash (Join-Path $tmp $f) -Algorithm SHA256).Hash -eq (Get-FileHash (Join-Path $updDir $f) -Algorithm SHA256).Hash
            if (-not $same) { $bad++ }
            Write-Host ("  [{0}] {1} is byte-identical to the local file" -f $(if ($same) { 'PASS' } else { 'FAIL' }), $f)
        }
        if ($bad -eq 0) {
            & $tools update-verify --dir $tmp | Out-Null   # same code as the app: signature, release-asset address rule, size, hash, no study settings
            Write-Host ("  [{0}] downloaded set passes the app's own checks (update-verify exit {1})" -f $(if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }), $LASTEXITCODE)
            if ($LASTEXITCODE -ne 0) { $bad++ }
        }
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host $(if ($bad -eq 0) { "RESULT: release v$Version is what installed apps will accept." } else { "RESULT: $bad problem(s). If 'latest' shows an older version, mark v$Version as the latest release (not pre-release)." })
    exit $(if ($bad -eq 0) { 0 } else { 1 })
}

Write-Host "== release check for $Version"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Stop-With "version must be three numbers, got '$Version'" }
if ($csVersion -ne $Version) { Stop-With "csproj says $csVersion, asked for $Version. Change <Version> in BlackBox.Participant.csproj and commit." }
Ok "csproj version $csVersion"

$key = Join-Path $env:USERPROFILE '.pca-study\update-signing.private.pem'
if (-not (Test-Path $key)) { Stop-With "update signing key not found: $key" }
Ok 'update signing key present'

# everything that is compiled into or copied next to the app, plus the scripts that build it
$inputs = @('participant', 'collector/BlackBox', 'tools/publish-participant.ps1', 'docs/participant-guide.md')
$dirty = @(G status --porcelain -- @inputs)
if ($dirty.Count -gt 0) {
    Write-Host '  uncommitted changes in the build inputs:'
    $dirty | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" }
    Stop-With 'commit these first (the build records the commit hash).'
}
$head = (G rev-parse HEAD).Trim()
Ok "build inputs are committed (HEAD $($head.Substring(0, 7)))"

$onRemote = @(G branch -r --contains $head)
if ($onRemote.Count -eq 0) { Write-Host "  [note] HEAD is not on the remote yet. Push before creating the release, or the tag will point at nothing public." -ForegroundColor Yellow }
else { Ok "HEAD is on the remote ($($onRemote[0].Trim()))" }

$existing = "$(G tag -l "v$Version")".Trim()
if ($existing) {
    $tagAt = (G rev-list -n 1 "v$Version").Trim()
    if ($tagAt -ne $head) { Stop-With "tag v$Version already exists on $($tagAt.Substring(0, 7)), not on HEAD. Use a higher version; never move a released tag." }
    Ok "tag v$Version is on HEAD"
}

Write-Host '== build'
$pubArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'tools\publish-participant.ps1'), '-Version', $Version)
if ($Notes) { $pubArgs += @('-Notes', $Notes) }   # an empty argument would be dropped by PowerShell 5.1 and break the parameter
& powershell @pubArgs
if ($LASTEXITCODE -ne 0) { Stop-With 'build failed' }
foreach ($f in $assets) { if (-not (Test-Path (Join-Path $updDir $f))) { Stop-With "expected output missing: $f" } }

$exe = (Get-ChildItem (Join-Path $repo "dist\$name") -Recurse -Filter 'PcBlackBox.exe' | Select-Object -First 1).FullName
$pv = (Get-Item $exe).VersionInfo.ProductVersion
if ($pv -notlike "$Version+$head*") { Stop-With "built file says '$pv', expected $Version+$($head.Substring(0, 7))..." }
Ok "built file records this commit ($pv)"

if ($Tag -and -not $existing) { G tag "v$Version" $head | Out-Null; Ok "created local tag v$Version (not pushed: git push origin v$Version)" }

Write-Host ''
Write-Host "== upload exactly these three files to the release v$Version (and nothing else from dist):"
foreach ($f in $assets) { Write-Host ("   {0}  ({1:N1} MB)" -f (Join-Path $updDir $f), ((Get-Item (Join-Path $updDir $f)).Length / 1MB)) }
Write-Host "   hand to NEW participants only, never upload: $(Join-Path $repo "dist\$name.zip")"

$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($Publish) {
    if (-not $gh) { Stop-With 'GitHub CLI (gh) is not installed: create the release in the browser (page opens below without -Publish).' }
    if ($onRemote.Count -eq 0) { Stop-With 'push the commit first.' }
    $body = if ($Notes) { $Notes } else { "PC BlackBox $Version" }
    & gh release create "v$Version" @($assets | ForEach-Object { Join-Path $updDir $_ }) --repo $slug --target $head --title "PC BlackBox $Version" --notes $body --latest
    if ($LASTEXITCODE -ne 0) { Stop-With 'gh release create failed' }
    Ok 'release created. Now run this script with -Verify.'
    exit 0
}
Write-Host ''
Write-Host 'Next (in this order):'
Write-Host "  1. push the commit           git push origin HEAD"
Write-Host "  2. create the release        the page and the folder open now: tag v$Version, attach the three files, keep 'Set as the latest release' checked"
Write-Host "  3. check what GitHub serves  tools\release-participant.ps1 -Verify"
if ($NoOpen) { exit 0 }
try {
    Start-Process explorer.exe $updDir
    $q = "tag=v$Version&title=" + [uri]::EscapeDataString("PC BlackBox $Version") + '&body=' + [uri]::EscapeDataString($Notes)
    Start-Process "https://github.com/$slug/releases/new?$q"
} catch { Write-Host "  (could not open the page: https://github.com/$slug/releases/new )" }
