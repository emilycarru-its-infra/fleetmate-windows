# FleetMate Implementation Plan

This document tracks the implementation progress and roadmap for FleetMate, a cross-platform fleet management CLI tool.

---

## Platform Scope

| Platform | Language | Repository | Status |
|----------|----------|------------|--------|
| Windows | C# (.NET 10) | This repo (`fleetmate/`) | ~80% complete |
| macOS | Swift | TBD (separate repo) | Not started |

---

## Architecture Overview

```
fleetmate/
├── FleetMate.CLI/      # CLI command implementations
│   ├── Commands/       # System.CommandLine command handlers
│   └── Program.cs      # Entry point and service initialization
├── FleetMate.Core/     # Core business logic
│   ├── Services/       # API client services (HTTP, SSH)
│   ├── Models/         # Data models for APIs
│   └── Configuration/  # Config loading (YAML + env + registry)
├── FleetMate.GUI/      # WPF desktop UI (future)
├── build.ps1           # Build and packaging automation
├── sign.ps1            # Code signing automation
└── config.sample.yaml  # Configuration template
```

### Tech Stack (Windows/C#)
- **CLI Framework:** System.CommandLine v2.0
- **Configuration:** YamlDotNet
- **Logging:** Serilog (file rolling)
- **Output:** Spectre.Console (rich tables, colors)
- **SSH:** Renci.SshNet
- **HTTP:** System.Net.Http.Json

---

## Current Implementation Status

### Services (9 total)

| Service | Status | Description |
|---------|--------|-------------|
| ReportMateService | ✅ COMPLETE | Fleet monitoring API (devices, errors, troubleshooting) |
| SnipeService | ✅ COMPLETE | Snipe-IT asset management (full CRUD - assets, users, locations, models, categories, licenses, etc.) |
| TdxService | ✅ COMPLETE | TeamDynamix assets and tickets (search with full field capture via JsonExtensionData) |
| GraphService | ✅ COMPLETE | Microsoft Graph - Intune devices, Entra users/groups |
| SecureShellService | ✅ COMPLETE | SSH remote execution with key-based auth |
| AzureDevOpsService | ✅ COMPLETE | ADO work items, sprints, boards |
| PkgInfoService | ✅ COMPLETE | Package metadata parsing and validation |
| QaService | ✅ COMPLETE | Quality assurance test execution |
| CimianService | ✅ COMPLETE | Internal Cimian utilities |

### Commands (15 total)

| Command | Status | Description |
|---------|--------|-------------|
| `status` | ✅ COMPLETE | Show config, paths, and service status with FigletText banner |
| `device` | ✅ COMPLETE | ReportMate device lookup by serial/hostname/asset tag |
| `errors` | ✅ COMPLETE | List fleet installation failures from ReportMate |
| `troubleshoot` | ✅ COMPLETE | Diagnose specific installation issues |
| `snipe` | ✅ COMPLETE | Snipe-IT asset management (15+ subcommands: assets, asset, users, user, locations, models, categories, statuses, manufacturers, licenses, accessories, consumables, components, activity, checkout, checkin, audit) |
| `ssh` | ✅ COMPLETE | SecureShell remote execution (exec, batch, test, logs) |
| `entra` | ✅ COMPLETE | Microsoft Entra ID queries (users, groups, memberships) |
| `intune` | ✅ COMPLETE | Intune device management (devices, device, compliance) |
| `tdx` | ✅ COMPLETE | TeamDynamix integration (assets search with full field capture, asset detail, tickets, ticket, create, comment) |
| `devops` | ✅ COMPLETE | Azure DevOps work items (items, item, create, update, from-error) |
| `configure` | ✅ COMPLETE | Interactive configuration wizard |
| `validate` | ✅ COMPLETE | Package structure validation |
| `lint` | ⚠️ PARTIAL | Pkginfo linting (--fix not implemented) |
| `test` | ✅ COMPLETE | Quality test execution |
| `qa` | ✅ COMPLETE | Quality assurance workflows |

---

## Current Status

### ✅ Completed
- [x] All core services implemented and functional
- [x] Full Snipe-IT integration with 15+ subcommands
- [x] TeamDynamix integration with complete field capture (JsonExtensionData)
- [x] Microsoft Graph integration (Intune + Entra)
- [x] SecureShell remote execution
- [x] ReportMate fleet monitoring
- [x] Azure DevOps work item management
- [x] System-check integration testing script
- [x] .pkg packaging with cimipkg
- [x] Build automation (build.ps1)
- [x] Zero build warnings (nullable references resolved)
- [x] Configuration via YAML + environment variables + Windows registry
- [x] JSON output support across all commands
- [x] Rich console output with Spectre.Console

### 🚧 In Progress / Remaining
- [ ] Complete `lint --fix` auto-corrections
- [ ] Add code signing to build.ps1 (infrastructure exists, needs cert integration)
- [ ] Create MSI installer option (currently .pkg only)
- [ ] Add retry logic for transient API failures
- [ ] Implement circuit breaker pattern for service unavailability
- [ ] Add `report` command for combined exports
- [ ] Add `lookup` command for cross-system asset correlation

### 📋 Testing Status
- [x] TDX authentication and asset search (tested with asset L003461)
- [x] Snipe-IT asset lookup (tested)
- [x] Entra user lookup (tested conditionally)
- [x] Intune device management (tested conditionally)
- [x] SecureShell command execution (tested with hostname command)
- [x] Full system-check script with all integrations (logs to quality/fleetmate/logs/)
- [x] Build and publish pipeline (tested successfully)
- [x] .pkg package creation (tested, 42MB output)
- [ ] Comprehensive unit tests
- [ ] Automated integration test suite
- [ ] Load testing for batch operations

---

## macOS Swift Implementation

### Architecture (Mirror C#)

```
FleetMate-Swift/
├── Package.swift
├── Sources/
│   └── FleetMate/
│       ├── Commands/        # ArgumentParser commands
│       ├── Services/        # API clients
│       ├── Models/          # Codable structs
│       └── Config/          # Configuration loading
└── Tests/
    └── FleetMateTests/
```

### Swift Dependencies
- **ArgumentParser** - CLI framework
- **Yams** - YAML parsing
- **AsyncHTTPClient** - Networking
- **SwiftNIO SSH** - SSH operations
- **Rainbow** - Terminal colors

### Feature Parity Priority
1. **Core (Week 1-2):** device, errors, troubleshoot, status
2. **Integration (Week 3-4):** snipe, ssh
3. **Extended (Week 5-6):** ado, graph, tdx

---

## Configuration

### Configuration Priority (Windows)
1. **Environment variables** (highest priority)
2. **Windows Registry** (`HKEY_CURRENT_USER\SOFTWARE\FleetMate`)
3. **.env file** (in executable directory or parent directories)
4. **config.yaml** (in executable directory or `%LOCALAPPDATA%\FleetMate\`)

### Environment Variables
| Variable | Description |
|----------|-------------|
| `REPORTMATE_URL` | ReportMate API endpoint |
| `REPORTMATE_PASSPHRASE` | ReportMate auth token |
| `SNIPE_URL` | Snipe-IT base URL |
| `SNIPE_API_KEY` | Snipe-IT API key |
| `SECURE_SHELL_PRIVATE_KEY` | SSH private key content (base64 or PEM) |
| `SECURE_SHELL_USERNAME` | SSH username for remote connections |
| `TDX_BASE_URL` | TeamDynamix API endpoint (e.g., https://servicedesk.emilycarru.ca/TDWebApi) |
| `TDX_APP_ID` | TeamDynamix application ID |
| `TDX_BEID` | TeamDynamix BEID for authentication |
| `TDX_WEB_SERVICES_KEY` | TeamDynamix web services key |
| `TDX_USERNAME` | TeamDynamix username (alternative auth) |
| `TDX_PASSWORD` | TeamDynamix password (alternative auth) |

### Windows Registry Keys
All keys under `HKEY_CURRENT_USER\SOFTWARE\FleetMate`:
- `TdxBaseUrl` - TeamDynamix API base URL
- `TdxAppId` - TeamDynamix application ID  
- `TdxBeid` / `TdxWebServicesKey` - TDX authentication
- `SnipeUrl` / `SnipeApiKey` - Snipe-IT configuration
- `ReportMateUrl` / `ReportMatePassphrase` - ReportMate configuration

---

## Command Reference

### Fleet Monitoring
```bash
fleetmate device <query>           # Look up device by serial/hostname/asset tag
fleetmate errors [--item <name>]   # List installation failures
fleetmate troubleshoot <item>      # Diagnose specific failure
fleetmate status                   # Show config, paths, and service status
```

### Asset Management (Snipe-IT)
```bash
fleetmate snipe assets [--status] [--location] [--search]  # List assets
fleetmate snipe asset <tag>        # Get asset details
fleetmate snipe checkout <id> --user <uid>  # Checkout asset to user
fleetmate snipe checkin <id>       # Check in asset
fleetmate snipe users              # List users
fleetmate snipe user <id>          # Get user details
fleetmate snipe locations          # List locations
fleetmate snipe models             # List asset models
fleetmate snipe categories         # List categories
fleetmate snipe statuses           # List status labels
fleetmate snipe licenses           # List software licenses
```

### Remote Execution (SecureShell)
```bash
fleetmate ssh exec <host> <cmd>    # Run command on host
fleetmate ssh batch <hosts> <cmd>  # Run command on multiple hosts
fleetmate ssh test <host>          # Test SSH connectivity
fleetmate ssh logs <host>          # Get Cimian logs
```

### Microsoft Graph
```bash
fleetmate intune devices           # List Intune devices
fleetmate intune device <serial>   # Get device by serial
fleetmate intune compliance <id>   # Check compliance status
fleetmate entra user <upn>         # Get Entra user
fleetmate entra groups             # List groups
fleetmate entra check-group <user> <group>  # Check membership
```

### TeamDynamix
```bash
fleetmate tdx assets --search <tag>  # Search TDX assets
fleetmate tdx asset <id>           # Get asset details (full field capture)
fleetmate tdx tickets [--status]   # Search tickets
fleetmate tdx ticket <id>          # Get ticket details
fleetmate tdx create <title>       # Create ticket
fleetmate tdx comment <id> <text>  # Add comment
```

---

## Testing Checklist

### Pre-Release Testing
- [ ] Build succeeds: `dotnet build`
- [ ] Help works: `fleetmate --help`
- [ ] Each command help works: `fleetmate <cmd> --help`
- [ ] Status command shows config: `fleetmate status`
- [ ] JSON output works: `fleetmate device <serial> --json`

### Integration Testing (requires credentials)
- [ ] ReportMate: `fleetmate errors`
- [ ] Snipe-IT: `fleetmate snipe assets`
- [ ] SSH: `fleetmate ssh test <host>`
- [ ] Azure DevOps: `fleetmate ado items`
- [ ] Graph: `fleetmate graph devices`
- [ ] TDX: `fleetmate tdx tickets`

---

## Verification Steps

1. **Build:** `dotnet build` completes with no errors
2. **Publish:** `dotnet publish -c Release -r win-x64 --self-contained`
3. **Run:** `./FleetMate.exe status` displays config
4. **Test:** Each command returns expected output
5. **Sign:** Sign executable with enterprise certificate
6. **Package:** Create MSI installer or Chocolatey package
