# Wiring FleetMate into cimian-bootstrap-mgmt (complete runbook)

FleetMate deploys via Cimian as a per-arch **signed MSI** mgmt tool, same class as
CimianTools/SbinInstaller. This is the exact, no-doubt runbook to sign + import it
via the production pipeline.

## Prerequisites (infra — must exist before the run)

1. **Pipeline access to FleetMate source or release.** `emilycarru-its-infra/fleetmate-windows`
   is PRIVATE. The other tools' fetch jobs pull PUBLIC GitHub releases unauthenticated;
   FleetMate needs auth. Pick one:
   - Add a GitHub token (PAT/app with `repo` read) as a pipeline secret and use it in the
     fetch step's `Authorization` header, OR
   - Update the existing `quality/fleetmate` submodule to the release commit and init it with
     an authed URL (the agent currently can't — private, no creds), OR
   - Make the specific release asset download public.
2. **Signing cert** — already available: KV `intune-graph-secrets` / `EmilyCarrWindowsEnterpriseCodeSignCert`,
   CN `EmilyCarrU Intune Windows Enterprise Certificate` (same block every fetch job uses).

## Edit 1 — parameter (after `sbinInstallerTag`)

```yaml
  - name: fleetmateTag
    displayName: "FleetMate release tag (emilycarru-its-infra/fleetmate-windows)"
    type: string
    default: latest
```

## Edit 2 — `fetch_fleetmate` job (end of BuildArtifacts stage)

FleetMate's own `build.ps1 -MsiOnly -Sign -Thumbprint <t>` signs the payload exe
BEFORE embedding it in the MSI cab, then signs the MSI — so the job reuses it instead
of re-implementing decompile/repack:

```yaml
      - job: fetch_fleetmate
        displayName: "Build + sign FleetMate MSI ($(arch))"
        strategy: { matrix: { x64: {arch: x64}, arm64: {arch: arm64} } }
        steps:
          - checkout: self            # submodules: false
          - task: AzureCLI@2          # <identical Key Vault cert import block; sets importSigningCert.signingCertThumbprint>
            name: importSigningCert
            ...
          - pwsh: |                   # get FleetMate source at the release tag (NEEDS the token from Prereq 1)
              git clone --depth 1 --branch '${{ parameters.fleetmateTag }}' `
                "https://$(GH_TOKEN)@github.com/emilycarru-its-infra/fleetmate-windows.git" src
            displayName: 'Clone FleetMate source'
          - pwsh: |                   # one call: publishes per-arch, signs exe, builds + signs MSI
              Set-Location src
              ./build.ps1 -MsiOnly -Sign -Thumbprint '$(importSigningCert.signingCertThumbprint)'
              # produces src/release/FleetMate-<arch>-<YYYY.MM.DD.HHMM>.msi
            displayName: 'build.ps1 -MsiOnly -Sign'
          - task: PublishPipelineArtifact@1
            inputs:
              targetPath: 'src/release'
              artifact: 'fleetmate-msi-$(arch)'
              publishLocation: 'pipeline'
```

(Note: `build.ps1 -MsiOnly` builds BOTH arches itself, so a matrix is optional — a single
non-matrix job calling it once yields both MSIs. Matrix shown for symmetry with siblings.)

## Edit 3 — stage MSIs into `public/` (PublishToBlob `$patterns` list, ~line 2013)

```powershell
    @{ Key='FleetMate-x64';   Filter='FleetMate-x64-*.msi' }
    @{ Key='FleetMate-arm64'; Filter='FleetMate-arm64-*.msi' }
```

This copies them into `public/`, which the existing "cimiimport each built MSI" step imports
into `deployment/pkgsinfo/mgmt/` + `deployment/pkgs/mgmt/`. FleetMate has no management.json
template entry, so it is imported into the repo but NOT added to the bootstrap chain (correct).

## Edit 4 — manifest (`deployment/manifests/ManagementTools.yaml`)

Add to `managed_installs:` (name must equal pkgsinfo `name: FleetMate`):
```yaml
  - FleetMate
```

## Run

- Run `cimian-bootstrap-mgmt` with **stage = Publish** (Build + PublishToBlob; no Intune Deploy).
  The commit step pushes `deployment/pkgsinfo` to `origin/main` regardless of the run branch,
  which triggers `cimian-push-production.yml` → `makecatalogs` → blob + Front Door purge.
- Then commit Edit 4 to `main` (manifests/ triggers push-production again; harmless if pkgsinfo
  already landed).

## Verify on this machine

```powershell
& "C:\Program Files\Cimian\managedsoftwareupdate.exe" --checkonly -vvv   # FleetMate pending?
& "C:\Program Files\Cimian\managedsoftwareupdate.exe" --auto -vv         # install
& "C:\Program Files\FleetMate\fleetmate.exe" --version                    # confirm
Get-Content "C:\ProgramData\ManagedInstalls\Logs\ManagedSoftwareUpdate.log" -Tail 50
```
