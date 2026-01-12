# FleetMate

FleetMate is a cross-platform command-line tool (macOS + Windows) that centralizes day-to-day fleet operations by encoding “how to talk to our systems” as deterministic, version-controlled commands. Instead of relying on humans to remember portals, endpoints, and workflows, FleetMate becomes the single interface that knows the API calls, auth, and query patterns for the tools we use.

An LLM (Claude) can sit on top as a natural-language layer, but FleetMate remains the execution engine: explicit commands, predictable outputs, and repeatable behavior.

---

## The problem this solves

Daily fleet work requires touching many systems, often to answer a single question:

- “Does this device have any open tickets?”
- “Is it in inventory? What’s the status?”
- “What’s the warranty / enrollment / owner record?”
- “What changed recently? Why is it failing?”
- “Update a record (serial, asset tag, assignment) without opening a UI.”

Today this work is slow because it involves:
- UI hunting across multiple portals
- manual API calls in tools like Insomnia
- tribal knowledge (“Rod knows how to do this”)
- repeated context switching (tickets → inventory → MDM → device → back again)

---

## The core idea

FleetMate is one binary that:
- **knows all supported systems**
- **has the API calls coded** (no discovery at runtime)
- **uses standard auth patterns** (SSO tokens, Key Vault-sourced secrets, env vars)
- **returns structured output** for humans, scripts, and AI tools

Claude (or another agent) can drive FleetMate via “skills” or tool schemas, but FleetMate is the stable contract: it is programmatic and predictable.

---

## FleetMate and AI: separation of responsibilities

### FleetMate (deterministic execution)
- Implements the real integrations: endpoints, payloads, pagination, retries, rate limits
- Handles authentication and secure configuration
- Produces consistent output formats (JSON, tables, reports)
- Works with or without an AI in the loop

### AI skills (natural language UX)
- Teach the model what FleetMate can do
- Translate intent into explicit FleetMate commands
- Prevent the “rediscover the API every time” failure mode
- Keep the model from improvising endpoints/payloads

The goal is: the model becomes the UI, FleetMate remains the source of truth for execution.

---

## Systems FleetMate is intended to unify

Examples discussed and implied in the transcript:

- Ticketing (TDX)
- Inventory (Snipe-IT or equivalent)
- MDM / device management (Munki workflows, MicroMDM or similar)
- Reporting (ReportMate)
- Windows management (Cimian)
- Identity + device graph (Azure Graph for Intune + Entra)
- DevOps automation (Azure DevOps)
- Apple School Manager (warranty/enrollment style checks)
- SSH for device-level inspection and remediation

The key is not the vendor list; it’s the pattern: one interface over many systems.

---

## What it should feel like (user experience)

FleetMate should make these interactions fast:

- “Tell me everything you know about this computer.”
- “Check all systems and give me a status report.”
- “Is this device in inventory? Any tickets mentioning it?”
- “Check serial across all systems and summarize.”
- “Update a record without opening a portal.”

Humans can run explicit commands. AI can drive the same commands. Outputs remain consistent.

---

## Command model (conceptual)

A practical mental model:

- `fleetmate device ...` for device-centric lookups
- `fleetmate tickets ...` for ticket correlations
- `fleetmate inventory ...` for asset records and lifecycle
- `fleetmate mdm ...` for enrollment/compliance/status
- `fleetmate ssh ...` for device-level diagnostics
- `fleetmate report ...` for combined summaries and exports

FleetMate should bias toward composable commands and machine-readable output.

---

## Example workflows pulled from the transcript

### 1) Ticket + inventory correlation (canonical use case)
Natural language intent:
- “Does this device have any tickets?”
- “Is it in inventory?”
- “Summarize what we know.”

FleetMate shape:
- Look up device identifiers
- Query ticket system for matching serial/asset tag/device ID
- Query inventory for assignment, status, invoice linkages, location
- Return a unified report

### 2) Replacing manual API calls (Insomnia → FleetMate)
Instead of crafting requests by hand:
- `fleetmate inventory update --serial ... --field ... --value ...`

The point: FleetMate already knows the endpoint, payload, and auth.

### 3) SSH-driven troubleshooting
Intent:
- “Here’s the IP, what’s wrong with this computer?”
- “Do this for many machines.”

FleetMate shape:
- SSH into host
- gather specific diagnostics (logs, state, disk, network, service status)
- return structured findings
- optionally run safe remediations behind explicit flags

### 4) Invoice PDF reconciliation (admin workflow automation)
Pain point:
- Vendor emails PDFs with invoice numbers
- Manual cross-reference against inventory to confirm receipt and approve payment

FleetMate shape:
- ingest invoice numbers (from PDFs or extracted list)
- query inventory for matching invoice/PO fields
- confirm received status + device list
- produce a “ready to pay” report

### 5) Faculty laptop program: upgrade-cost PDF generation
Workflow described:
- baseline model: no paperwork
- above baseline: generate a PDF for signature with upgrade cost
- template exists; data comes from inventory and ordering records
- output should land where the team expects it (ex: SharePoint)

FleetMate shape:
- `fleetmate forms generate faculty-upgrade --person ... --serial ... --options ...`
- produce PDF + store it + return link/path + status

---

## Security and configuration assumptions

The transcript implies this approach:

- FleetMate reads configuration from environment variables
- Setup scripts can source secrets from Key Vault
- Prefer SSO / expiring tokens when possible
- Avoid embedding secrets in repos or logs
- Make “who did what” auditable (log identifiers and actions, not secrets)

---

## Why this matters (benefits)

- Reduces context switching and portal hunting
- Codifies operational knowledge into a tool anyone can run
- Makes workflows repeatable and supportable
- Enables bulk operations (audit, correlate, remediate)
- Provides a stable execution layer for AI-assisted ops

---

## Related supporting ideas from the conversation

### Git worktrees + subagents
Parallel work with fewer merge conflicts:
- create separate worktrees for separate integration tasks
- let multiple agents explore approaches in isolation
- compare results and choose the best implementation

### Documentation as a forcing function
FleetMate encourages:
- source control first
- workflows written down as commands
- fewer “Word doc in SharePoint” or hidden UI-only “code”

---

## Appendix: non-FleetMate conversation context

Parts of the transcript are incidental:
- platform SSO / setup assistant expectations
- lab wipe/imaging logistics and Munki permission/ownership changes
- brief sci-fi tangent
- general ops chatter

They provide context for why FleetMate is valuable, but are not core requirements.

---

## Next steps (practical)

1) Define the first 5–10 commands that cover the highest-frequency pain
2) Implement those integrations with stable output formats
3) Add AI “skills” only after the command contract is stable
4) Expand system coverage iteratively, keeping everything in source control