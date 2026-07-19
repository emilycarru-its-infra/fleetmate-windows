<#
.SYNOPSIS
    FleetMate Windows - Secure Secrets Setup

.DESCRIPTION
    This script retrieves secrets from Azure Key Vault and stores them securely
    in Windows Registry (HKCU:\SOFTWARE\FleetMate) - NOT in plain text files.
    
    Can be run manually: .\scripts\setup-secrets.ps1 [-Force]
    
    Prerequisites:
      - Azure CLI installed (winget install Microsoft.AzureCLI)
      - Access to the Azure subscription and Key Vault
      - Membership in the DevOps resources owners group (or Key Vault RBAC)
    
    Security:
      - Secrets are stored in Windows Registry, not plain text files
      - Uses Azure CLI SSO for authentication
      - Config file only contains non-sensitive settings

.PARAMETER Force
    Force refresh of secrets even if already configured

.EXAMPLE
    .\setup-secrets.ps1
    .\setup-secrets.ps1 -Force
#>

[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Windows Registry path for FleetMate credentials
$RegistryPath = "HKCU:\SOFTWARE\FleetMate"

# Azure Key Vault Configuration
$KeyVaultName = "your-keyvault"
$CimianKeyVaultName = "your-cimian-keyvault"
$SubscriptionId = "00000000-0000-0000-0000-000000000000"
$TenantId = "00000000-0000-0000-0000-000000000000"

# Config file (non-sensitive settings only)
$ConfigDir = Join-Path $env:USERPROFILE ".fleetmate"
$ConfigFile = Join-Path $ConfigDir "config.yaml"

# Check if already configured (unless forced)
if (-not $Force) {
    if (Test-Path $RegistryPath) {
        $existingKey = Get-ItemProperty -Path $RegistryPath -Name "SnipeApiKey" -ErrorAction SilentlyContinue
        if ($existingKey) {
            Write-Host "FleetMate secrets already configured in Registry."
            Write-Host "Run with -Force to refresh: .\setup-secrets.ps1 -Force"
            exit 0
        }
    }
}

Write-Host ""
Write-Host "======================================================================"
Write-Host "  FleetMate Windows - Secure Secrets Setup"
Write-Host "======================================================================"
Write-Host ""
Write-Host "This script will:"
Write-Host "  1. Authenticate to Azure using SSO"
Write-Host "  2. Fetch secrets from Azure Key Vault"
Write-Host "  3. Store them securely in Windows Registry"
Write-Host "  4. Create a config file for non-sensitive settings"
Write-Host ""

# Check if Azure CLI is installed
$azPath = Get-Command az -ErrorAction SilentlyContinue
if (-not $azPath) {
    Write-Host "X Error: Azure CLI is not installed." -ForegroundColor Red
    Write-Host ""
    Write-Host "Install it with: winget install Microsoft.AzureCLI"
    Write-Host "Then run: .\setup-secrets.ps1"
    exit 1
}

# Check current login status and login if needed
Write-Host "-> Checking Azure CLI login status..."
$accountInfo = az account show 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
if (-not $accountInfo) {
    Write-Host "   Not logged in. Initiating SSO login..."
    az login --tenant $TenantId
    if ($LASTEXITCODE -ne 0) {
        Write-Host "X Login failed." -ForegroundColor Red
        exit 1
    }
} else {
    if ($accountInfo.tenantId -ne $TenantId) {
        Write-Host "   Logged into different tenant. Switching..."
        az login --tenant $TenantId
        if ($LASTEXITCODE -ne 0) {
            Write-Host "X Login failed." -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "   OK Already logged in to correct tenant." -ForegroundColor Green
    }
}

# Set the subscription
az account set --subscription $SubscriptionId 2>$null
$subscriptionName = (az account show --query "name" -o tsv)
Write-Host "   OK Subscription: $subscriptionName" -ForegroundColor Green
Write-Host ""

# Function to get secret from Key Vault
function Get-KeyVaultSecret {
    param(
        [string]$VaultName,
        [string]$SecretName
    )
    $secret = az keyvault secret show --vault-name $VaultName --name $SecretName --query "value" -o tsv 2>$null
    return $secret
}

# Function to store secret in Registry
function Set-RegistrySecret {
    param(
        [string]$Name,
        [string]$Value
    )
    
    if ([string]::IsNullOrEmpty($Value)) {
        return $false
    }
    
    # Create registry key if it doesn't exist
    if (-not (Test-Path $RegistryPath)) {
        New-Item -Path $RegistryPath -Force | Out-Null
    }
    
    Set-ItemProperty -Path $RegistryPath -Name $Name -Value $Value
    return $true
}

Write-Host "-> Fetching secrets from Azure Key Vault..."
Write-Host ""

# Fetch secrets from Inventory Key Vault
Write-Host "   From Key Vault: $KeyVaultName"
Write-Host "   |- SnipeApiUrl..."
$SnipeApiUrl = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "SnipeApiUrl"
Write-Host "   |- SnipeApiKey..."
$SnipeApiKey = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "SnipeApiKey"
Write-Host "   |- TdxUsername..."
$TdxUsername = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "TdxUsername"
Write-Host "   |- TdxPassword..."
$TdxPassword = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "TdxPassword"
Write-Host "   |- TdxBeid..."
$TdxBeid = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "TdxBeid"
Write-Host "   |- TdxBeidSecret..."
$TdxWebServicesKey = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "TdxBeidSecret"
Write-Host "   |- DevicesGraphId..."
$GraphClientId = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "DevicesGraphId"
Write-Host "   |- DevicesGraphSecret..."
$GraphClientSecret = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "DevicesGraphSecret"
Write-Host "   +- AzureTenantId..."
$GraphTenantId = Get-KeyVaultSecret -VaultName $KeyVaultName -SecretName "AzureTenantId"

# Fetch secrets from Cimian Key Vault
Write-Host ""
Write-Host "   From Key Vault: $CimianKeyVaultName"
Write-Host "   |- ReportMateApiUrl..."
$ReportMateUrl = Get-KeyVaultSecret -VaultName $CimianKeyVaultName -SecretName "ReportMateApiUrl"
Write-Host "   |- ReportMatePassphrase..."
$ReportMatePassphrase = Get-KeyVaultSecret -VaultName $CimianKeyVaultName -SecretName "ReportMatePassphrase"
Write-Host "   |- AzureDevOpsOrganization..."
$DevOpsOrg = Get-KeyVaultSecret -VaultName $CimianKeyVaultName -SecretName "AzureDevOpsOrganization"
Write-Host "   +- AzureDevOpsProject..."
$DevOpsProject = Get-KeyVaultSecret -VaultName $CimianKeyVaultName -SecretName "AzureDevOpsProject"

Write-Host ""

# Set defaults for missing values
if ([string]::IsNullOrEmpty($ReportMateUrl)) { 
    $ReportMateUrl = "https://reportmate.example.com" 
}
if ([string]::IsNullOrEmpty($GraphTenantId)) { $GraphTenantId = $TenantId }
if ([string]::IsNullOrEmpty($DevOpsOrg)) { $DevOpsOrg = "your-org" }
if ([string]::IsNullOrEmpty($DevOpsProject)) { $DevOpsProject = "DevOps" }

# Validate required secrets
$MissingSecrets = @()
if ([string]::IsNullOrEmpty($SnipeApiKey)) { $MissingSecrets += "SnipeApiKey" }
if ([string]::IsNullOrEmpty($GraphClientId)) { $MissingSecrets += "DevicesGraphId" }
if ([string]::IsNullOrEmpty($GraphClientSecret)) { $MissingSecrets += "DevicesGraphSecret" }

if ($MissingSecrets.Count -gt 0) {
    Write-Host "!! Warning: The following secrets are missing from Key Vault:" -ForegroundColor Yellow
    foreach ($secret in $MissingSecrets) {
        Write-Host "      - $secret" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "   Some features may not work without these secrets."
    Write-Host ""
}

Write-Host "-> Storing secrets in Windows Registry..."
Write-Host ""

# Store secrets in Registry
$Stored = 0
$Failed = 0

function Store-Secret {
    param(
        [string]$Name,
        [string]$Value
    )
    
    if (-not [string]::IsNullOrEmpty($Value)) {
        if (Set-RegistrySecret -Name $Name -Value $Value) {
            Write-Host "   OK $Name" -ForegroundColor Green
            $script:Stored++
        } else {
            Write-Host "   X $Name (failed)" -ForegroundColor Red
            $script:Failed++
        }
    } else {
        Write-Host "   - $Name (skipped - empty)" -ForegroundColor DarkGray
    }
}

Store-Secret -Name "SnipeUrl" -Value $SnipeApiUrl
Store-Secret -Name "SnipeApiKey" -Value $SnipeApiKey
Store-Secret -Name "GraphTenantId" -Value $GraphTenantId
Store-Secret -Name "GraphClientId" -Value $GraphClientId
Store-Secret -Name "GraphClientSecret" -Value $GraphClientSecret
Store-Secret -Name "TdxBaseUrl" -Value "https://tdx.example.com/TDWebApi"
Store-Secret -Name "TdxAppId" -Value "123"
Store-Secret -Name "TdxUsername" -Value $TdxUsername
Store-Secret -Name "TdxPassword" -Value $TdxPassword
Store-Secret -Name "TdxBeid" -Value $TdxBeid
Store-Secret -Name "TdxWebServicesKey" -Value $TdxWebServicesKey
Store-Secret -Name "ReportMateUrl" -Value $ReportMateUrl
Store-Secret -Name "ReportMatePassphrase" -Value $ReportMatePassphrase
Store-Secret -Name "DevOpsOrganization" -Value $DevOpsOrg
Store-Secret -Name "DevOpsProject" -Value $DevOpsProject

Write-Host ""

# Create config directory
if (-not (Test-Path $ConfigDir)) {
    New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
}

# Generate config.yaml with NON-SENSITIVE settings only
Write-Host "-> Creating config file (non-sensitive settings only)..."
@"
# FleetMate Configuration
# ========================
# Sensitive credentials are stored in Windows Registry, not here.
# Run 'scripts\setup-secrets.ps1 -Force' to refresh secrets from Azure Key Vault.

# Graph API (Microsoft Intune/Entra)
# Credentials: Registry -> HKCU:\SOFTWARE\FleetMate -> GraphClientId, GraphClientSecret, GraphTenantId
graph:
  use_azure_cli_auth: true  # Prefer Azure CLI SSO over client credentials

# Snipe-IT Asset Management
# Credentials: Registry -> HKCU:\SOFTWARE\FleetMate -> SnipeApiKey, SnipeUrl
snipe:
  enabled: true

# TeamDynamix (TDX) Ticketing
# Credentials: Registry -> HKCU:\SOFTWARE\FleetMate -> TdxUsername, TdxPassword, TdxBeid, TdxWebServicesKey
tdx:
  base_url: https://tdx.example.com/TDWebApi
  app_id: 123
  enabled: true

# Azure DevOps (Work Items)
# Uses Azure CLI SSO for authentication
devops:
  enabled: true
  use_azure_cli_auth: true

# ReportMate API
# Credentials: Registry -> HKCU:\SOFTWARE\FleetMate -> ReportMateUrl, ReportMatePassphrase
reportmate:
  enabled: true

# Secure Shell (SSH)
secure_shell:
  private_key_path: ~/.ssh/id_rsa
  default_username: winadmins
  connection_timeout_seconds: 30
  command_timeout_seconds: 120
  max_concurrent_connections: 10
  port: 22

# Task Provider Settings
tasks:
  default_provider: azdevops
  providers:
    azdevops:
      enabled: true
    github:
      enabled: false
    gitea:
      enabled: false

# Logging
log_level: info
"@ | Set-Content -Path $ConfigFile -Encoding UTF8

Write-Host "   OK Created: $ConfigFile" -ForegroundColor Green
Write-Host ""

# Create a helper script to view configured secrets
$HelperScript = Join-Path $ConfigDir "check-secrets.ps1"
@'
# Check which FleetMate secrets are configured in Registry

$RegistryPath = "HKCU:\SOFTWARE\FleetMate"

Write-Host ""
Write-Host "FleetMate Registry Secrets Status"
Write-Host "=================================="
Write-Host ""

function Check-Secret {
    param([string]$Name)
    
    try {
        $value = Get-ItemProperty -Path $RegistryPath -Name $Name -ErrorAction SilentlyContinue
        if ($value.$Name) {
            Write-Host "  OK $Name" -ForegroundColor Green
        } else {
            Write-Host "  X $Name (not set)" -ForegroundColor Red
        }
    } catch {
        Write-Host "  X $Name (not set)" -ForegroundColor Red
    }
}

if (-not (Test-Path $RegistryPath)) {
    Write-Host "  Registry key not found: $RegistryPath" -ForegroundColor Red
    Write-Host ""
    Write-Host "Run scripts\setup-secrets.ps1 to configure secrets."
    exit 1
}

Check-Secret "SnipeUrl"
Check-Secret "SnipeApiKey"
Check-Secret "GraphTenantId"
Check-Secret "GraphClientId"
Check-Secret "GraphClientSecret"
Check-Secret "TdxBaseUrl"
Check-Secret "TdxAppId"
Check-Secret "TdxUsername"
Check-Secret "TdxPassword"
Check-Secret "TdxBeid"
Check-Secret "TdxWebServicesKey"
Check-Secret "ReportMateUrl"
Check-Secret "ReportMatePassphrase"
Check-Secret "DevOpsOrganization"
Check-Secret "DevOpsProject"

Write-Host ""
Write-Host "Secrets are stored in: $RegistryPath"
Write-Host "To refresh: scripts\setup-secrets.ps1 -Force"
Write-Host ""
'@ | Set-Content -Path $HelperScript -Encoding UTF8

Write-Host "======================================================================"
Write-Host "  OK Setup Complete!" -ForegroundColor Green
Write-Host "======================================================================"
Write-Host ""
Write-Host "Stored $Stored secrets in Windows Registry"
if ($Failed -gt 0) {
    Write-Host "Failed to store $Failed secrets" -ForegroundColor Red
}
Write-Host ""
Write-Host "Files created:"
Write-Host "  - $ConfigFile (non-sensitive settings)"
Write-Host "  - $HelperScript (check secret status)"
Write-Host ""
Write-Host "Secrets are stored securely in:"
Write-Host "  Registry: $RegistryPath"
Write-Host ""
Write-Host "To refresh secrets from Azure Key Vault:"
Write-Host "  .\scripts\setup-secrets.ps1 -Force"
Write-Host ""
Write-Host "To check which secrets are configured:"
Write-Host "  & $HelperScript"
Write-Host ""
