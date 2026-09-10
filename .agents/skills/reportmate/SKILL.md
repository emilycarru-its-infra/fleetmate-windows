---
name: reportmate
description: Query and administer a ReportMate fleet through the reportmate CLI — device inventory, per-device module documents, events and logs, fleet-wide module and application reports, certificate search, fleet log sweeps, ingest failures, usage-history maintenance, API keys and settings. Use when a task in this repository needs fleet or device data, when changing how FleetMate talks to ReportMate, or when reproducing what FleetMate's ReportMate integration does by hand. Triggers include which machines have X installed, device hardware/network/security/installs info, install errors across the fleet, failed check-ins, last seen, IP address of a device, app usage, expiring certificates, ReportMate API.
compatibility: Needs the reportmate CLI on PATH (github.com/reportmate/reportmate-cli releases) and REPORTMATE_API_URL plus one credential in the environment. Raw REST fallback needs only curl.
metadata:
  version: "2026.09.09.1928"
---

# ReportMate through the reportmate CLI

ReportMate is the fleet reporting platform FleetMate reads from: device agents post module data to a REST API, and everything the fleet knows is readable under `/api/v1`. The `reportmate` CLI is the reference client for that API. It has a command for every route and prints the API's JSON unchanged, which is why FleetMate prefers it: `ReportMateService` (both platforms) routes every read through the installed CLI and falls back to its own HTTP client only when no CLI can be launched. Keeping the two in step is a matter of updating the CLI, not FleetMate.

## Setup

Install the CLI from its GitHub release (a universal macOS binary and Windows x64/arm64 builds), or let the fleet management pipeline install it. Then export the target and a credential:

```
export REPORTMATE_API_URL=https://reportmate.example.edu
export REPORTMATE_API_KEY=rm_client_secret
```

An OIDC bearer token (`REPORTMATE_TOKEN`) or the shared client passphrase (`REPORTMATE_PASSPHRASE`) also work. `REPORTMATE_CLI=/path/to/reportmate` pins FleetMate to a specific binary; set it empty to force the HTTP path.

Every command takes `--output json`. Errors go to stderr with the upstream status and body, non-zero exit on failure. `reportmate <command> --help` lists every flag.

## Devices

```
reportmate devices --limit 20 --output json
```

One device, everything or one module; the cheap summary; events; the managed-software install log; one tool's log tail; app usage history:

```
reportmate device SERIAL
```

```
reportmate device SERIAL --module installs
```

```
reportmate device SERIAL info
```

```
reportmate device SERIAL events --limit 20 --type error
```

```
reportmate device SERIAL installs-log
```

```
reportmate device SERIAL log munki
```

```
reportmate device SERIAL usage --days 90 --app Photoshop
```

Lifecycle needs an admin-scoped credential; `delete` refuses to run without `--confirm`:

```
reportmate device SERIAL archive
```

## Fleet reports

Any module (`hardware`, `applications`, `installs`, `network`, `security`, `management`, `inventory`, `system`, `peripherals`, `identity`, `profiles`), with `--include-archived`, `--limit`, `--offset`, and `--param k=v` for anything else. The `network` report is what FleetMate's host scanner uses to turn serial numbers into addresses in one call:

```
reportmate module network --limit 1000
```

```
reportmate module installs/full --limit 500
```

Dashboard rollup, certificate search, and a fleet-wide sweep of one tool's log tails (`--summary` folds the same fault on many devices into one pattern):

```
reportmate dashboard
```

```
reportmate certificates --status expiring
```

```
reportmate logs munki --summary
```

## Applications

```
reportmate apps list --names "Zoom,Slack" --platforms macos
```

```
reportmate apps usage --days 30 --min-hours 1
```

```
reportmate apps by-device Photoshop --days 90
```

```
reportmate apps distribution "Zoom,Slack"
```

```
reportmate apps filters
```

```
reportmate apps collection-health
```

## Events and ingest

Recent events with filters; check-ins the API turned away (`--outcome rejected|retried|accepted|all`), which is the first stop when a device "stopped reporting"; the full payload of one event:

```
reportmate events --limit 20 --type error --since 2026-09-01
```

```
reportmate events failures --hours 24
```

```
reportmate events payload EVENT_ID
```

## Health, admin, settings

```
reportmate health --ready
```

```
reportmate metrics
```

API keys, usage-history maintenance (`date-anomalies`, `integrity`, `export --from --to`, `reset-baseline --before --confirm`, `cleanup`), installs maintenance (`clear-errors`, `reclassify`), database diagnostics, and org settings:

```
reportmate api-keys create ci-reader --scope read
```

```
reportmate admin usage-history integrity --days 7
```

```
reportmate admin installs clear-errors --days 10
```

```
reportmate admin debug-database
```

```
reportmate settings get
```

Anything else, with any method:

```
reportmate raw /api/v1/dashboard --param eventsLimit=10
```

## How FleetMate uses it

- `ReportMateCli` (Swift: `Sources/FleetMateCore/Services/Reporting/ReportMateCli.swift`; C#: `FleetMate.Core/Services/Reporting/ReportMateCli.cs`) locates the binary and runs it with only the API URL and one credential in its environment.
- `ReportMateService` pairs every read with the CLI arguments that produce the same JSON as the HTTP path (`devices --limit --offset`, `module installs`, `module network`, `device SERIAL module network`, `device SERIAL installs-log`, `device SERIAL`). A CLI that cannot launch falls back to HTTP; a CLI that ran and got an API refusal surfaces it, and a `-> 404` in its stderr is treated as "not found".
- The host scanner and SSH/ARD resolvers take addresses from the fleet `network` report first and only then ask for one device's network module.

When adding a ReportMate read to FleetMate, add the CLI arguments alongside the HTTP path rather than a second HTTP call.

## Raw REST fallback

Authenticate every request with one header: `X-API-Key`, `Authorization: Bearer`, or `X-Client-Passphrase`. Paths are the same ones the commands above wrap: `/api/v1/devices`, `/api/v1/device/{serial}` (`/info`, `/modules/{module}`, `/events`, `/installs/log`, `/logs/{tool}`, `/applications/usage/history`), `/api/v1/{module}`, `/api/v1/applications/...`, `/api/v1/security/certificates`, `/api/v1/management/logs/{tool}`, `/api/v1/events` (`/failures`, `/{id}/payload`), `/api/v1/dashboard`, `/api/v1/health`, `/api/v1/admin/...`, `/api/v1/settings`. 401 means no or bad credential, 403 a missing scope, 429 rate limiting with `Retry-After`. The API source of truth is github.com/reportmate/reportmate-api.
