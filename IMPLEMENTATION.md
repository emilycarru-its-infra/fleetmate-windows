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
├── Commands/           # CLI command implementations (System.CommandLine)
├── Services/           # API client services (HTTP, SSH)
├── Models/             # Data models (Codable structs)
├── Config/             # Configuration loading (YAML + env vars)
├── Converters/         # JSON/YAML converters
├── Program.cs          # Entry point and service initialization
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

### Services (7 total, all functional)

| Service | Lines | Status | Description |
|---------|-------|--------|-------------|
| ReportMateService | 242 | COMPLETE | Fleet monitoring API (devices, errors) |
| PkgInfoService | 364 | COMPLETE | Package metadata parsing and validation |
| SnipeService | 1,195 | COMPLETE | Snipe-IT asset management (full CRUD) |
| SshService | 373 | COMPLETE | SSH remote execution (single + batch) |
| AzureDevOpsService | 536 | COMPLETE | ADO work items, sprints, boards |
| GraphService | 620 | COMPLETE | Intune devices, Entra users/groups |
| TdxService | 522 | COMPLETE | TeamDynamix tickets (search, create, comment) |

### Commands (12 total)

| Command | Status | Description |
|---------|--------|-------------|
| `device` | COMPLETE | Device lookup by serial/hostname/asset |
| `errors` | COMPLETE | List fleet installation failures |
| `troubleshoot` | COMPLETE | Diagnose specific installation issues |
| `status` | COMPLETE | Show config and fleet metrics |
| `snipe` | COMPLETE | Snipe-IT asset management (15 subcommands) |
| `ssh` | COMPLETE | Remote SSH execution (exec, batch, test, logs) |
| `ado` | COMPLETE | Azure DevOps work items (7 subcommands) |
| `graph` | COMPLETE | Intune/Entra queries (7 subcommands) |
| `tdx` | COMPLETE | TeamDynamix tickets (6 subcommands) |
| `test` | PARTIAL | Quality tests (wraps PowerShell) |
| `lint` | PARTIAL | Pkginfo linting (--fix not implemented) |
| `validate` | COMPLETE | Package structure validation |

---

## Remaining Windows Work

### Phase 1: Testing & Hardening
- [ ] Manual test each command with --help
- [ ] Manual test each command with --json output
- [ ] Test error handling with invalid inputs
- [ ] Test all authentication flows (Azure CLI, TDX JWT, SSH keys)
- [ ] Verify pagination in all list commands
- [ ] Test batch SSH operations

### Phase 2: Missing Features
- [ ] Implement `lint --fix` auto-corrections
- [ ] Add `report` command for combined exports
- [ ] Add `lookup` command for cross-system correlation
- [ ] Add retry logic for API failures
- [ ] Add circuit breaker pattern for service unavailability

### Phase 3: Documentation & Distribution
- [ ] Create README.md with command reference
- [ ] Document configuration options
- [ ] Build MSI installer
- [ ] Code sign binaries
- [ ] Create Chocolatey package

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

### Config File Locations (priority order)
1. `~/.fleetmate/config.yaml`
2. `%LOCALAPPDATA%\FleetMate\config.yaml` (Windows)
3. `~/Library/Application Support/FleetMate/config.yaml` (macOS)
4. `/etc/fleetmate/config.yaml` (system-wide)

### Environment Variables
| Variable | Description |
|----------|-------------|
| `REPORTMATE_URL` | ReportMate API endpoint |
| `REPORTMATE_PASSPHRASE` | ReportMate auth |
| `SNIPE_URL` | Snipe-IT base URL |
| `SNIPE_API_KEY` | Snipe-IT API key |
| `SECURE_SHELL_PRIVATE_KEY` | SSH private key content |
| `TDX_BEID` | TeamDynamix BEID |
| `TDX_WEB_SERVICES_KEY` | TeamDynamix web services key |

---

## Command Reference

### Fleet Monitoring
```bash
fleetmate device <query>           # Look up device by serial/hostname
fleetmate errors [--item <name>]   # List installation failures
fleetmate troubleshoot <item>      # Diagnose specific failure
fleetmate status                   # Show config and metrics
```

### Asset Management
```bash
fleetmate snipe assets [--status]  # List assets
fleetmate snipe asset <tag>        # Get asset details
fleetmate snipe checkout <id>      # Checkout asset to user
fleetmate snipe checkin <id>       # Check in asset
```

### Remote Execution
```bash
fleetmate ssh exec <host> <cmd>    # Run command on host
fleetmate ssh batch <hosts> <cmd>  # Run command on multiple hosts
fleetmate ssh test <host>          # Test SSH connectivity
fleetmate ssh logs <host>          # Get Cimian logs
```

### Azure DevOps
```bash
fleetmate ado items [--state]      # List work items
fleetmate ado item <id>            # Get work item details
fleetmate ado create <title>       # Create work item
fleetmate ado from-error <item>    # Create work item from error
```

### Microsoft Graph
```bash
fleetmate graph devices            # List Intune devices
fleetmate graph device <serial>    # Get device by serial
fleetmate graph compliance <id>    # Check compliance status
fleetmate graph user <upn>         # Get Entra user
fleetmate graph check-group <user> <group>  # Check membership
```

### TeamDynamix
```bash
fleetmate tdx tickets [--status]   # Search tickets
fleetmate tdx ticket <id>          # Get ticket details
fleetmate tdx create <title>       # Create ticket
fleetmate tdx comment <id> <text>  # Add comment
fleetmate tdx from-error <item>    # Create ticket from error
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
