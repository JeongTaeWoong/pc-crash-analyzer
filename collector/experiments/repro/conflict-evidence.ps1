# Read-only. Run after morning-elevated.ps1 (it calls this once) and again after each reboot. Records what the installs left that
# bears on conflicts and attack surface; it judges nothing:
#   - error events after the first install, next to the same events in an equally long window just before it
#     (this PC already logs service-start failures every day, so counts alone would overstate the installs)
#   - for every service and driver the installs added: file version, signature, and whether non-admins can write the file or its folder
#   - per product: new listening ports, root certificates and minifilters (from install-diff)
#   powershell -ExecutionPolicy Bypass -File collector\experiments\repro\conflict-evidence.ps1 [-Label after-reboot] [-Since 2026-11-10T10:00]
# Output stays in repro-runs (git-ignored). Paths under the user profile are written as %USERPROFILE%.
param(
    [string]$Label = 'after-install',
    [datetime]$Since,
    [string]$DiffDir = 'F:\2026CAU\Capstone2\pc-crash-analyzer\collector\experiments\repro-runs\install-diff',
    [string]$OutDir = 'F:\2026CAU\Capstone2\pc-crash-analyzer\collector\experiments\repro-runs\evidence'
)
$ErrorActionPreference = 'Stop'

function Hide([string]$s) { if ($s) { $s.Replace($env:USERPROFILE, '%USERPROFILE%') } else { $s } }
function Unavailable([string]$why) { [ordered]@{ status = 'unavailable'; reason = (Hide $why) } }

$diffs = @(Get-ChildItem -LiteralPath $DiffDir -Filter *.json -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName | ConvertFrom-Json })
if (-not $Since) {
    $first = @($diffs | ForEach-Object { [datetime]$_.at } | Sort-Object)[0]
    # 'at' is stamped 30 s after the installer exits, and an install may run up to InstallTimeoutSec (600 s)
    $Since = if ($first) { $first.AddMinutes(-15) } else { (Get-Date).AddHours(-3) }
}
$now = Get-Date
$before = $Since - ($now - $Since)

# --- events ---
$eventSets = @(
    @{ log = 'System'; ids = 41, 1001, 6008, 219, 7000, 7001, 7009, 7011, 7023, 7024, 7026, 7031, 7034 }
    @{ log = 'Application'; ids = 1000, 1001, 1002 }
    @{ log = 'Microsoft-Windows-CodeIntegrity/Operational'; ids = 3033, 3076, 3077 }
)
function Get-Events([string]$log, [int[]]$ids, [datetime]$from, [datetime]$to) {
    try { return @(Get-WinEvent -FilterHashtable @{ LogName = $log; Id = $ids; StartTime = $from; EndTime = $to } -ErrorAction Stop) }
    catch { if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { return @() } throw }
}
$events = [ordered]@{}
foreach ($set in $eventSets) {
    try {
        $after = Get-Events $set.log $set.ids $Since $now
        $base = Get-Events $set.log $set.ids $before $Since
        $events[$set.log] = [ordered]@{
            countsAfter = [ordered]@{}; countsBefore = [ordered]@{}
            items = @($after | Select-Object -First 200 | ForEach-Object {
                    $m = "$($_.Message)" -replace '\s+', ' '
                    [ordered]@{ at = $_.TimeCreated.ToString('s'); id = $_.Id; provider = $_.ProviderName; message = (Hide $m.Substring(0, [math]::Min(400, $m.Length))) }
                })
        }
        foreach ($g in ($after | Group-Object Id)) { $events[$set.log].countsAfter["$($g.Name)"] = $g.Count }
        foreach ($g in ($base | Group-Object Id)) { $events[$set.log].countsBefore["$($g.Name)"] = $g.Count }
    }
    catch { $events[$set.log] = Unavailable $_.Exception.Message }
}

# --- binaries the installs added ---
$weakSids = 'S-1-1-0', 'S-1-5-11', 'S-1-5-32-545', 'S-1-5-4'   # Everyone, Authenticated Users, Users, Interactive
# WriteData, AppendData, Delete, ChangePermissions, TakeOwnership, GENERIC_ALL, GENERIC_WRITE
$writeMask = 0x2 -bor 0x4 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
function Get-WeakWrite([string]$path) {
    try {
        $rules = (Get-Acl -LiteralPath $path).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
        # leading comma: keep an empty result as [] instead of letting PowerShell unroll it to $null
        return , @($rules | Where-Object { $_.AccessControlType -eq 'Allow' -and $weakSids -contains $_.IdentityReference.Value -and ([int64]$_.FileSystemRights -band $writeMask) } |
                ForEach-Object { "$($_.IdentityReference.Value):$($_.FileSystemRights)" })
    }
    catch { return Unavailable $_.Exception.Message }
}
function Resolve-ImagePath([string]$p) {
    if (-not $p) { return $null }
    $p = $p.Trim()
    if ($p.StartsWith('"')) { $p = $p.Substring(1, $p.IndexOf('"', 1) - 1) }
    elseif ($p -match '^(.+?\.(exe|sys|dll))(\s|$)') { $p = $Matches[1] }
    $p = $p -replace '^\\\?\?\\', ''
    $p = $p -replace '^(?i)\\SystemRoot', $env:SystemRoot
    if ($p -match '^(?i)system32\\') { $p = Join-Path $env:SystemRoot $p }
    return $p
}
$binaries = @(foreach ($d in $diffs) {
        foreach ($kind in 'services', 'drivers') {
            foreach ($line in @($d."new_$kind")) {
                if (-not $line) { continue }
                $name, $mode, $raw = "$line" -split '\|', 3
                $file = Resolve-ImagePath $raw
                $row = [ordered]@{ product = $d.product; kind = $kind; name = $name; startMode = $mode; path = (Hide $file) }
                if (-not $file -or -not (Test-Path -LiteralPath $file)) { $row.file = Unavailable 'path not found'; $row; continue }
                $vi = (Get-Item -LiteralPath $file).VersionInfo
                $sig = Get-AuthenticodeSignature -LiteralPath $file
                $row.fileVersion = $vi.FileVersion
                $row.productVersion = $vi.ProductVersion
                $row.signature = "$($sig.Status)"
                $row.signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject.Split(',')[0] } else { $null }
                $row.fileWritableByNonAdmins = Get-WeakWrite $file
                $row.folderWritableByNonAdmins = Get-WeakWrite (Split-Path -Parent $file)
                $row
            }
        }
    })

# --- per product, from install-diff ---
$products = @(foreach ($d in $diffs) {
        [ordered]@{
            product = $d.product; version = $d.version; outcome = $d.outcome
            newListening = @($d.new_listening); newRootCerts = @($d.new_rootCerts); newMinifilters = @($d.new_minifilters)
            newDrivers = @($d.new_drivers).Count; newServices = @($d.new_services).Count; newAutoruns = @($d.new_autoruns)
        }
    })

# --- filter stacks as they are now ---
$kb = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E96B-E325-11CE-BFC1-08002BE10318}'
$flt = cmd /c "fltmc filters 2>&1"
$stacks = [ordered]@{
    keyboardUpperFilters = @((Get-ItemProperty $kb -ErrorAction SilentlyContinue).UpperFilters)
    minifilters = if ($LASTEXITCODE -eq 0) { @($flt | Select-Object -Skip 3 | Where-Object { $_.Trim() } | ForEach-Object { ($_ -split '\s{2,}' | Where-Object { $_ }) -join ' | ' }) } else { Unavailable 'fltmc needs administrator' }
}

$result = [ordered]@{
    label = $Label; at = $now.ToString('s'); since = $Since.ToString('s'); baselineFrom = $before.ToString('s')
    elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    installDiffs = $diffs.Count
    products = $products; binaries = $binaries; stacks = $stacks; events = $events
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = Join-Path $OutDir ("{0}-{1}.json" -f ($Label -replace '[^\w.-]', '_'), $now.ToString('yyyyMMdd-HHmmss'))
[IO.File]::WriteAllText($out, ($result | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))

Write-Host "[evidence] $out"
Write-Host ("[evidence] window {0} .. now, baseline from {1}; {2} install diffs, {3} new binaries" -f $result.since, $result.baselineFrom, $diffs.Count, $binaries.Count)
foreach ($k in $events.Keys) {
    $e = $events[$k]
    if ($e.status) { Write-Host "  $k : unavailable ($($e.reason))"; continue }
    $a = ($e.countsAfter.Keys | ForEach-Object { "$_=$($e.countsAfter[$_])" }) -join ' '
    $b = ($e.countsBefore.Keys | ForEach-Object { "$_=$($e.countsBefore[$_])" }) -join ' '
    Write-Host ("  {0} : after [{1}]  before [{2}]" -f $k, $a, $b)
}
$weak = @($binaries | Where-Object { @($_.fileWritableByNonAdmins | Where-Object { $_ -is [string] }).Count -or @($_.folderWritableByNonAdmins | Where-Object { $_ -is [string] }).Count })
$unsigned = @($binaries | Where-Object { $_.signature -and $_.signature -ne 'Valid' })
Write-Host ("  binaries: {0} writable by non-admins (file or folder), {1} without a valid signature" -f $weak.Count, $unsigned.Count)
Write-Host ("  keyboard upper filters: {0}" -f ($stacks.keyboardUpperFilters -join ' > '))
