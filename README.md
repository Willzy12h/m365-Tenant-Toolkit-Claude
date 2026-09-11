# BDIT Microsoft 365 Tenant Toolkit

The toolkit operationalises the **BDIT Build Standard** for Blue Diamond IT client tenants. It connects to one Microsoft 365 tenant at a time, reads its configuration, compares it property by property with a versioned standard, records approved deviations, detects drift between visits, and can create *safe candidate* configuration that an engineer then validates, assigns and enables.

It is not a general administration portal and it does not compete with CIPP. Assessment is the primary capability; deployment is secondary and deliberately conservative.

## What it does

| Step | What happens | Writes to the tenant? |
|---|---|---|
| Connect | Interactive Microsoft sign-in in your browser, pinned to the tenant ID; the organisation and the signed-in operator are verified | No |
| Configuration | Reads Conditional Access, named locations, Intune configuration, settings catalogue, compliance, apps, app protection, enrolment, Autopilot, groups, users, authentication methods and licences into a local snapshot | No |
| Assessment | Compares every control in the standard with the snapshot: settings match, match-not-enforced, partial match, missing, unable to assess, manual review, licence unavailable, not applicable | No |
| Deviations | Records approved departures from the standard per tenant | No |
| Plan | Builds an integrity-bound plan of safe candidates for selected controls | No |
| Deploy | With a separate deployment sign-in, a typed tenant ID and an acknowledged before-change snapshot: creates disabled Conditional Access candidates or unassigned Intune objects, reads each back, captures an after-change snapshot | Yes, only then |
| Evidence and drift | Browses snapshots and runs; compares two snapshots and names what changed | No |

## What it never does

- Enable a Conditional Access policy or set it to report-only.
- Assign an Intune policy or profile.
- Delete, adopt or overwrite an existing policy because its name looks right.
- Infer that something is missing when a collection failed to read.
- Retry a write after an ambiguous failure.
- Store credentials, secrets or tokens in plain text.

## Prerequisites

- Windows 10 or 11, x64. No local administrator rights, no installer, no .NET runtime installation (the release is self-contained).
- A tenant account that can read the configuration (see *Authentication*). Global Administrator is not required for assessment.
- Outbound HTTPS to `login.microsoftonline.com` and `graph.microsoft.com`.

## Launch

1. Extract the release ZIP to a writable folder (for example `C:\Tools\BDIT-Tenant-Toolkit`). Do not use `Program Files`.
2. Run `Start.cmd`. A native window opens; no console window remains.
3. If it does not start, run `Start-Diagnostics.cmd`, which launches with verbose logging and shows `logs\startup.log`.

Everything the toolkit reads or writes lives under the extracted folder:

```
app\         the application (self-contained .NET 8, WPF)
standards\   Build Standard releases and manifest.json (integrity digests)
config\      toolkit.settings.json
data\        profiles and per-tenant evidence (data\tenants\<tenantId>\...)
logs\        JSONL diagnostic logs (secrets are scrubbed before writing)
reports\     exported reports
docs\        this documentation
```

## Authentication and permissions

The toolkit uses delegated authentication (MSAL, system browser, DPAPI-protected token cache per tenant and mode). It never uses an embedded web view and never sees your password or MFA.

Two application registrations give token-level separation between reading and writing:

| Registration | Permissions (delegated) | Used for |
|---|---|---|
| **BDIT Tenant Assessment** | `User.Read`, `Organization.Read.All`, `RoleManagement.Read.Directory`, `Policy.Read.All`, `Policy.Read.AuthenticationMethod`, `DeviceManagementConfiguration.Read.All`, `DeviceManagementApps.Read.All`, `DeviceManagementServiceConfig.Read.All`, `Group.Read.All`, `User.Read.All` | Every read-only session |
| **BDIT Tenant Deployment** | the read scopes above plus `Policy.ReadWrite.ConditionalAccess`, `DeviceManagementConfiguration.ReadWrite.All` | Deployment sessions only |

Record the client IDs in `config\toolkit.settings.json` (`assessmentClientId`, `deploymentClientId`). Both registrations are public clients with the `http://localhost` redirect URI. Register them once in the BDIT tenant as multi-tenant applications and grant admin consent in each client tenant, or register single-tenant copies in a client tenant and record their IDs on that client's profile. The exact scope list is in the Build Standard (`collections[*].scope` and `write`).

If no assessment client ID is configured, the toolkit falls back to the shared Microsoft Graph PowerShell application for read-only work and warns that the token may carry previously consented write scopes. Deployment never uses the shared application.

Why two registrations: an access token contains every delegated scope consented for that application, not just the scopes requested. A single registration with write consent can never issue a read-only token. The toolkit additionally blocks every write in an assessment session, but the separate registration makes that isolation real at the token level.

Roles: assessment works with read-capable roles (for example Global Reader or Security Reader plus Intune read). Creating Conditional Access policies requires Conditional Access Administrator or Security Administrator; creating Intune objects requires Intune Administrator. Global Administrator is not required.

## Working with a tenant

1. **Connect page.** Create a client profile: company label, tenant ID, and the policy parameters (emergency access account object IDs, office named-location ID, MAM-only group ID, optional exclusion group). Save and connect read-only. The access check shows observed roles and a read probe per collection. It creates nothing.
2. **Configuration page.** Read the tenant configuration. Collections that fail are recorded as failures; nothing is assumed absent. Export the capture as JSON, CSV or Excel.
3. **Assessment page.** Review findings. Where a control declares equivalence signals, a client's own differently named policy that meets the control's real conditions is reported as a partial match naming that policy and the properties that satisfied it, rather than as missing. See `docs\EQUIVALENCE-SIGNALS.md`. Each recognised policy shows property-level differences with IDs resolved to names. Export the engineer report (HTML, Markdown, JSON, CSV, Excel) or the client-facing summary.
4. **Deviations page.** Record approved deviations or not-applicable controls. They show as *Compliant (approved deviation)* or *Not applicable* but never hide unknown data.
5. **Manual checks page.** Record outcomes and evidence notes for controls that cannot be assessed automatically.
6. **Evidence and drift page.** Compare any two captures of the tenant. Toolkit-created objects are classified as removed, modified externally or metadata-only.

## Deployment

1. On the Deploy page, choose **Enable deployment access**. You sign in again with the deployment registration. The read-only capture and plan are discarded; capture again in the deployment session.
2. Read the tenant configuration, then on the Plan page select controls and build a plan. The plan is bound to the tenant, profile, standard release, snapshot, managed-object mapping, account and application by SHA-256 digests.
3. Export and acknowledge the before-change capture.
4. Choose **Deploy reviewed changes** and type the tenant ID in full.
5. Every write is preceded by a live re-read (name collisions, overlapping settings, ownership, state, assignments), followed by a readback that must confirm the requested settings, and the run ends with an after-change capture. Pause and stop take effect at the next action boundary; closing the window waits for the in-flight write.

### Safe candidate behaviour

| Object type | Expected production state (documented) | What the toolkit creates |
|---|---|---|
| Conditional Access policy | Enabled, targeting all users, emergency accounts excluded | `state: disabled`, expected targeting written, emergency accounts, optional exclusion group and the signed-in operator excluded |
| Intune compliance or configuration | Assigned to the standard device or user group | Created with no assignment |

The operator exclusion does not expire; remove it deliberately after testing. Enabling, report-only evaluation, assignment and functional testing follow the rollout procedure in `docs\BUILD-STANDARD-SUMMARY.md`.

## Reports and evidence

- Engineer report: HTML, Markdown, JSON, CSV (zip) and Excel, including property-level differences and collection status.
- Client summary: HTML with overall position, prioritised recommendations, business impact, agreed exceptions and checks not completed. No raw Graph JSON.
- Deployment run: results, readback status, journal.
- Drift: control status changes and object-level differences.

Evidence stays local under `data\tenants\<tenantId>\` (snapshots, assessments, plans, runs, journals, managed-object mapping, deviations, manual checks). Snapshots and runs carry a SHA-256 integrity digest that detects modification; this is tamper-evident, not tamper-proof, and not a signature.

## Local storage and security

- No service, scheduled task, registry change, PATH change or elevation.
- Token caches are DPAPI-protected to the Windows user and deleted on disconnect and on exit.
- Logs never contain tokens, bodies or credentials; every message passes through a scrubber.
- CSV and Excel exports store every value as text so a hostile display name cannot become a formula.
- The Build Standard is loaded only if its SHA-256 digest matches `standards\manifest.json`.

## Troubleshooting

| Symptom | Action |
|---|---|
| Window does not appear | Run `Start-Diagnostics.cmd`; read `logs\startup.log`. Most causes are an incomplete extraction or a blocked `app\BDIT.TenantToolkit.App.exe` under application control. |
| SmartScreen "unknown publisher" on first launch | Expected: the tool is internal and unsigned. Verify the ZIP against its `.sha256` file, unblock the ZIP (Properties, Unblock) before extracting, or choose Run anyway. |
| "Build Standard could not be loaded" | `standards\manifest.json` is missing or the standard was edited after release. Use the released package or run `build\Update-StandardsManifest.ps1` on a maintained checkout. |
| Sign-in never returns | The browser tab may have been closed. Wait for the timeout (5 minutes by default) and try again. |
| 403 during capture | The access check names the collection and the expected delegated scope. Grant consent or use an account with a read-capable role. |
| Plan rejected before deployment | Plans expire with their snapshot (20 minutes by default) and are invalidated by any change to profile, standard, snapshot, mapping, account or application. Capture and plan again. |
| Run ended "Review required" | Read the run journal. Ambiguous failures are never retried automatically; reassess the tenant before repeating. |

## Updating the Build Standard

1. Add or edit a release file in `standards\` (schema version 3). Control IDs, collections and safe-deployment states are validated on load; a Conditional Access recipe that requests any state other than `disabled` is rejected.
2. Run `build\Update-StandardsManifest.ps1` to regenerate the digest manifest.
3. Run the tests and rebuild the portable package with `build\Build-Portable.cmd`.
4. Choose the release on the Build Standard page. Plans are invalidated when the release changes.

## Building from source

No SDK installed? Double-click `BUILD-ME-FIRST.cmd` in the repository root. It installs a user-local .NET 8 SDK into `.dotnet\` (no administrator rights, nothing outside the folder), then builds, tests and packages into `dist\`. Run `dist\BDIT-Tenant-Toolkit-<version>-win-x64\Start.cmd` afterwards.

## Developers

See `docs\ARCHITECTURE.md` for project structure, the catalogue schema, how to add a control or collector, testing and packaging. Build with the .NET 8 SDK:

```bash
dotnet build BDIT.TenantToolkit.sln -c Release
dotnet test tests/BDIT.TenantToolkit.Tests -c Release
build\Build-Portable.cmd
```
