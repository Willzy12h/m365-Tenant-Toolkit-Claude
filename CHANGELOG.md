# Changelog

## 1.1.0 (unreleased) - equivalent configuration and usability

### Assessment
- **Equivalence signals.** Controls may declare, as catalogue data, what an existing object must look like to cover them. A client policy that meets every required condition is reported as a partial match naming the object and the properties that satisfied each condition, instead of *Missing* or *Requires manual review*. Equivalence never reports Compliant on its own, never overrides a stronger settings result, and always surfaces exclusions and disabled states as caveats.
- Signals support grouping, so alternative routes to the same outcome (an MFA built-in control or an authentication strength) both count.
- Signals drafted for CA-001, CA-003, CA-004, CA-005, CA-006, CA-007, CA-008, CA-009, CA-010, MAM-IOS-001 and MAM-AND-001.
- Engineer HTML and the CSV/XLSX exports gained an equivalence table with expected and observed values, plus a caveats sheet.

### Fixes
- The access check no longer probes `/subscribedSkus` with `$top=1`; collections may declare `supportsQuery: false`. Previously a Graph `400 UnsupportedQuery` was reported as if it were a permission failure on `Organization.Read.All`.
- Selecting a saved client reloads its details reliably. Previously the selection could be set internally without loading the form, so re-selecting a client did nothing and saving then created a duplicate client.
- Saving a second client for a tenant that already has one is refused; evidence, mappings and deviations are keyed by tenant ID and would otherwise be split in two.

### Usability
- Connection details, application details, the access check, export paths and finding details are selectable text with Copy buttons.
- Exports report how many objects were written and offer **Open containing folder**.
- The Connect page names the selected client and separates "connect to selected client" from "save and connect".

## 1.0.0 (2026-09-11) - consolidated design

Replaces two earlier prototypes (Tenant Console 0.3.2-rc.1, Node/PowerShell; and the .NET/WPF Tenant Toolkit 1.0.0 draft). Nothing was merged mechanically; the design decisions are recorded in `docs/REVIEW-REPORT.md`.

### Architecture
- Single .NET 8 solution: Core, Graph, Engine, WPF App, Tests. Self-contained win-x64 portable release; no runtime installation.
- Native Microsoft Graph HTTP client replaces the PowerShell worker and the Graph PowerShell module dependency.
- Removed the CLI project and the What-If (beta) service from scope; both are documented as future options.

### Safety
- Two application registrations (assessment and deployment) for token-level read/write separation; incremental consent within one registration was rejected because tokens carry every consented scope.
- Conditional Access candidates are created `disabled` with expected targeting, emergency accounts, optional exclusion group and the verified operator excluded; report-only was rejected as the initial state.
- Write guard enforced in the catalogue validator, the planner and the Graph client; assignments are never written.
- Plans are digest-bound to tenant, profile, standard, snapshot, managed-object mapping, account and application; they expire with their snapshot.
- Pre-write reconciliation, readback verification, after-change snapshots, no automatic retry of writes, honest interrupted-run marking, close-waits-for-write shutdown.

### Assessment
- Collection-based comparison with property-level differences and name resolution; statuses distinguish enforced, not enforced, partial, missing, unknown, manual, licence and not applicable.
- Licence evaluation from subscribed SKUs.
- Deviation register (approved deviation or not applicable) that never hides unknown data.
- Drift analyser classifies toolkit-managed objects (removed, modified externally, metadata only) and explains control status changes.

### Evidence and reports
- Tenant-partitioned evidence with integrity digests; every load checks tenant binding.
- Engineer HTML, Markdown, JSON, CSV and XLSX; client-facing HTML summary; run and drift reports. Formula-safe CSV/XLSX with no third-party dependency.

### Build
- `BUILD-ME-FIRST.cmd` installs a user-local .NET 8 SDK into `.dotnet\` (no administrator rights) and runs the full restore, build, test, manifest, publish and package pipeline; all output is written to `build\last-build.log`.
- Repo-level `nuget.config` pins nuget.org so restore does not depend on machine-level NuGet settings.
- First green build: 0 errors, 89 tests passing, packaged to `dist\BDIT-Tenant-Toolkit-1.0.0-win-x64.zip`.

### Standards
- Catalogue schema v3 with expected production state and safe deployment state per control; 45 controls (12 automated recipes) carried over from Tenant Console 2026.09.2 and enriched with severity, business impact, engineer action, licence and manual instructions.
- SHA-256 manifest integrity check (documented as a digest, not a signature).
