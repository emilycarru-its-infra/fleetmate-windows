<#
.SYNOPSIS
    Signs FleetMate.WinUI's own binaries after a build, when a code-signing
    certificate is installed.

.DESCRIPTION
    Called by the SignDevBuild target in FleetMate.WinUI.csproj. Finds the
    certificate the same way build.ps1 does: CERT_THUMBPRINT if set, otherwise
    the newest certificate with a private key whose subject matches
    ENTERPRISE_CERT_CN or CIMIAN_CERT_SUBJECT. With no certificate (CI, a fresh
    machine) it says so and exits 0, so the build still succeeds.
#>
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string[]]$Files
)

$ErrorActionPreference = 'Stop'

$cn = if ($env:ENTERPRISE_CERT_CN) { $env:ENTERPRISE_CERT_CN } else { 'Enterprise Code Signing Certificate' }
$subject = if ($env:CIMIAN_CERT_SUBJECT) { $env:CIMIAN_CERT_SUBJECT } else { 'ExampleOrgU' }

$cert = $null
foreach ($store in 'CurrentUser', 'LocalMachine') {
    $candidates = Get-ChildItem "Cert:\$store\My" -ErrorAction SilentlyContinue | Where-Object HasPrivateKey
    $cert = if ($env:CERT_THUMBPRINT) {
        $candidates | Where-Object Thumbprint -eq $env:CERT_THUMBPRINT | Select-Object -First 1
    } else {
        $candidates | Where-Object { $_.Subject -like "*$cn*" -or $_.Subject -like "*$subject*" } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
    }
    if ($cert) { $storeArgs = if ($store -eq 'LocalMachine') { @('/s', 'My', '/sm') } else { @('/s', 'My') }; break }
}
if (-not $cert) {
    Write-Host 'SignDevBuild: no code-signing certificate installed; binaries left unsigned.'
    exit 0
}

$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
if (-not $signtool) {
    $root = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signtool = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) {
    Write-Warning 'SignDevBuild: signtool.exe not found; install the Windows SDK signing tools.'
    exit 0
}

foreach ($name in $Files) {
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path)) { continue }

    # An incremental build that left the file alone keeps its signature.
    $sig = Get-AuthenticodeSignature -LiteralPath $path
    if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Thumbprint -eq $cert.Thumbprint) { continue }

    $base = @('sign') + $storeArgs + @('/sha1', $cert.Thumbprint, '/fd', 'SHA256')
    & $signtool @($base + @('/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $path)) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # A dev build does not need a timestamp; don't fail it over a timestamp server.
        & $signtool @($base + @($path)) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "SignDevBuild: signtool failed for $path" }
    }
    Write-Host "SignDevBuild: signed $name"
}
