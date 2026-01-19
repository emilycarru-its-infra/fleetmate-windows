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

.PARAMETER PkgOnly
    Create .pkg package from existing binaries (skip build)

.EXAMPLE
    .\build.ps1 -Sign
    # Build and sign FleetMate

.EXAMPLE
    .\build.ps1 -Publish -Sign -PkgOnly
    # Build, sign, and create .pkg package
#>

[CmdletBinding()]
param(
    [switch]$Sign,
    [switch]$Clean,
    [switch]$Publish,
    [switch]$PkgOnly
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

function Get-BuildVersion {
    $now = Get-Date
    $fullVersion = $now.ToString("yyyy.MM.dd.HHmm")
    $semanticVersion = $now.ToString("yyyy.M.d")
    
    return @{
        Full = $fullVersion
        Semantic = $semanticVersion
    }
}

function Build-PkgPackage {
    param(
        [hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint
    )
    
    Write-BuildLog "Creating .pkg package..." "INFO"
    
    $publishDir = Join-Path $RootDir "publish"
    $fleetmatePath = Join-Path $publishDir "fleetmate.dll"
    
    # Check if published binaries exist
    if (-not (Test-Path $publishDir)) {
        Write-BuildLog "Publish directory not found: $publishDir" "WARNING"
        Write-BuildLog "Run with -Publish first to create binaries" "INFO"
        return $null
    }
    
    if (-not (Test-Path $fleetmatePath)) {
        Write-BuildLog "fleetmate.dll not found: $fleetmatePath" "WARNING"
        return $null
    }
    
    # Create temporary .pkg build directory
    $pkgTempDir = Join-Path $RootDir "build\pkg_temp"
    if (Test-Path $pkgTempDir) { Remove-Item $pkgTempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $pkgTempDir -Force | Out-Null
    
    # Create payload directory and copy all published files
    $payloadDir = Join-Path $pkgTempDir "payload"
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    
    Write-BuildLog "Copying FleetMate binaries to .pkg payload..." "INFO"
    Copy-Item "$publishDir\*" $payloadDir -Recurse -Force
    
    # Create scripts directory and copy install scripts if they exist
    $scriptsDir = Join-Path $pkgTempDir "scripts"
    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
    
    $releaseVersion = $Version.Full
    $semanticVersion = $Version.Semantic
    
    # Copy or create postinstall script
    $postinstallTemplatePath = Join-Path $RootDir "scripts\postinstall.ps1"
    $postinstallPath = Join-Path $scriptsDir "postinstall.ps1"
    if (Test-Path $postinstallTemplatePath) {
        $postinstallTemplate = Get-Content $postinstallTemplatePath -Raw
        $postinstallContent = $postinstallTemplate -replace '\{\{VERSION\}\}', $semanticVersion
        $postinstallContent | Set-Content $postinstallPath -Encoding UTF8
        Write-BuildLog "Created postinstall.ps1 script" "INFO"
    } else {
        # Create default postinstall script
        $defaultPostinstall = @'
# FleetMate Postinstall Script
# Version: {{VERSION}}
Write-Host "FleetMate {{VERSION}} installed successfully"
'@
        $defaultPostinstall -replace '\{\{VERSION\}\}', $semanticVersion | Set-Content $postinstallPath -Encoding UTF8
        Write-BuildLog "Created default postinstall.ps1 script" "INFO"
    }
    
    # Copy or create preinstall script
    $preinstallTemplatePath = Join-Path $RootDir "scripts\preinstall.ps1"
    $preinstallPath = Join-Path $scriptsDir "preinstall.ps1"
    if (Test-Path $preinstallTemplatePath) {
        $preinstallTemplate = Get-Content $preinstallTemplatePath -Raw
        $preinstallContent = $preinstallTemplate -replace '\{\{VERSION\}\}', $semanticVersion
        $preinstallContent | Set-Content $preinstallPath -Encoding UTF8
        Write-BuildLog "Created preinstall.ps1 script" "INFO"
    }
    
    # Create build-info.yaml
    $buildInfoPath = Join-Path $pkgTempDir "build-info.yaml"
    $buildInfoContent = @"
name: FleetMate
display_name: FleetMate CLI
version: $releaseVersion
description: Enterprise IT asset management and automation CLI
installer_type: copy_from_dmg
installs:
  - path: C:\Program Files\FleetMate
    type: application
"@
    $buildInfoContent | Set-Content $buildInfoPath -Encoding UTF8
    Write-BuildLog "Created build-info.yaml with version $releaseVersion" "INFO"
    
    # Check for cimipkg in PATH or in CimianTools
    $cimipkgPath = $null
    $cimipkgCmd = Get-Command cimipkg.exe -ErrorAction SilentlyContinue
    if ($cimipkgCmd) {
        $cimipkgPath = $cimipkgCmd.Source
    } else {
        # Try to find in CimianTools release directory
        $cimianToolsPath = Join-Path (Split-Path (Split-Path $RootDir)) "packages\CimianTools\release"
        $possiblePaths = @(
            (Join-Path $cimianToolsPath "x64\cimipkg.exe"),
            (Join-Path $cimianToolsPath "arm64\cimipkg.exe")
        )
        foreach ($path in $possiblePaths) {
            if (Test-Path $path) {
                $cimipkgPath = $path
                break
            }
        }
    }
    
    if (-not $cimipkgPath) {
        Write-BuildLog "cimipkg.exe not found in PATH or CimianTools" "ERROR"
        Write-BuildLog "Build cimipkg from packages\CimianTools or add to PATH" "INFO"
        Remove-Item $pkgTempDir -Recurse -Force -ErrorAction SilentlyContinue
        return $null
    }
    
    Write-BuildLog "Using cimipkg: $cimipkgPath" "INFO"
    
    # Build the .pkg package using cimipkg
    try {
        $cimipkgArgs = @("--verbose", $pkgTempDir)
        
        Write-BuildLog "Running cimipkg to create .pkg package..." "INFO"
        $process = Start-Process -FilePath $cimipkgPath -ArgumentList $cimipkgArgs -Wait -NoNewWindow -PassThru
        
        if ($process.ExitCode -eq 0) {
            Write-BuildLog ".pkg package created successfully" "SUCCESS"
            
            # Look for the created .pkg file in the build subdirectory
            $buildDir = Join-Path $pkgTempDir "build"
            if (Test-Path $buildDir) {
                $createdPkgFiles = Get-ChildItem -Path $buildDir -Filter "*.pkg"
                foreach ($pkgFile in $createdPkgFiles) {
                    $pkgSize = $pkgFile.Length
                    Write-BuildLog ".pkg package: $($pkgFile.Name) ($([math]::Round($pkgSize / 1MB, 2)) MB)" "INFO"
                    
                    # Move to release directory with versioned naming
                    $releaseDir = Join-Path $RootDir "release"
                    if (-not (Test-Path $releaseDir)) {
                        New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
                    }
                    $targetPath = Join-Path $releaseDir "FleetMate-$releaseVersion.pkg"
                    Copy-Item $pkgFile.FullName $targetPath -Force
                    Write-BuildLog "Package saved to: $targetPath" "SUCCESS"
                    
                    return $targetPath
                }
            } else {
                Write-BuildLog "Build directory not found after cimipkg execution: $buildDir" "WARNING"
            }
        } else {
            Write-BuildLog "Failed to create .pkg package (exit code: $($process.ExitCode))" "ERROR"
        }
    }
    catch {
        Write-BuildLog "Error creating .pkg package: $_" "ERROR"
    }
    finally {
        # Clean up temporary directory
        Remove-Item $pkgTempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    return $null
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
        & dotnet publish "$RootDir\FleetMate.CLI\FleetMate.CLI.csproj" `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true `
            --output "$RootDir\publish"
        $outputDir = Join-Path $RootDir "publish"
    } else {
        Write-BuildLog "Building FleetMate..."
        & dotnet build "$RootDir\FleetMate.sln" --configuration Release
    }

    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    Write-BuildLog "Build completed" "SUCCESS"

    # Sign
    if ($Sign -and $certInfo) {
        if ($Publish) {
            $exePath = Join-Path $RootDir "publish\fleetmate.exe"
        } else {
            $exePath = Join-Path $RootDir "FleetMate.CLI\bin\Release\net10.0-windows\win-x64\fleetmate.exe"
        }
        
        if (Test-Path $exePath) {
            Invoke-SignArtifact -Path $exePath -Thumbprint $certInfo.Thumbprint -Store $certInfo.Store
        } else {
            Write-BuildLog "Executable not found at $exePath" "WARNING"
        }
    }

    # Summary
    Write-Host "`n=== Build Complete ===" -ForegroundColor Green
    if (Test-Path $outputDir) {
        Get-ChildItem $outputDir -Filter "*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
            $size = [math]::Round($_.Length / 1MB, 1)
            Write-Host "  $($_.Name) ($size MB)"
        }
    }
    if ($Publish) {
        $dllPath = Join-Path $RootDir "publish\fleetmate.dll"
        if (Test-Path $dllPath) {
            $size = [math]::Round((Get-Item $dllPath).Length / 1MB, 1)
            Write-Host "  fleetmate.dll ($size MB)"
        }
    }

    # Create .pkg if requested
    if ($PkgOnly) {
        $version = Get-BuildVersion
        $pkgPath = Build-PkgPackage -Version $version -Sign:$Sign -Thumbprint $certInfo.Thumbprint
        if ($pkgPath) {
            Write-Host "`n=== Package Created ===" -ForegroundColor Green
            Write-Host "  $(Split-Path $pkgPath -Leaf)" -ForegroundColor Cyan
        }
    }

} catch {
    Write-BuildLog "Build failed: $_" "ERROR"
    exit 1
}
