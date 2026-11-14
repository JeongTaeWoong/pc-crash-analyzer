# Installs programs from lab\vm\apps.json into a running case VM, without a person. ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -List                          # programs and bundles
#   powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -Name try1 -Check              # are the winget ids and local files there?
#   powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -Name office1 -Bundle office
#   powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -Name office1 -App krita,vlc
#
# Each program is installed in the guest's desktop session (elevated), then looked up in the guest's list of installed
# programs. What was installed, which version, exit codes and the end of each log go to <vm root>\clones\<name>\installed.json.
# Exit code: 0 = everything asked for is installed, 1 = at least one failed (the others are still installed).
param(
    [switch]$List,
    [string]$Name,
    [switch]$Check,
    [string]$Bundle,
    [string[]]$App,
    [switch]$Force,                        # install even when the program is already there
    [string]$Root
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
$catalog = Get-Content (Join-Path $PSScriptRoot 'apps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$labDir = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $labDir 'tools\vm-apps'                 # lab\tools is not committed

if ($List -or (-not $Name)) {
    Write-Host 'Programs:'
    foreach ($p in $catalog.apps.PSObject.Properties) { Write-Host ("  {0,-18} {1,-9} {2}{3}" -f $p.Name, $p.Value.kind, $p.Value.name, $(if ($p.Value.gpu) { "   [GPU: $($p.Value.gpu)]" } else { '' })) }
    Write-Host 'Bundles:'
    foreach ($b in $catalog.bundles.PSObject.Properties) { Write-Host ("  {0,-12} {1}" -f $b.Name, ($b.Value -join ', ')) }
    exit 0
}

Assert-HyperV
$vmName = Get-CloneVmName $cfg $Name

# Looks a program up in the guest: list of installed programs (name contains $match) or a file.
$lookup = {
    param($match, $file)
    if ($file) { if (Test-Path $file) { return "file:" + (Get-Item $file).VersionInfo.ProductVersion } else { return $null } }
    if (-not $match) { return $null }
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    # an installer run for the lab user writes HKCU of that user; this background session runs as the same user
    $all = @(Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName })
    $hit = $all | Where-Object { $_.DisplayName -like "*$match*" } | Select-Object -First 1
    # a localized display name (KakaoTalk shows a Korean name on Korean Windows) does not contain the
    # English text: fall back to the name of the registry key, which installers keep in English
    if (-not $hit) { $hit = $all | Where-Object { $_.PSChildName -like "*$match*" } | Select-Object -First 1 }
    if ($hit) { return "$($hit.DisplayName) | $($hit.DisplayVersion)" }
    return $null
}

# Host folder of a portable program: "{artcraft}/X" = under vm.json artcraft_dir, an absolute path, or a path under lab\.
function Resolve-PortableDir($a) {
    $d = "$($a.dir)"
    if ($d -like '{artcraft}*') { return (Join-Path $cfg.artcraft_dir $d.Substring('{artcraft}'.Length).TrimStart('/', [char]92)) }
    if ([IO.Path]::IsPathRooted($d)) { return $d }
    return (Join-Path $labDir $d)
}
# Path in the guest that proves the program is there (portable programs: their executable under C:\lab\apps\<id>).
function Get-CheckFile([string]$id, $a) {
    if ($a.kind -eq 'portable' -and $a.exe) { return ('C:\lab\apps\{0}\{1}' -f $id, $a.exe) }
    return $a.file
}

function Find-Download([string]$fileName) {
    foreach ($d in @($cfg.download_dirs) + @($cache)) {
        if (-not $d) { continue }
        $p = Join-Path $d $fileName
        if (Test-Path $p) { return $p }
    }
    return $null
}

$session = New-GuestSession $cfg $Name
try {
    if ($Check) {
        $bad = 0
        foreach ($p in $catalog.apps.PSObject.Properties) {
            $a = $p.Value
            switch ($a.kind) {
                'winget' {
                    $r = Invoke-Command -Session $session -ArgumentList $a.id -ScriptBlock {
                        param($id)
                        $w = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
                        $o = (& $w show --id $id -e --source winget --accept-source-agreements --disable-interactivity 2>&1 | Out-String)
                        $v = if ($o -match '(?m)^\s*(Version|\uBC84\uC804)\s*:\s*(\S+)') { $Matches[2] } else { '' }
                        [pscustomobject]@{ Code = $LASTEXITCODE; Version = $v }
                    }
                    if ($r.Code -eq 0) { Write-Host ("  [ok]      {0,-18} {1,-36} latest {2}" -f $p.Name, $a.id, $r.Version) } else { $bad++; Write-Host ("  [MISSING] {0,-18} {1,-36} winget exit {2}" -f $p.Name, $a.id, $r.Code) }
                }
                'msi-url' { Write-Host ("  [url]     {0,-18} {1}" -f $p.Name, $a.url) }
                'zip-exe' { $f = Find-Download $a.zip; if ($f) { Write-Host ("  [ok]      {0,-18} {1}" -f $p.Name, $f) } else { $bad++; Write-Host ("  [MISSING] {0,-18} {1} not found in download_dirs (vm.json)" -f $p.Name, $a.zip) } }
                'portable' { $d = Resolve-PortableDir $a; if (Test-Path $d) { Write-Host ("  [ok]      {0,-18} {1}" -f $p.Name, $d) } else { $bad++; Write-Host ("  [MISSING] {0,-18} {1}" -f $p.Name, $d) } }
            }
        }
        exit $(if ($bad) { 1 } else { 0 })
    }

    $ids = @()
    if ($Bundle) {
        $b = $catalog.bundles.$Bundle
        if (-not $b) { throw "Unknown bundle '$Bundle'. See -List." }
        $ids += $b
    }
    if ($App) { $ids += ($App | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
    $ids = @($ids | Select-Object -Unique)
    if (-not $ids.Count) { throw 'Give -Bundle <name> and/or -App <id,id>.' }
    foreach ($id in $ids) { if (-not $catalog.apps.$id) { throw "Unknown program '$id'. See -List." } }

    $recordPath = Join-Path (Get-CloneDir $cfg $Name) 'installed.json'
    $record = @()
    if (Test-Path $recordPath) {
        # Windows PowerShell 5.1 hands a JSON array over as ONE object: unroll it, or every entry test below
        # would look at the whole list at once
        $record = @((Get-Content $recordPath -Raw -Encoding UTF8 | ConvertFrom-Json) | ForEach-Object { $_ } | Where-Object { $_.id })
        # records written before 2026-11-12 noon stored 'found' as an object with remoting properties: keep the text
        foreach ($r in $record) { if ($r.found -and $r.found -isnot [string] -and $r.found.value) { $r.found = [string]$r.found.value } }
    }
    Invoke-Command -Session $session -ScriptBlock { New-Item -ItemType Directory -Force -Path 'C:\lab\installers', 'C:\lab\apps' | Out-Null }
    $failed = 0
    # a clone that is slowed down on purpose gets longer time limits
    $slow = (Get-LabVmSlowdown $vmName).factor
    if ($slow -gt 1) { Write-Host "  $vmName is a slowed-down profile: time limits x$slow" }

    foreach ($id in $ids) {
        $a = $catalog.apps.$id
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $timeout = [int]($(if ($a.timeout_sec) { [int]$a.timeout_sec } else { 1200 }) * $slow)
        $checkFile = Get-CheckFile $id $a
        $before = Invoke-Command -Session $session -ScriptBlock $lookup -ArgumentList $a.match, $checkFile
        if ($before -and -not $Force) {
            Write-Host ("  [have]  {0,-18} {1}" -f $id, $before)
            # the record must agree with what is in the guest: an entry that is missing, or one that an earlier
            # run marked failed although the program is there, is written now (the install log of that run is kept)
            $old = @($record | Where-Object { $_.id -eq $id }) | Select-Object -First 1
            if (-not $old -or -not $old.ok) {
                $entry = [pscustomobject]@{
                    id = $id; name = $a.name; kind = $a.kind; ok = $true; found = "$before"
                    exit_code = $(if ($old) { $old.exit_code } else { $null }); timed_out = $(if ($old) { $old.timed_out } else { $false })
                    error = $null; seconds = $(if ($old) { $old.seconds } else { 0 }); at = $(if ($old) { $old.at } else { $null })
                    log_tail = $(if ($old) { $old.log_tail } else { '' }); screenshot = $(if ($old) { $old.screenshot } else { $null })
                    rechecked_at = (Get-Date).ToString('o')
                    recheck_note = $(if ($old) { 'marked failed by the run that installed it; found in the guest at the recheck' } else { 'already in the guest; not installed by install.ps1' })
                }
                $record = @($record | Where-Object { $_.id -ne $id }) + $entry
                ConvertTo-Json -InputObject @($record) -Depth 4 | Set-Content -Path $recordPath -Encoding UTF8
            }
            continue
        }
        Write-Host ("  [..]    {0,-18} {1} ({2})" -f $id, $a.name, $a.kind)
        $job = $null; $err = $null
        try {
            switch ($a.kind) {
                'winget' {
                    $ver = if ($a.version) { "--version $($a.version)" } else { '' }
                    $script = @"
`$w = Join-Path `$env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
& `$w install --id $($a.id) -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity $ver 2>&1 | ForEach-Object { "`$_" } | Where-Object { `$_ -match '\S' -and `$_ -notmatch '^\s*[-\\|/]\s*$' -and `$_ -notmatch '\u2588|\u2592' }
exit `$LASTEXITCODE
"@
                    $job = Invoke-GuestDesktop $session $script $timeout
                }
                'msi-url' {
                    New-Item -ItemType Directory -Force -Path $cache | Out-Null
                    $local = Join-Path $cache ([IO.Path]::GetFileName(([Uri]$a.url).AbsolutePath))
                    if (-not (Test-Path $local)) {
                        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                        $ProgressPreference = 'SilentlyContinue'
                        Invoke-WebRequest -Uri $a.url -OutFile $local -UseBasicParsing
                    }
                    $hash = (Get-FileHash $local -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($hash -ne "$($a.sha256)".ToLowerInvariant()) { Remove-Item $local -Force; throw "sha256 of the download is $hash, apps.json says $($a.sha256): not installed" }
                    $guestPath = "C:\lab\installers\" + [IO.Path]::GetFileName($local)
                    Copy-Item -Path $local -Destination $guestPath -ToSession $session -Force
                    $script = "`$p = Start-Process msiexec.exe -ArgumentList '/i', '`"$guestPath`"', '/qn', '/norestart' -Wait -PassThru; `"msiexec exit `$(`$p.ExitCode)`"; exit `$p.ExitCode"
                    $job = Invoke-GuestDesktop $session $script $timeout
                }
                'zip-exe' {
                    $zip = Find-Download $a.zip
                    if (-not $zip) { throw "$($a.zip) not found in the download folders (vm.json download_dirs)" }
                    New-Item -ItemType Directory -Force -Path $cache | Out-Null
                    $exe = Join-Path $cache $a.inner
                    if (-not (Test-Path $exe)) {
                        Add-Type -AssemblyName System.IO.Compression.FileSystem
                        $z = [IO.Compression.ZipFile]::OpenRead($zip)
                        try { $e = $z.Entries | Where-Object { $_.FullName -eq $a.inner } | Select-Object -First 1; if (-not $e) { throw "$($a.inner) is not inside $zip" }; [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $exe, $true) } finally { $z.Dispose() }
                    }
                    $sig = Get-AuthenticodeSignature $exe
                    if ($sig.Status -ne 'Valid') { throw "The installer's signature is $($sig.Status): not installed" }
                    Write-Host ("          signed by {0}; copying {1:N1} GB into the guest..." -f ($sig.SignerCertificate.Subject -replace '^.*?CN=([^,]+).*$', '$1'), ((Get-Item $exe).Length / 1GB))
                    $guestPath = "C:\lab\installers\" + $a.inner
                    Copy-Item -Path $exe -Destination $guestPath -ToSession $session -Force
                    $script = "`$p = Start-Process '$guestPath' -ArgumentList '$($a.args)' -Wait -PassThru; `"installer exit `$(`$p.ExitCode)`"; exit `$p.ExitCode"
                    $job = Invoke-GuestDesktop $session $script $timeout
                }
                'portable' {
                    $src = Resolve-PortableDir $a
                    if (-not (Test-Path $src)) { throw "Not found on the host: $src" }
                    # a clean copy: the folder itself becomes C:\lab\apps\<id>
                    Invoke-Command -Session $session -ArgumentList $id -ScriptBlock { param($id) $d = "C:\lab\apps\$id"; if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
                    Copy-Item -Path $src -Destination "C:\lab\apps\$id" -ToSession $session -Recurse -Force
                    $job = [pscustomobject]@{ ExitCode = 0; Log = "copied to C:\lab\apps\$id"; TimedOut = $false }
                }
                default { throw "Unknown kind '$($a.kind)'" }
            }
        } catch { $err = $_.Exception.Message }

        $after = Invoke-Command -Session $session -ScriptBlock $lookup -ArgumentList $a.match, $checkFile
        if (-not $after -and $job -and $job.TimedOut) {
            # The time limit ends our wait, not the installer: it may still be working (2026-11-12, slowed-down clone:
            # 7-Zip, LibreOffice and KakaoTalk were all installed although each was recorded as timed out).
            # Look again for a few minutes before the next installer is started on top of it.
            $until = (Get-Date).AddSeconds([int](240 * $slow))
            while (-not $after -and (Get-Date) -lt $until) {
                Start-Sleep -Seconds 20
                $after = Invoke-Command -Session $session -ScriptBlock $lookup -ArgumentList $a.match, $checkFile
            }
        }
        if (-not $after -and $a.kind -eq 'winget' -and -not $a.match -and $job -and $job.ExitCode -eq 0) {
            # programs without an entry in the list of installed programs (portable packages): ask winget
            $after = Invoke-Command -Session $session -ArgumentList $a.id -ScriptBlock { param($id) $w = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'; $o = (& $w list --id $id -e --source winget --accept-source-agreements --disable-interactivity 2>&1 | Out-String); if ($LASTEXITCODE -eq 0 -and $o -match [regex]::Escape($id)) { "winget list: " + (($o -split "`n" | Where-Object { $_ -match [regex]::Escape($id) } | Select-Object -First 1) -replace '\s+', ' ').Trim() } }
        }
        # a string that came back from the guest carries remoting properties (PSComputerName ...): keep the text only
        if ($after) { $after = "$after" }
        $ok = [bool]$after
        if (-not $ok) { $failed++ }
        $tail = ''
        if ($job -and $job.Log) { $tail = (($job.Log -split "`r?`n" | Where-Object { $_ -match '\S' } | Select-Object -Last 6) -join ' / ') }
        $shot = $null
        if (-not $ok) {
            # keep what the screen shows: a window waiting for a click explains most failures
            try { $shot = Save-VmScreenshot $vmName (Join-Path (Get-CloneDir $cfg $Name) ("install-fail-$id-" + (Get-Date -Format 'HHmmss') + '.png')) 1280 720 } catch { }
        }
        $entry = [pscustomobject]@{
            id = $id; name = $a.name; kind = $a.kind; ok = $ok; found = $after
            exit_code = $(if ($job) { $job.ExitCode } else { $null }); timed_out = $(if ($job) { $job.TimedOut } else { $false })
            error = $err; seconds = [int]$sw.Elapsed.TotalSeconds; at = (Get-Date).ToString('o'); log_tail = $tail; screenshot = $shot
        }
        $record = @($record | Where-Object { $_.id -ne $id }) + $entry
        ConvertTo-Json -InputObject @($record) -Depth 4 | Set-Content -Path $recordPath -Encoding UTF8
        if ($ok) { Write-Host ("  [ok]    {0,-18} {1}  ({2} s)" -f $id, $after, $entry.seconds) }
        else { Write-Host ("  [FAIL]  {0,-18} exit {1}{2}{3}  ({4} s){5}" -f $id, $entry.exit_code, $(if ($entry.timed_out) { ' TIMED OUT' } else { '' }), $(if ($err) { " $err" } else { '' }), $entry.seconds, $(if ($tail) { "`n          $tail" } else { '' })) ; if ($shot) { Write-Host "          screen: $shot" } }
    }
    # Last look at everything this run recorded as failed: an installer that outlived its time limit may have
    # finished while the later programs were installed. The record must say what is in the guest.
    foreach ($id in $ids) {
        $old = @($record | Where-Object { $_.id -eq $id }) | Select-Object -First 1
        if (-not $old -or $old.ok) { continue }
        $a = $catalog.apps.$id
        $now = Invoke-Command -Session $session -ScriptBlock $lookup -ArgumentList $a.match, (Get-CheckFile $id $a)
        if (-not $now) { continue }
        $old.ok = $true; $old.found = "$now"
        $old | Add-Member -NotePropertyName rechecked_at -NotePropertyValue (Get-Date).ToString('o') -Force
        $old | Add-Member -NotePropertyName recheck_note -NotePropertyValue 'recorded as failed when its time limit ran out; found in the guest at the end of the run' -Force
        $failed--
        Write-Host ("  [ok*]   {0,-18} {1}  (found at the end of the run; was recorded as failed)" -f $id, $now)
    }
    ConvertTo-Json -InputObject @($record) -Depth 4 | Set-Content -Path $recordPath -Encoding UTF8
    Write-Host "record: $recordPath"
    exit $(if ($failed -gt 0) { 1 } else { 0 })
} finally { Remove-PSSession $session }
