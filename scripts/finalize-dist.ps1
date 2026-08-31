# UTscan dist finalizer (called by publish-self-contained.cmd)
# Copies config/docs/drivers into the publish output, then generates
# manifest.json (per-file SHA256) and version.json.
# UTF-8 with BOM so PowerShell 5.1 reads the Chinese asset paths correctly.
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$Version,
    # Previous version for sequential-upgrade chain check; pass -Previous when known
    [string]$Previous = "",
    # Build id, defaults to yyyyMMdd
    [string]$Build = "",
    [string]$Date = ""
)

$ErrorActionPreference = 'Stop'
if (-not $Build) { $Build = (Get-Date).ToString('yyyyMMdd') }
if (-not $Date)  { $Date  = (Get-Date).ToString('yyyy-MM-dd') }

Write-Host '[4a] copy hardware.json / docs / drivers ...'

# Current field config ships only in the FIRST install package; the update
# channel never touches it (UpdateService double protection).
Copy-Item -LiteralPath (Join-Path $Root 'src/UTscan/hardware.json') -Destination (Join-Path $AppDir 'hardware.json') -Force

$docTargets = @(
    @{ src = 'DOCS/HARDWARE-CONFIG.md'; dst = 'HARDWARE-CONFIG.md' },
    @{ src = 'DOCS/部署清单.txt';        dst = '部署清单.txt' }
)
foreach ($t in $docTargets) {
    $s = Join-Path $Root $t.src
    if (Test-Path -LiteralPath $s) {
        Copy-Item -LiteralPath $s -Destination (Join-Path $AppDir $t.dst) -Force
    }
}

$driverDir = Join-Path $AppDir 'drivers'
New-Item -ItemType Directory -Force -Path $driverDir | Out-Null
$drivers = @(
    '数据采集卡/M3i.3242-exp-德国Spectrum/CD_SPCM_348a/Driver/windows/spcm_drv_install_4.0.13877.exe',
    '超声脉冲发生接收器/JSRControlPanelInstaller_3_3_0_0/JSRControlPanelInstaller_3_3_0_0/JSRControlPanelInstaller.3.3.0.0.exe'
)
foreach ($d in $drivers) {
    $s = Join-Path $Root $d
    if (Test-Path -LiteralPath $s) {
        Copy-Item -LiteralPath $s -Destination $driverDir -Force
        Write-Host ("  driver: {0}" -f (Split-Path $s -Leaf))
    }
    else {
        Write-Warning ("driver missing, skipped: {0}" -f $s)
    }
}

Write-Host '[4b] generate manifest.json + version.json ...'

# Files never shipped/updated through the update channel
$ProtectedNames = @('hardware.json')

$AppDirRooted = (Get-Item -LiteralPath $AppDir).FullName.TrimEnd('\') + '\'

$sha = [System.Security.Cryptography.SHA256]::Create()
$files = New-Object System.Collections.Generic.List[object]

Get-ChildItem -LiteralPath $AppDir -Recurse -File | ForEach-Object {
    $full = $_.FullName
    $rel = $full.Substring($AppDirRooted.Length).TrimStart('\','/').Replace('\','/')
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
[System.IO.File]::WriteAllText($outManifest, ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
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
