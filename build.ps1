#Requires -Version 7.0
<#
.SYNOPSIS
    Builds FleetMate CLI with enterprise code signing.

.PARAMETER Sign
    Sign binary with enterprise certificate (required for production)

.PARAMETER Clean
    Clean build artifacts before building

.PARAMETER Publish
    Create single-file self-contained executable

.EXAMPLE
    .\build.ps1 -Sign
    # Build and sign FleetMate
#>

[CmdletBinding()]
param(
    [switch]$Sign,
    [switch]$Clean,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$RootDir = $PSScriptRoot

# Enterprise certificate configuration
$EnterpriseCertSubject = $env:CIMIAN_CERT_SUBJECT ?? 'EmilyCarrU'

function Write-BuildLog {
    param([string]$Message, [ValidateSet("INFO","WARNING","ERROR","SUCCESS")][string]$Level = "INFO")
    $color = switch ($Level) { "INFO" { "Cyan" } "WARNING" { "Yellow" } "ERROR" { "Red" } "SUCCESS" { "Green" } }
    Write-Host "[$Level] " -NoNewline -ForegroundColor $color
    Write-Host $Message
}

function Get-SigningCertThumbprint {
    # Check CurrentUser store first
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.HasPrivateKey -and $_.Subject -like "*$EnterpriseCertSubject*"
    } | Sort-Object NotAfter -Descending | Select-Object -First 1

    if ($cert) { return @{ Thumbprint = $cert.Thumbprint; Store = "CurrentUser" } }

    # Check LocalMachine store
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object {
        $_.HasPrivateKey -and $_.Subject -like "*$EnterpriseCertSubject*"
    } | Sort-Object NotAfter -Descending | Select-Object -First 1

    if ($cert) { return @{ Thumbprint = $cert.Thumbprint; Store = "LocalMachine" } }
    return $null
}

function Get-SignToolPath {
    # Check PATH
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c -and $c.Source -match '\\x64\\') { return $c.Source }

    # Search Windows SDK
    $programFilesx86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $searchRoot = Join-Path $programFilesx86 "Windows Kits\10\bin"

    if (Test-Path $searchRoot) {
        $candidates = Get-ChildItem -Path $searchRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object { $_.Directory.Parent.Name } -Descending
        if ($candidates.Count -gt 0) { return $candidates[0].FullName }
    }
    return $null
}

function Invoke-SignArtifact {
    param([string]$Path, [string]$Thumbprint, [string]$Store = "CurrentUser")

    $signToolExe = Get-SignToolPath
    if (-not $signToolExe) { throw "signtool.exe not found" }

    $storeParam = if ($Store -eq "CurrentUser") { "/s", "My" } else { "/s", "My", "/sm" }
    $tsas = @('http://timestamp.digicert.com', 'http://timestamp.sectigo.com')

    foreach ($tsa in $tsas) {
        try {
            Write-BuildLog "Signing: $Path"
            $signArgs = @("sign", "/sha1", $Thumbprint, "/tr", $tsa, "/td", "sha256", "/fd", "sha256") + $storeParam + @($Path)

            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $signToolExe
            $psi.Arguments = $signArgs -join ' '
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.CreateNoWindow = $true

            $process = [System.Diagnostics.Process]::Start($psi)
            $process.WaitForExit()

            if ($process.ExitCode -eq 0) {
                Write-BuildLog "Signed: $Path" "SUCCESS"
                return
            }
        } catch {
            Write-BuildLog "Signing attempt failed: $_" "WARNING"
        }
        Start-Sleep -Seconds 2
    }
    throw "Signing failed: $Path"
}

# Main execution
try {
    Write-Host "`n=== FleetMate Build ===" -ForegroundColor Cyan

    # Clean if requested
    if ($Clean) {
        Write-BuildLog "Cleaning build artifacts..."
        Remove-Item -Path "$RootDir\bin" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path "$RootDir\obj" -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Check signing
    $certInfo = $null
    if ($Sign) {
        $certInfo = Get-SigningCertThumbprint
        if (-not $certInfo) {
            throw "No enterprise signing certificate found. Install certificate or omit -Sign."
        }
        Write-BuildLog "Using certificate: $($certInfo.Thumbprint)" "SUCCESS"
    }

    # Build
    $outputDir = Join-Path $RootDir "bin\Release\net10.0-windows\win-x64"

    if ($Publish) {
        Write-BuildLog "Publishing self-contained single-file executable..."
        & dotnet publish "$RootDir\FleetMate.csproj" `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true `
            --output $outputDir
    } else {
        Write-BuildLog "Building FleetMate..."
        & dotnet build "$RootDir\FleetMate.csproj" --configuration Release
    }

    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-BuildLog "Build completed" "SUCCESS"

    # Sign
    if ($Sign -and $certInfo) {
        $exePath = Join-Path $outputDir "fleetmate.exe"
        if (Test-Path $exePath) {
            Invoke-SignArtifact -Path $exePath -Thumbprint $certInfo.Thumbprint -Store $certInfo.Store
        } else {
            Write-BuildLog "Executable not found at $exePath" "WARNING"
        }
    }

    # Summary
    Write-Host "`n=== Build Complete ===" -ForegroundColor Green
    if (Test-Path $outputDir) {
        Get-ChildItem $outputDir -Filter "*.exe" | ForEach-Object {
            $size = [math]::Round($_.Length / 1MB, 1)
            Write-Host "  $($_.Name) ($size MB)"
        }
    }

} catch {
    Write-BuildLog "Build failed: $_" "ERROR"
    exit 1
}
