# FleetMate

> Enterprise IT fleet orchestration, asset management, and deployment monitoring CLI

FleetMate is a unified command-line interface for managing IT assets across multiple systems including Snipe-IT, TeamDynamix, Microsoft Intune/Entra, ReportMate, and Azure DevOps. Built for Windows with .NET 10, it provides a consistent interface for fleet management tasks.

## Features

- **Fleet Monitoring** - Real-time device status and error tracking via ReportMate
- **Asset Management** - Complete Snipe-IT integration (assets, users, locations, checkout/checkin)
- **Ticketing** - TeamDynamix ticket and asset management
- **Identity & Device** - Microsoft Entra ID and Intune integration
- **Remote Execution** - SSH-based remote command execution
- **Rich Output** - Beautiful tables and JSON export support
- **Flexible Config** - Environment variables, registry, YAML, or .env files

## Quick Start

### Installation

Download the latest release `.pkg` file and install via sbin-installer, or build from source:

```powershell
git clone https://github.com/emilycarru-its-infra/fleetmate-windows.git
cd fleetmate-windows
.\build.ps1 -Publish -Sign -PkgOnly
```

### Configuration

FleetMate uses a priority-based configuration system:

1. **Environment Variables** (highest priority)
2. **Windows Registry** (`HKCU\SOFTWARE\FleetMate`)
3. **.env file** (in executable directory or parent)
4. **config.yaml** (in executable directory or `%LOCALAPPDATA%\FleetMate\`)

**Quick Setup:**
```powershell
# Run interactive configuration wizard
fleetmate configure

# Or set environment variables
$env:SNIPE_URL = "https://snipe.example.com"
$env:SNIPE_API_KEY = "your-api-key"
$env:REPORTMATE_URL = "https://reportmate.example.com"
$env:REPORTMATE_PASSPHRASE = "your-passphrase"
```

### Verify Installation

```powershell
fleetmate status
```

This displays your configuration, available services, and connection status.

## Command Reference

### Fleet Monitoring

Monitor deployment health and troubleshoot installation failures:

```powershell
# Look up device by serial, hostname, or asset tag
fleetmate device L003461

# List all installation errors
fleetmate errors

# List errors for specific package
fleetmate errors --item "Adobe Creative Cloud"

# Deep-dive troubleshooting for specific package
fleetmate troubleshoot "Adobe Creative Cloud"
```

### Asset Management (Snipe-IT)

Comprehensive Snipe-IT integration with 15+ subcommands:

```powershell
# Search assets
fleetmate snipe assets --search "laptop"
fleetmate snipe assets --status 2 --location 5

# Get asset details (by tag, serial, or ID)
fleetmate snipe asset L003461

# Asset lifecycle
fleetmate snipe checkout 923 --user 42 --note "Assigned to new hire"
fleetmate snipe checkin 923 --note "Returned from user"
fleetmate snipe audit 923 --location 5

# Users and locations
fleetmate snipe users --search "bryan"
fleetmate snipe user 42
fleetmate snipe locations

# Asset metadata
fleetmate snipe models
fleetmate snipe categories
fleetmate snipe manufacturers
fleetmate snipe statuses

# Licenses and inventory
fleetmate snipe licenses --search "Adobe"
fleetmate snipe accessories
fleetmate snipe consumables
fleetmate snipe components

# Activity log
fleetmate snipe activity --limit 50
```

### TeamDynamix

Asset and ticket management:

```powershell
# Search assets (returns full field data via JsonExtensionData)
fleetmate tdx assets --search L003461 --limit 10

# Get asset details with all fields
fleetmate tdx asset 243576 --json

# Ticket management
fleetmate tdx tickets --status "New" --priority "High"
fleetmate tdx ticket 12345
fleetmate tdx create "Laptop not booting" --description "User reports black screen"
fleetmate tdx comment 12345 "Troubleshooting steps taken..."
```

### Remote Execution (SecureShell)

SSH-based remote command execution with automatic host key management:

```powershell
# Execute single command
fleetmate ssh exec L003461 "hostname"
fleetmate ssh exec REMOTE-24 "Get-Service Cimian"

# Batch execution
fleetmate ssh batch "L003461,REMOTE-24,STUDIO-10" "uptime"

# Test connectivity
fleetmate ssh test L003461

# Retrieve Cimian logs
fleetmate ssh logs L003461

# Host key management (for reimaged devices)
fleetmate ssh host-key clean 10.15.26.123     # Remove stale key for single host
fleetmate ssh host-key clean-all 10.15.26.    # Clean all hosts in subnet (with confirmation)
fleetmate ssh host-key clean-all 10.15. -n    # Dry run to preview what would be removed
```

**Auto Host Key Recovery**: When connecting to a reimaged device, FleetMate automatically detects the "host key verification failed" error, removes the stale host key from `known_hosts`, and retries the connection. This is enabled by default and can be disabled in configuration.

### Microsoft Graph (Intune/Entra)

Query Intune devices and Entra users:

```powershell
# Intune devices
fleetmate intune devices
fleetmate intune device 16BQKQ3  # by serial
fleetmate intune compliance <device-id>

# Entra users
fleetmate entra user jane.doe@example.com
fleetmate entra groups
fleetmate entra check-group jane.doe@example.com "IT Staff"
```

### Azure DevOps

Work item management:

```powershell
# List work items
fleetmate devops items --state Active
fleetmate devops items --assigned-to "me"

# Work item details
fleetmate devops item 1234

# Create work items
fleetmate devops create "Fix deployment error" --type Task
fleetmate devops from-error "Adobe Creative Cloud"  # auto-create from error

# Update work items
fleetmate devops update 1234 --state Resolved --comment "Fixed by script update"
```

### Quality Assurance

Package validation and testing:

```powershell
# Validate package structure
fleetmate validate /path/to/package

# Lint pkginfo files
fleetmate lint /path/to/pkginfo

# Run quality tests
fleetmate test --suite smoke
fleetmate qa run --environment staging
```

## JSON Output

All commands support `--json` for programmatic consumption:

```powershell
fleetmate snipe asset L003461 --json | ConvertFrom-Json
fleetmate tdx assets --search L003461 --json | ConvertFrom-Json
fleetmate intune device 16BQKQ3 --json | ConvertFrom-Json
```

## System Check Demo

FleetMate includes a comprehensive system check script that tests all integrations:

```powershell
# Run full system check for an asset
powershell -File "C:\path\to\fleetmate\scripts\system-check.ps1" -AssetTag L003461
```

This generates a timestamped log showing:
- ReportMate device lookup
- Snipe-IT asset details
- Entra user lookup (if assigned)
- Intune device information (if serial available)
- TeamDynamix asset summary
- SecureShell command execution

**Sample Output:**
```
FleetMate system check for asset tag L003461
Timestamp: 2026-01-18 23:41:26Z

=== ReportMate: device lookup ===
Remote Compute 24 (Serial: 16BQKQ3)

=== Snipe: asset detail ===
+-------------------- Asset Details --------------------+
ID         : 923
Asset Tag  : L003461
Name       : Remote Compute 24
Serial     : 16BQKQ3
Model      : Dell Precision 3660 Tower
Status     : Ready to Deploy
Location   : IT Storage
...

=== TDX Asset ===
+-------------------- TDX Asset --------------------+
Name        : Remote Compute 24
Asset Tag   : L003461
Serial      : 16BQKQ3
Model       : Precision 3660 Tower
Manufacturer: Dell
Status      : In Use
...

Log saved to: quality\fleetmate\logs\system-check-L003461-20260118-234126.log
```

## Build & Package

### Build from Source

```powershell
# Standard build
.\build.ps1

# Publish self-contained executable
.\build.ps1 -Publish

# Build and create .pkg package
.\build.ps1 -Publish -Sign -PkgOnly

# Clean build
.\build.ps1 -Clean -Publish
```

### Output

- **Executable:** `publish\fleetmate.exe` (41.4 MB single-file)
- **Package:** `release\FleetMate-YYYY.MM.DD.HHMM.pkg` (42 MB)
- **Logs:** `quality\fleetmate\logs\`

## Configuration Reference

### Environment Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `REPORTMATE_URL` | ReportMate API endpoint | `https://reportmate.example.com` |
| `REPORTMATE_PASSPHRASE` | ReportMate authentication token | `your-token` |
| `SNIPE_URL` | Snipe-IT base URL | `https://snipe.example.com` |
| `SNIPE_API_KEY` | Snipe-IT API key | `your-api-key` |
| `TDX_BASE_URL` | TeamDynamix API endpoint | `https://tdx.example.com/TDWebApi` |
| `TDX_APP_ID` | TeamDynamix application ID | `123` |
| `TDX_BEID` | TeamDynamix BEID | `your-beid` |
| `TDX_WEB_SERVICES_KEY` | TeamDynamix web services key | `your-key` |
| `SECURE_SHELL_PRIVATE_KEY` | SSH private key (base64 or PEM) | `-----BEGIN RSA PRIVATE KEY-----...` |
| `SECURE_SHELL_USERNAME` | SSH username | `administrator` |

### Windows Registry

Registry keys under `HKEY_CURRENT_USER\SOFTWARE\FleetMate`:

- `TdxBaseUrl` (string)
- `TdxAppId` (string)
- `TdxBeid` / `TdxWebServicesKey` (string)
- `SnipeUrl` / `SnipeApiKey` (string)
- `ReportMateUrl` / `ReportMatePassphrase` (string)

### Config File (config.yaml)

```yaml
reportmate:
  url: https://reportmate.example.com
  passphrase: your-passphrase

snipe:
  url: https://snipe.example.com
  api_key: your-api-key

tdx:
  base_url: https://tdx.example.com/TDWebApi
  app_id: 123
  beid: your-beid
  web_services_key: your-key

secure_shell:
  username: administrator
  private_key_path: C:\keys\id_rsa

logging:
  path: C:\Logs\FleetMate
  level: Information
```

## Architecture

```
FleetMate/
├── FleetMate.CLI/           # Command-line interface
│   ├── Commands/            # Command implementations
│   │   ├── StatusCommand.cs
│   │   ├── DeviceCommand.cs
│   │   ├── SnipeCommand.cs
│   │   ├── TdxCommand.cs
│   │   ├── IntuneCommand.cs
│   │   ├── EntraCommand.cs
│   │   ├── SshCommand.cs
│   │   └── ...
│   └── Program.cs           # Entry point
├── FleetMate.Core/          # Core business logic
│   ├── Services/            # API clients
│   │   ├── ReportMateService.cs
│   │   ├── SnipeService.cs
│   │   ├── TdxService.cs
│   │   ├── GraphService.cs
│   │   ├── SecureShellService.cs
│   │   └── ...
│   ├── Models/              # Data models
│   │   ├── Snipe/
│   │   ├── Tdx/
│   │   ├── Graph/
│   │   └── ...
│   └── Configuration/       # Config loading
└── FleetMate.GUI/           # Future WPF UI
```

### Tech Stack

- **.NET 10** - Modern cross-platform framework
- **System.CommandLine** - Robust CLI framework
- **Spectre.Console** - Rich terminal UI
- **Serilog** - Structured logging
- **Renci.SshNet** - SSH client
- **YamlDotNet** - YAML configuration
- **System.Text.Json** - High-performance JSON

## Troubleshooting

### TDX Authentication Errors

If you see error 487 "unregistered host name":

```powershell
# Check registry values
Get-ItemProperty HKCU:\SOFTWARE\FleetMate

# Update if incorrect
Set-ItemProperty HKCU:\SOFTWARE\FleetMate -Name TdxBaseUrl -Value "https://tdx.example.com/TDWebApi"
Set-ItemProperty HKCU:\SOFTWARE\FleetMate -Name TdxAppId -Value "116"
```

### SSH Connection Failures

```powershell
# Test connectivity
fleetmate ssh test L003461

# Verify key permissions and format
# Key should be PEM format, no passphrase
# Set via environment variable or config
```

### Missing Configuration

```powershell
# Run status to see what's configured
fleetmate status

# Use interactive wizard
fleetmate configure

# Or manually set environment variables
$env:SNIPE_URL = "https://snipe.example.com"
$env:SNIPE_API_KEY = "your-key"
```

## Development

### Prerequisites

- .NET 10 SDK
- Windows 10/11
- PowerShell 7+
- Visual Studio 2022 or VS Code

### Build & Test

```powershell
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run
dotnet run --project FleetMate.CLI -- status

# Publish
dotnet publish FleetMate.CLI --configuration Release --runtime win-x64 --self-contained
```

### Testing

```powershell
# Run system check
.\scripts\system-check.ps1 -AssetTag L003461

# Manual command testing
dotnet run --project FleetMate.CLI -- device L003461
dotnet run --project FleetMate.CLI -- snipe asset L003461
dotnet run --project FleetMate.CLI -- tdx assets --search L003461
```

## License

Proprietary - Emily Carr University of Art + Design

## Support

For issues or questions:
- File an issue on GitHub
- Contact IT Systems team
- Check logs in `quality\fleetmate\logs\`
