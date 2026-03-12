#Requires -Version 7.0
<#
.SYNOPSIS
    Builds FleetMate (CLI + GUI) with enterprise code signing.

.PARAMETER Sign
    Sign binaries with enterprise certificate (required to run on WDAC-enforced systems)

.PARAMETER Thumbprint
    Override certificate thumbprint (auto-detected from cert store if not specified)

.PARAMETER Clean
    Clean build artifacts before building

.PARAMETER Publish
    Create single-file self-contained executables

.PARAMETER GUI
    Build the GUI project (fleetmate-gui.exe) in addition to the CLI

.PARAMETER GUIOnly
    Build and sign only the GUI project

.PARAMETER CLIOnly
    Build and sign only the CLI project  

.PARAMETER PkgOnly
    Create .pkg package from existing binaries (skip build)

.PARAMETER Launch
    After building and signing, launch the GUI application

.EXAMPLE
    .\build.ps1 -Sign -GUI
    # Build CLI + GUI with auto-detected enterprise cert

.EXAMPLE
    .\build.ps1 -Sign -GUIOnly -Launch
    # Build, sign, and launch just the GUI

.EXAMPLE
    .\build.ps1 -Sign -Thumbprint "ABCDEF..."
    # Build with specific certificate thumbprint
#>

[CmdletBinding()]
param(
    [switch]$Sign,
    [string]$Thumbprint,
    [switch]$Clean,
    [switch]$Publish,
    [switch]$GUI,
    [switch]$GUIOnly,
    [switch]$CLIOnly,
    [switch]$PkgOnly,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$RootDir = $PSScriptRoot

# Enterprise certificate configuration — matches CN used by Cimian build scripts
$EnterpriseCertCN = $env:ENTERPRISE_CERT_CN ?? 'EmilyCarrU Intune Windows Enterprise Certificate'
# Fallback subject substring search
$EnterpriseCertSubject = $env:CIMIAN_CERT_SUBJECT ?? 'EmilyCarrU'

function Write-BuildLog {
    param([string]$Message, [ValidateSet("INFO","WARNING","ERROR","SUCCESS")][string]$Level = "INFO")
    $color = switch ($Level) { "INFO" { "Cyan" } "WARNING" { "Yellow" } "ERROR" { "Red" } "SUCCESS" { "Green" } }
    Write-Host "[$Level] " -NoNewline -ForegroundColor $color
    Write-Host $Message
}

function Get-SigningCertThumbprint {
    param([string]$OverrideThumbprint = $null)

    # Explicit thumbprint from param or environment variable
    $explicit = $OverrideThumbprint
    if (-not $explicit -and $env:CERT_THUMBPRINT) { $explicit = $env:CERT_THUMBPRINT }
    if ($explicit) {
        foreach ($store in @('CurrentUser', 'LocalMachine')) {
            $cert = Get-ChildItem "Cert:\$store\My\$explicit" -ErrorAction SilentlyContinue
            if ($cert -and $cert.HasPrivateKey) {
                return @{ Thumbprint = $explicit; Store = $store }
            }
        }
        throw "Certificate with thumbprint '$explicit' not found in cert stores."
    }

    # Auto-detect by CN (matches Cimian enterprise cert pattern)
    foreach ($store in @('CurrentUser', 'LocalMachine')) {
        $cert = Get-ChildItem "Cert:\$store\My" -ErrorAction SilentlyContinue |
            Where-Object { $_.HasPrivateKey -and ($_.Subject -like "*$EnterpriseCertCN*" -or $_.Subject -like "*$EnterpriseCertSubject*") } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if ($cert) { return @{ Thumbprint = $cert.Thumbprint; Store = $store } }
    }
    return $null
}

function Get-SignToolPath {
    # Check PATH — prefer arm64 or x64 variant
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }

    # Search Windows SDK — prefer arm64 on ARM64 systems, fall back to x64
    $programFilesx86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $searchRoot = Join-Path $programFilesx86 "Windows Kits\10\bin"

    if (Test-Path $searchRoot) {
        # On ARM64, prefer arm64 signtool; on x64, prefer x64
        $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
        $candidates = Get-ChildItem -Path $searchRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object @{ Expression = { if ($_.FullName -match "\\$arch\\") { 0 } else { 1 } }; Ascending = $true },
                        @{ Expression = { $_.Directory.Parent.Name }; Descending = $true }
        if ($candidates.Count -gt 0) { return $candidates[0].FullName }
    }
    return $null
}

function Invoke-SignArtifact {
    param([string]$Path, [string]$Thumbprint, [string]$Store = "CurrentUser", [int]$MaxAttempts = 4)

    if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }

    $signToolExe = Get-SignToolPath
    if (-not $signToolExe) { throw "signtool.exe not found. Install Windows 10/11 SDK with Signing Tools." }

    Write-BuildLog "  signtool: $signToolExe" "INFO"

    # CurrentUser store: just /s My; LocalMachine store: add /sm
    $storeParam = if ($Store -eq 'LocalMachine') { @('/s', 'My', '/sm') } else { @('/s', 'My') }

    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS'
    )

    $attempt = 0
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        foreach ($tsa in $tsas) {
            $signArgs = @('sign') + $storeParam + @(
                '/sha1', $Thumbprint,
                '/fd', 'SHA256',
                '/td', 'SHA256',
                '/tr', $tsa,
                '/v',
                $Path
            )
            Write-BuildLog "Signing [$tsa] (attempt $attempt): $(Split-Path $Path -Leaf)"
            & $signToolExe @signArgs
            if ($LASTEXITCODE -eq 0) {
                Write-BuildLog "Signed: $(Split-Path $Path -Leaf)" "SUCCESS"
                return
            }
            Write-BuildLog "Attempt $attempt failed (exit $LASTEXITCODE)" "WARNING"
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
    throw "Signing failed after $MaxAttempts attempts: $Path"
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

    # Determine what to build
    $buildCLI = -not $GUIOnly
    $buildGUI  = $GUI -or $GUIOnly

    # Default with no flags: build + sign everything (CLI + GUI)
    if (-not $buildCLI -and -not $buildGUI) {
        $buildCLI = $true
        $buildGUI = $true
    }
    if (-not $Sign -and -not $PkgOnly) {
        Write-BuildLog "No -Sign flag specified — defaulting to signed build" "INFO"
        $Sign = $true
    }

    # Clean if requested
    if ($Clean) {
        Write-BuildLog "Cleaning build artifacts..."
        foreach ($proj in @('FleetMate.CLI', 'FleetMate.GUI', 'FleetMate.Core')) {
            Remove-Item -Path "$RootDir\$proj\bin" -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path "$RootDir\$proj\obj" -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Resolve signing cert
    $certInfo = $null
    if ($Sign) {
        $certInfo = Get-SigningCertThumbprint -OverrideThumbprint $Thumbprint
        if (-not $certInfo) {
            throw "No enterprise signing certificate found. Install the enterprise cert or pass -Thumbprint."
        }
        Write-BuildLog "Signing with certificate: $($certInfo.Thumbprint) (Store: $($certInfo.Store))" "SUCCESS"
    }

    # --- Build CLI ---
    if ($buildCLI -and -not $PkgOnly) {
        Write-BuildLog "Building FleetMate CLI..."
        if ($Publish) {
            & dotnet publish "$RootDir\FleetMate.CLI\FleetMate.CLI.csproj" `
                --configuration Release `
                --runtime win-arm64 `
                --self-contained true `
                -p:PublishSingleFile=true `
                -p:EnableCompressionInSingleFile=true `
                --output "$RootDir\publish\cli"
        } else {
            foreach ($rid in @('win-x64', 'win-arm64')) {
                & dotnet build "$RootDir\FleetMate.CLI\FleetMate.CLI.csproj" --configuration Release -r $rid
                if ($LASTEXITCODE -ne 0) { throw "CLI build failed ($rid)" }
            }
        }
        Write-BuildLog "CLI build completed" "SUCCESS"

        if ($Sign -and $certInfo) {
            if ($Publish) {
                $cliExe = "$RootDir\publish\cli\fleetmate.exe"
                if (Test-Path $cliExe) {
                    Invoke-SignArtifact -Path $cliExe -Thumbprint $certInfo.Thumbprint -Store $certInfo.Store
                } else {
                    Write-BuildLog "CLI exe not found at: $cliExe" "WARNING"
                }
            } else {
                foreach ($rid in @('win-x64', 'win-arm64')) {
                    $cliExe = "$RootDir\FleetMate.CLI\bin\Release\net10.0-windows\$rid\fleetmate.exe"
                    if (Test-Path $cliExe) {
                        Invoke-SignArtifact -Path $cliExe -Thumbprint $certInfo.Thumbprint -Store $certInfo.Store
                    } else {
                        Write-BuildLog "CLI exe not found at: $cliExe" "WARNING"
                    }
                }
            }
        }
    }

    # --- Build GUI ---
    if ($buildGUI -and -not $PkgOnly) {
        Write-BuildLog "Building FleetMate GUI..."

        # GUI must be published as self-contained single-file for WDAC-compliant signing.
        # A regular dotnet build produces a dotnet host stub that WDAC blocks signtool from modifying.
        $guiOutDir = "$RootDir\publish\gui"

        & dotnet publish "$RootDir\FleetMate.GUI\FleetMate.GUI.csproj" `
            --configuration Release `
            --runtime win-arm64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            --output $guiOutDir

        if ($LASTEXITCODE -ne 0) { throw "GUI build failed" }
        Write-BuildLog "GUI build completed" "SUCCESS"

        if ($Sign -and $certInfo) {
            $guiExe = Join-Path $guiOutDir "fleetmate-gui.exe"
            if (Test-Path $guiExe) {
                Invoke-SignArtifact -Path $guiExe -Thumbprint $certInfo.Thumbprint -Store $certInfo.Store
            } else {
                Write-BuildLog "GUI exe not found at: $guiExe — skipping sign" "WARNING"
            }
        }
    }

    # --- Summary ---
    Write-Host "`n=== Build Complete ===" -ForegroundColor Green

    # --- Launch GUI after build ---
    if ($Launch) {
        if (-not $buildGUI) {
            Write-BuildLog "Cannot launch: GUI was not built (add -GUI or -GUIOnly)" "WARNING"
        } else {
            $guiExe = "$RootDir\publish\gui\fleetmate-gui.exe"
            if (Test-Path $guiExe) {
                Write-BuildLog "Launching: $guiExe" "INFO"
                Start-Process $guiExe
            } else {
                Write-BuildLog "GUI exe not found: $guiExe" "ERROR"
            }
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
