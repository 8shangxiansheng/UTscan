# UTscan manifest generator (called by publish-self-contained.cmd)
# Emits manifest.json (per-file SHA256) and version.json in AppDir.
# ASCII-only output; JSON uses UTF-8 (no BOM).
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$Version,
    # Previous version for sequential-upgrade chain check; pass -Previous when known
    [string]$Previous = "",
    # Build id, defaults to yyyyMMdd
    [string]$Build = "",
    [string]$Date = ""
)

$ErrorActionPreference = 'Stop'
if (-not $Build)     { $Build = (Get-Date).ToString('yyyyMMdd') }
if (-not $Date)      { $Date  = (Get-Date).ToString('yyyy-MM-dd') }

# Files never shipped/updated through the update channel
$ProtectedNames = @('hardware.json')

$sha = [System.Security.Cryptography.SHA256]::Create()
$files = New-Object System.Collections.Generic.List[object]

Get-ChildItem -LiteralPath $AppDir -Recurse -File | ForEach-Object {
    $rel = [System.IO.Path]::GetRelativePath($AppDir, $_.FullName).Replace('\', '/')
    if ($ProtectedNames -contains $_.Name) { return }   # skip protected config
    $hash = $sha.ComputeHash([System.IO.File]::ReadAllBytes($_.FullName))
    $hex  = ($hash | ForEach-Object { $_.ToString('x2') }) -join ''
    $files.Add(@{
        path   = $rel
        sha256 = $hex
        size   = $_.Length
    })
}

$manifest = @{
    version  = $Version
    build    = $Build
    date     = $Date
    previous = $Previous
    files    = $files
}

$outManifest = Join-Path $AppDir 'manifest.json'
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($outManifest, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("manifest: {0} files -> {1}" -f $files.Count, $outManifest)

# version.json consumed by Program.VersionInfo at startup
$versionInfo = @{
    version = $Version
    build   = $Build
    date    = $Date
}
$outVersion = Join-Path $AppDir 'version.json'
[System.IO.File]::WriteAllText($outVersion, ($versionInfo | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("version : v{0} (build {1}) -> {2}" -f $Version, $Build, $outVersion)
