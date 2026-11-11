# Finds files that contain an AI service key prefix ("sk-ant-"). Used by tools\publish-participant.ps1 and tools\release-participant.ps1
# so that a build with an embedded key never goes to participants or to public GitHub Releases (docs/product-architecture-v2.md 3.4). ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File tools\find-api-key.ps1 -Dir dist\PcBlackBox-0.9.0-win-x64      # prints the files, exit 1 when any
#   powershell -ExecutionPolicy Bypass -File tools\find-api-key.ps1 -Zip dist\update\...\x-update.zip
#   $hits = & tools\find-api-key.ps1 -Dir <folder>          # from a script: the paths are the output, $LASTEXITCODE is 1 when any
#
# Looks for the prefix as ASCII/UTF-8 bytes and as UTF-16LE bytes (how .NET stores string literals), followed by at least 20 key characters,
# so the bare prefix in documentation or in this script does not count. It cannot see a key that was split, encoded or encrypted.
param(
    [string]$Dir,
    [string]$Zip,
    [int]$MinTail = 20
)
$ErrorActionPreference = 'Stop'

function Test-KeyBytes([byte[]]$data, [int]$minTail) {
    $latin = [Text.Encoding]::GetEncoding(28591)                     # one byte = one char, so byte patterns can be matched with a regex
    $text = $latin.GetString($data)
    $tail = '[A-Za-z0-9_\-]'
    if ($text -match ('sk-ant-' + $tail + '{' + $minTail + ',}')) { return $true }
    $wide = 's\x00k\x00-\x00a\x00n\x00t\x00-\x00(?:' + $tail + '\x00){' + $minTail + ',}'
    return ($text -match $wide)
}

$hits = New-Object System.Collections.Generic.List[string]
if ($Dir) {
    if (-not (Test-Path $Dir)) { throw "Not found: $Dir" }
    foreach ($f in Get-ChildItem $Dir -Recurse -File) {
        if (Test-KeyBytes ([IO.File]::ReadAllBytes($f.FullName)) $MinTail) { $hits.Add($f.FullName) }
    }
}
if ($Zip) {
    if (-not (Test-Path $Zip)) { throw "Not found: $Zip" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Zip).Path)
    try {
        foreach ($e in $z.Entries) {
            if ($e.Length -eq 0) { continue }
            $ms = New-Object IO.MemoryStream
            $s = $e.Open()
            try { $s.CopyTo($ms) } finally { $s.Dispose() }
            if (Test-KeyBytes $ms.ToArray() $MinTail) { $hits.Add("$Zip!$($e.FullName)") }
        }
    } finally { $z.Dispose() }
}
if (-not $Dir -and -not $Zip) { throw 'Give -Dir or -Zip.' }

# The paths go to the output (a calling script captures them); the summary goes to the console only.
$hits
if ($hits.Count) { $hits | ForEach-Object { Write-Host "KEY FOUND: $_" }; exit 1 }
Write-Host "No AI service key found."
exit 0
