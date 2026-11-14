# Builds what a case run copies into a guest: the console collector and the fault injection test program. ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\payload.ps1            # build if missing or older than the sources
#   powershell -ExecutionPolicy Bypass -File lab\vm\payload.ps1 -Force
#
# Result: <vm root>\payload\case-payload.zip (+ payload.json with the git commit and time it was built from).
#   collector\   BlackBox console edition, self-contained (the guest has no .NET). It only collects, buffers and dumps;
#                the console edition sends nothing over the network (the participant edition is NOT used in guests).
#   faultlab\    collector\experiments\FaultLab, self-contained, plus FaultHost.exe / FaultPlugin.dll when they are built
#                (and Microsoft's vcruntime140.dll next to them while FaultHost.exe is a /MD build).
# Nothing here is committed: the payload lives under the VM root.
param([switch]$Force, [string]$Root)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dir = Join-Path $cfg.root 'payload'
$zip = Join-Path $dir 'case-payload.zip'
$info = Join-Path $dir 'payload.json'
$srcDirs = @((Join-Path $repo 'collector\BlackBox'), (Join-Path $repo 'collector\experiments\FaultLab'))
$newest = (@(Get-ChildItem $srcDirs -Recurse -File -Include *.cs, *.csproj, *.c | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) + @(Get-Item (Join-Path $PSScriptRoot 'aliases.json')) | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
$aliasIds = @((Get-Content (Join-Path $PSScriptRoot 'aliases.json') -Raw -Encoding UTF8 | ConvertFrom-Json).aliases | ForEach-Object { $_.id })
$aliasesReady = -not @($aliasIds | Where-Object { $z = Join-Path (Join-Path $dir 'aliases') "$_.zip"; -not (Test-Path $z) -or (Get-Item $z).LastWriteTime -le $newest }).Count
if (-not $Force -and (Test-Path $zip) -and (Get-Item $zip).LastWriteTime -gt $newest -and $aliasesReady) { Write-Host "payload is up to date: $zip"; exit 0 }

$stage = Join-Path $dir 'stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'collector'), (Join-Path $stage 'faultlab') | Out-Null

Write-Host 'publishing the collector (console edition)...'
& dotnet publish (Join-Path $repo 'collector\BlackBox\BlackBox.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'collector') -p:PublishSingleFile=false -p:DebugType=none --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (collector) failed: $LASTEXITCODE" }
Write-Host 'publishing FaultLab...'
& dotnet publish (Join-Path $repo 'collector\experiments\FaultLab\FaultLab.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'faultlab') -p:PublishSingleFile=false -p:DebugType=none --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (FaultLab) failed: $LASTEXITCODE" }
$plugin = Join-Path $repo 'collector\experiments\FaultLab\plugin\build\Release'
$hasPlugin = $false
$pluginRuntime = $null
if ((Test-Path (Join-Path $plugin 'FaultHost.exe')) -and (Test-Path (Join-Path $plugin 'FaultPlugin.dll'))) {
    Copy-Item (Join-Path $plugin 'FaultHost.exe'), (Join-Path $plugin 'FaultPlugin.dll') (Join-Path $stage 'faultlab')
    $hasPlugin = $true
    # A FaultHost.exe built with the DLL runtime (/MD) needs VCRUNTIME140.dll, which a fresh Windows does not have:
    # on 2026-11-12 it ended with 0xC0000135 in a guest without the VC++ redistributable and no crash was made.
    # Until it is rebuilt with /MT (plugin\CMakeLists.txt), Microsoft's own DLL is put next to it (app-local).
    $bytes = [IO.File]::ReadAllBytes((Join-Path $plugin 'FaultHost.exe'))
    $needsRuntime = [Text.Encoding]::ASCII.GetString($bytes).IndexOf('VCRUNTIME140.dll', [StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $needsRuntime) { $pluginRuntime = 'static (no VCRUNTIME140.dll import)' }
    else {
        $cands = @()
        $vsw = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vsw) { foreach ($vs in @(& $vsw -all -products * -property installationPath)) { if ($vs) { $cands += (Join-Path $vs 'Common7\IDE\vcruntime140.dll') } } }
        $cands += (Join-Path $env:SystemRoot 'System32\vcruntime140.dll')
        foreach ($c in $cands) {
            if (-not (Test-Path $c)) { continue }
            $b = [IO.File]::ReadAllBytes($c)
            $machine = [BitConverter]::ToUInt16($b, [BitConverter]::ToInt32($b, 0x3c) + 4)
            $sig = Get-AuthenticodeSignature $c
            if ($machine -eq 0x8664 -and $sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -match 'O=Microsoft Corporation') {
                Copy-Item $c (Join-Path $stage 'faultlab')
                $pluginRuntime = "app-local vcruntime140.dll $((Get-Item $c).VersionInfo.FileVersion) from $c"
                break
            }
        }
        if (-not $pluginRuntime) { Write-Host 'WARNING: FaultHost.exe needs VCRUNTIME140.dll and no Microsoft-signed x64 copy was found: crash-plugin works only in guests that have the VC++ redistributable.' }
    }
} else { Write-Host 'FaultHost.exe / FaultPlugin.dll are not built: the crash-plugin scenario will report that.' }

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item -Recurse -Force $stage

# The fault program under ordinary names (aliases.json): one zip per alias, <payload>\aliases\<id>.zip. Each is the same
# program built with another assembly name and version information, plus the native host and DLL under the alias's file
# names. Nothing in such a folder is called FaultLab.
$aliasDir = Join-Path $dir 'aliases'
New-Item -ItemType Directory -Force -Path $aliasDir | Out-Null
$aliases = @((Get-Content (Join-Path $PSScriptRoot 'aliases.json') -Raw -Encoding UTF8 | ConvertFrom-Json).aliases)
$built = 0
foreach ($a in $aliases) {
    $azip = Join-Path $aliasDir "$($a.id).zip"
    $astage = Join-Path $aliasDir "stage-$($a.id)"
    if (Test-Path $astage) { Remove-Item -Recurse -Force $astage }
    # (a separate intermediate folder per alias was tried and failed: the project's own obj\ then gets compiled a second time)
    & dotnet publish (Join-Path $repo 'collector\experiments\FaultLab\FaultLab.csproj') -c Release -r win-x64 --self-contained true -o $astage `
        "-p:AssemblyName=$($a.exe)" "-p:Company=$($a.vendor)" "-p:Product=$($a.product)" "-p:AssemblyTitle=$($a.product)" "-p:Description=$($a.product)" `
        -p:PublishSingleFile=false -p:DebugType=none --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (alias $($a.id)) failed: $LASTEXITCODE" }
    if ($hasPlugin) {
        Copy-Item (Join-Path $plugin 'FaultHost.exe') (Join-Path $astage $a.host)
        Copy-Item (Join-Path $plugin 'FaultPlugin.dll') (Join-Path $astage $a.plugin)
    }
    $leak = @(Get-ChildItem $astage -Recurse -File | Where-Object { $_.Name -match 'fault' })
    if ($leak.Count) { throw "alias $($a.id): file names still say 'fault': $($leak.Name -join ', ')" }
    if (Test-Path $azip) { Remove-Item -Force $azip }
    Compress-Archive -Path (Join-Path $astage '*') -DestinationPath $azip
    Remove-Item -Recurse -Force $astage
    $built++
}
Write-Host ("aliases: {0} built under {1} ({2:N0} MB in all)" -f $built, $aliasDir, ((Get-ChildItem $aliasDir -Filter *.zip | Measure-Object Length -Sum).Sum / 1MB))
$commit = (& git -c safe.directory=* -C $repo rev-parse --short HEAD 2>$null | Out-String).Trim()
$dirty = [bool](& git -c safe.directory=* -C $repo status --porcelain -- collector/BlackBox collector/experiments/FaultLab 2>$null | Out-String).Trim()
[pscustomobject]@{ built_at = (Get-Date).ToString('o'); commit = $commit; collector_sources_modified = $dirty; fault_plugin = $hasPlugin; fault_plugin_runtime = $pluginRuntime; zip_mb = [math]::Round((Get-Item $zip).Length / 1MB, 1) } |
    ConvertTo-Json | Set-Content -Path $info -Encoding UTF8
Write-Host ("payload: {0} ({1} MB, commit {2}{3})" -f $zip, [math]::Round((Get-Item $zip).Length / 1MB, 1), $commit, $(if ($dirty) { ', collector sources modified' } else { '' }))
