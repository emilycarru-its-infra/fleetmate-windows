param(
    [Parameter(Mandatory = $true)]
    [string]$AssetTag
)

$ErrorActionPreference = "Continue"

function Invoke-FleetMateCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Exe,
        [Parameter(Mandatory = $true)]
        [string[]]$Args
    )

    try {
        $output = & $Exe @Args 2>&1 | Out-String
        return $output.TrimEnd()
    } catch {
        return $_.Exception.Message
    } finally {
        $global:LASTEXITCODE = 0
    }
}

$logDir = Join-Path $PSScriptRoot "..\logs"
$logDir = (Resolve-Path $logDir).Path
if (!(Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

$fleetmateRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fleetmateExe = Join-Path $fleetmateRoot "publish\fleetmate.exe"

$ts = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = Join-Path $logDir "system-check-$AssetTag-$ts.log"

"FleetMate system check for asset tag $AssetTag" | Tee-Object -FilePath $logFile
"Timestamp: $(Get-Date -Format u)" | Tee-Object -FilePath $logFile -Append
"" | Tee-Object -FilePath $logFile -Append

"=== ReportMate: device lookup (asset tag) ===" | Tee-Object -FilePath $logFile -Append
$deviceOutput = Invoke-FleetMateCommand $fleetmateExe @("device", $AssetTag)
$deviceOutput | Tee-Object -FilePath $logFile -Append

# Parse device details from ReportMate table output
$serial = $null
$owner = $null
try {
    $deviceOutput -split "`n" | ForEach-Object {
        if ($_ -match "│\s*Serial\s*│\s*(.+?)\s*│") { $serial = $matches[1].Trim() }
        if ($_ -match "│\s*Owner\s*│\s*(.+?)\s*│") { $owner = $matches[1].Trim() }
    }
} catch {}

"" | Tee-Object -FilePath $logFile -Append

"=== Snipe: asset detail (asset tag) ===" | Tee-Object -FilePath $logFile -Append
Invoke-FleetMateCommand $fleetmateExe @("snipe", "asset", $AssetTag) | Tee-Object -FilePath $logFile -Append
"" | Tee-Object -FilePath $logFile -Append

"=== Entra: user lookup (from device owner if available) ===" | Tee-Object -FilePath $logFile -Append

# Fall back to Snipe if no owner in ReportMate
if (-not $owner) {
    $assetJson = Invoke-FleetMateCommand $fleetmateExe @("snipe", "asset", $AssetTag, "--json")
    try {
        $asset = $assetJson | ConvertFrom-Json
        if ($asset.assignedTo -and $asset.assignedTo.username) { $owner = $asset.assignedTo.username }
        elseif ($asset.assignedTo -and $asset.assignedTo.name) { $owner = $asset.assignedTo.name }
    } catch {}
}

if ($owner) {
    $upn = if ($owner -match "@") { $owner } else { "$owner@ecuad.ca" }
    "Resolved user: $upn" | Tee-Object -FilePath $logFile -Append
    Invoke-FleetMateCommand $fleetmateExe @("entra", "user", $upn) | Tee-Object -FilePath $logFile -Append
} else {
    "No assigned user found; skipping Entra lookup." | Tee-Object -FilePath $logFile -Append
}
"" | Tee-Object -FilePath $logFile -Append

"=== Intune: basic info lookup (serial from device) ===" | Tee-Object -FilePath $logFile -Append

# Fall back to Snipe if no serial in ReportMate
if (-not $serial -and $asset -and $asset.serial) {
    $serial = $asset.serial
}

if ($serial) {
    "Serial: $serial" | Tee-Object -FilePath $logFile -Append
    Invoke-FleetMateCommand $fleetmateExe @("intune", "device", $serial) | Tee-Object -FilePath $logFile -Append
} else {
    "No serial found; skipping Intune lookup." | Tee-Object -FilePath $logFile -Append
}
"" | Tee-Object -FilePath $logFile -Append

"=== TDX: asset info (search + detail) ===" | Tee-Object -FilePath $logFile -Append
$tdxAssetId = $null
$tdxAssetsJson = Invoke-FleetMateCommand $fleetmateExe @("tdx", "assets", "--search", $AssetTag, "--limit", "5", "--json")
$tdxJsonPayload = $tdxAssetsJson
if ($tdxAssetsJson -match "(?s)(\[.*\])") { $tdxJsonPayload = $matches[1] }
try {
    $tdxAssets = $tdxJsonPayload | ConvertFrom-Json
    if ($tdxAssets -and $tdxAssets[0]) {
        $tdxAssetId = $tdxAssets[0].id
        if (-not $tdxAssetId) { $tdxAssetId = $tdxAssets[0].ID }
        if (-not $tdxAssetId) { $tdxAssetId = $tdxAssets[0].Id }
    }
} catch {}
if ($tdxAssetId) {
    "TDX asset ID: $tdxAssetId" | Tee-Object -FilePath $logFile -Append
    $tdxAssetJson = Invoke-FleetMateCommand $fleetmateExe @("tdx", "asset", $tdxAssetId, "--json")
    try {
        $tdxAssetPayload = $tdxAssetJson
        if ($tdxAssetJson -match "(?s)(\{.*\})") { $tdxAssetPayload = $matches[1] }
        $tdxAsset = $tdxAssetPayload | ConvertFrom-Json
        "" | Tee-Object -FilePath $logFile -Append
        "+-------------------- TDX Asset --------------------+" | Tee-Object -FilePath $logFile -Append
        "Name        : $($tdxAsset.Name)" | Tee-Object -FilePath $logFile -Append
        "Asset Tag   : $($tdxAsset.Tag)" | Tee-Object -FilePath $logFile -Append
        "Serial      : $($tdxAsset.SerialNumber)" | Tee-Object -FilePath $logFile -Append
        "Model       : $($tdxAsset.ProductModelName)" | Tee-Object -FilePath $logFile -Append
        "Manufacturer: $($tdxAsset.ManufacturerName)" | Tee-Object -FilePath $logFile -Append
        "Status      : $($tdxAsset.StatusName)" | Tee-Object -FilePath $logFile -Append
        "Location    : $($tdxAsset.LocationName)" | Tee-Object -FilePath $logFile -Append
        "Created     : $($tdxAsset.CreatedDate)" | Tee-Object -FilePath $logFile -Append
        "Modified    : $($tdxAsset.ModifiedDate)" | Tee-Object -FilePath $logFile -Append
        "+---------------------------------------------------+" | Tee-Object -FilePath $logFile -Append
        "" | Tee-Object -FilePath $logFile -Append
    } catch {}
} else {
    "No TDX asset match found; showing asset search output." | Tee-Object -FilePath $logFile -Append
    $tdxAssetsJson | Tee-Object -FilePath $logFile -Append
}
"" | Tee-Object -FilePath $logFile -Append

"=== SecureShell: run command (hostname) ===" | Tee-Object -FilePath $logFile -Append
Invoke-FleetMateCommand $fleetmateExe @("ssh", "exec", $AssetTag, "hostname") | Tee-Object -FilePath $logFile -Append
"" | Tee-Object -FilePath $logFile -Append

"Log saved to: $logFile" | Tee-Object -FilePath $logFile -Append
Write-Host "Log saved to: $logFile"
exit 0