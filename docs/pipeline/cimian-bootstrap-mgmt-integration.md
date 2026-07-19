# Deploying FleetMate via Cimian (mgmt tool)

FleetMate ships through the **Cimian** software-distribution channel — **not Intune**
(only BootstrapMate goes to Intune). It is the same shape as `CimianTools` /
`SbinInstaller`: a **per-architecture signed MSI**, imported into the Cimian repo with
`cimiimport` as `installer_type: msi`, referenced by `name` in a manifest's
`managed_installs`.

## Artifacts (this repo)

`build.ps1 -MsiOnly` (and `release.yml` on `v*` tags) produces, for x64 and arm64:

- `FleetMate-<arch>-<YYYY.MM.DD.HHMM>.msi` — filename carries the **full calendar
  version**, which `cimiimport` uses as the Cimian pkgsinfo `version`.
- MSI internal `ProductVersion` is the compressed MSI-safe `yy.M.{day*100+hour}`
  (e.g. calendar `2026.07.18.1819` → MSI `26.7.1818`), matching CimianTools exactly.

The MSI installs the self-contained `fleetmate.exe` to `C:\Program Files\FleetMate`,
adds it to the system PATH, and writes `HKLM\SOFTWARE\FleetMate` (`Version`,
`LastRunVersion`). Cimian's install-check keys off the MSI `product_code` +
`key_path` (`C:\Program Files\FleetMate\fleetmate.exe`).

## End-to-end deployment path

```
release.yml (v* tag)  →  FleetMate-<arch>-<ver>.msi on the GitHub release (unsigned)
   ↓
cimian-bootstrap-mgmt.yml  (add a fetch_fleetmate job like fetch_cimian_tools):
   fetch release MSIs → sign with enterprise cert (EmilyCarrU Intune Windows
   Enterprise Certificate, KV intune-graph-secrets / EmilyCarrWindowsEnterpriseCodeSignCert)
   → existing stage: cimiimport each MSI → deployment/pkgsinfo/mgmt/ + deployment/pkgs/mgmt/
   → azcopy sync deployment/pkgs → blob (cimiancloudstorage/repo)
   → git commit deployment/pkgsinfo → main
   ↓ (commit to main under deployment/** triggers)
cimian-push-production.yml:  makecatalogs → upload catalogs/manifests/pkgsinfo to blob → Front Door purge
```

Then add FleetMate to the mgmt manifest and run the client:

- **Manifest:** `deployment/manifests/ManagementTools.yaml` → add `- FleetMate` to
  `managed_installs` (name must equal the pkgsinfo `name: FleetMate`; one entry covers
  both arches — the client resolves by `supported_architectures`).
- **pkgsinfo:** two files (`FleetMate-x64-<ver>.yaml`, `FleetMate-arm64-<ver>.yaml`),
  both `name: FleetMate`, `category: Management`, `catalogs: [Development, Testing,
  Staging, Production]`, `supported_architectures: [x64]` / `[arm64]`.
- **Client (this machine):**
  ```powershell
  & "C:\Program Files\Cimian\managedsoftwareupdate.exe" --checkonly -vvv   # FleetMate pending?
  & "C:\Program Files\Cimian\managedsoftwareupdate.exe" --auto -vv         # install
  Get-Content "C:\ProgramData\ManagedInstalls\Logs\ManagedSoftwareUpdate.log" -Tail 50
  ```

## cimiimport invocation (per the pipeline)

```powershell
cimiimport "FleetMate-x64-<ver>.msi"   --repo_path="<repo>\deployment" --nointeractive
cimiimport "FleetMate-arm64-<ver>.msi" --repo_path="<repo>\deployment" --nointeractive
```

`cimiimport` derives `version` from the filename, extracts `product_code`/`upgrade_code`
from the MSI, and writes the pkgsinfo under `mgmt/`. Set `catalogs` and confirm
`installs[].key_path` afterward.
