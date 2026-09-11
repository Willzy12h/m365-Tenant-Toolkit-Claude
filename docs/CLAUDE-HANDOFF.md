# Claude implementation handover

For the reviewer integrating this implementation with the Codex baseline on `main`. Read `AGENTS.md` first; it states the invariants this implementation is built around and the rules for working on it.

Status vocabulary used throughout: **Tested** (automated test or live-tenant validation exists and passed), **Untested** (implemented, not exercised), **Partial**, **Mocked**, **Planned**.

## 1. Version

BDIT Tenant Toolkit **1.1.0 (unreleased)**; the last packaged build was 1.0.0. Versioning is in `Directory.Build.props`.

## 2. Current development status

Builds green as of the last run through `BUILD-ME-FIRST.cmd`: 0 errors, **89 tests passed**, packaged to `dist\BDIT-Tenant-Toolkit-1.0.0-win-x64.zip`.

**Changes since that build have not yet been compiled**: equivalence signals, the profile-selection fix, copy/open-folder actions, the `$top` probe fix and the associated tests (expected total 99). CI on this branch will report the true state. Do not treat the sections below as verified for those changes until it does.

## 3. Summary of improvements over the Codex baseline

This is a new implementation, not a fork of `main`. It exists because the comparative review (`docs/REVIEW-REPORT.md`) found that neither prior candidate's safety model could be retrofitted. The material differences:

- **Native Windows application** (.NET 8, WPF). No local HTTP service, no browser UI, no Node runtime. The loopback binding, timing-safe token compare, CSP and Host/Origin checks that `main` needs exist only because it is a web app; removing the web app removes that attack surface entirely.
- **Token-level read/write separation.** Two application registrations. An assessment session physically cannot hold a write scope, rather than being trusted not to use one.
- **Safety enforced in three layers** (catalogue validator, planner, Graph client) rather than one: Conditional Access `disabled`, no assignments, writes only to collection root or root/{guid} of declared-writable collections.
- **Ambiguous writes are never retried**; a timeout or 502/503/504 raises `AmbiguousWriteException` and the run stops for human reconciliation.
- **Plans are digest-bound** to tenant, profile, standard, snapshot, managed-object mapping, account and application, and expire with their snapshot.
- **Equivalence signals**: declared, data-driven recognition of a client's own differently named policy, producing an evidence-backed partial match that never claims compliance. Addresses the "names never match" problem directly.
- **Every stored artefact is tenant-bound** and integrity-digested.
- **Dependency-free XLSX and formula-safe CSV.**

## 4. Feature inventory

| Feature | Status | Evidence |
|---|---|---|
| Interactive delegated sign-in (MSAL, system browser, tenant-pinned authority) | Tested (live, read-only) | Connected to one tenant via the shared Graph PowerShell fallback |
| Tenant verification (`/organization` ID must equal profile tenant ID) | Tested (live) | Header showed "verified" |
| Operator resolution and verification (`/me`) | Tested (live) | Operator resolved and verified |
| DPAPI token cache per tenant and mode, deleted on disconnect and exit | Untested live; unit-tested logic | `TokenCacheProtection` |
| Two-registration model, deployment blocked without a deployment client ID | Tested (live, negative) | Deploy page correctly refused with no deployment app configured |
| Access check (roles, per-collection read probe) | Tested (live) | Found and fixed the `/subscribedSkus` `$top` defect |
| Collection of 13 collections incl. assignments, relationships, child settings | Tested (live, one tenant) | Capture completed; per-collection failures recorded |
| Pagination with origin/version/route validation, page and item caps | Tested (unit) | `GraphClientTests` |
| Read retries honouring Retry-After (capped), never for writes | Tested (unit) | `GraphClientTests` |
| Assessment: settings comparison, enforcement, licence, deviation, manual | Tested (unit + live once) | `AssessmentTests` |
| Equivalence signals with groups and caveats | Implemented, **not yet compiled** | `EquivalenceTests` (10 tests) |
| Deviation register | Tested (unit) | |
| Manual check register | Untested | |
| Deployment planner: preflight, name collision, overlap, ownership, digest | Tested (unit) | `PlannerTests` |
| Deployment executor: journal, write, mapping, readback, after-snapshot, pause/stop | Tested (unit, `FakeGraphClient`) | `ExecutorTests` |
| **Live deployment against a tenant** | **Untested** | See §24 |
| Drift analysis with managed-object classification | Tested (unit) | `DriftTests` |
| Reports: engineer HTML/MD/JSON/CSV/XLSX, client HTML, run, drift | Tested (unit for writers; live export once) | |
| Portable packaging, `Start.cmd`, diagnostics launcher | Tested (Windows, one machine) | App launched and operated |
| `BUILD-ME-FIRST.cmd` user-local SDK bootstrap | Tested (Windows, one machine) | |
| Application registration creation | **Planned** | `main` has `Initialize-Applications.ps1`; see §27 |
| Update of existing objects | **Not implemented by design** | Nothing is overwritten; see §18 |
| Application (app-only) authentication | **Not implemented by design** | Delegated only; see §7 |

## 5. Architecture overview

Four projects plus tests. Dependencies point one way: App → Engine → Graph → Core.

- `BDIT.TenantToolkit.Core` — models, canonical JSON, exceptions, safety guards, settings, paths, scrubbing logger. No I/O beyond files.
- `BDIT.TenantToolkit.Graph` — MSAL authentication, token-cache protection, route allow-list, guarded `HttpClient` Graph client, connection service.
- `BDIT.TenantToolkit.Engine` — standards loader/validator, evidence store, collector, assessment, equivalence, name resolution, planner, executor, drift, reports.
- `BDIT.TenantToolkit.App` — WPF shell, `Workspace` composition root, ten page view models and views.
- `BDIT.TenantToolkit.Tests` — xunit; scripted `FakeGraphClient`; no reference to the App project, so everything but the UI builds and tests on Linux.

Full detail: `docs/ARCHITECTURE.md`.

## 6. Important data flows

**Assess:** profile → sign-in (assessment app) → verify tenant and operator → capture every collection (failures recorded, never inferred) → snapshot saved with digest → assess against standard + mappings + deviations → report.

**Deploy:** second sign-in (deployment app) → fresh capture → build plan (digest-bound, expires) → export and acknowledge before-snapshot → typed tenant ID → per row: live re-read preflight → journal intent → write → record mapping → readback subset check → after-snapshot → run report.

**Drift:** two snapshots of the same tenant → object diff → managed-object classification → control status changes.

## 7. Authentication methods

Delegated only, interactive, system browser, MSAL `Microsoft.Identity.Client` 4.89.0, `http://localhost` redirect. Silent renewal only mid-operation; if interaction is required the operation fails and the engineer reconnects deliberately.

App-only (client credential) authentication is deliberately absent: it cannot represent the operator identity that Conditional Access exclusion and evidence attribution depend on.

## 8. Required Microsoft Graph permissions

Assessment registration (delegated): `User.Read`, `Organization.Read.All`, `RoleManagement.Read.Directory`, `Policy.Read.All`, `Policy.Read.AuthenticationMethod`, `DeviceManagementConfiguration.Read.All`, `DeviceManagementApps.Read.All`, `DeviceManagementServiceConfig.Read.All`, `Group.Read.All`, `User.Read.All`.

Deployment registration: the above plus `Policy.ReadWrite.ConditionalAccess`, `DeviceManagementConfiguration.ReadWrite.All`.

The authoritative list is derived at runtime from `collections[*].scope` and `write` in the loaded standard.

## 9. Microsoft Graph endpoints used

Reads (v1.0 unless stated): `/organization`, `/me`, `/roleManagement/directory/roleAssignments`, `/subscribedSkus`, `/identity/conditionalAccess/policies`, `/identity/conditionalAccess/namedLocations`, `/deviceManagement/deviceConfigurations`, `/deviceManagement/configurationPolicies` (beta, with `/settings`), `/deviceManagement/deviceCompliancePolicies` (with `scheduledActionsForRule`), `/deviceAppManagement/mobileApps`, `/deviceAppManagement/managedAppPolicies`, `/deviceManagement/deviceEnrollmentConfigurations`, `/deviceManagement/windowsAutopilotDeploymentProfiles` (beta), `/groups`, `/users`, `/policies/authenticationMethodsPolicy`, plus `/assignments` per object where declared.

Writes: `POST` to the root of, and `PATCH` to root/{guid} of, collections declaring `write`. No other route is reachable; the allow-list is built from the standard.

## 10. Build-standard coverage

`standards/2026.09.3.json`, schema v3: 13 collections, 5 parameters, 45 controls.

## 11. Fully automated controls

12 settings-mode recipes: CA-001, CA-003, CA-004, CA-005, CA-006, CA-007, CA-008, CA-009, CA-010, CMP-WIN-001, CMP-IOS-001, CFG-WIN-003. All produce disabled or unassigned candidates only.

## 12. Manual or review-only controls

33 manual-mode controls. Of these, MAM-IOS-001 and MAM-AND-001 now carry equivalence signals that can lift them to an explained partial match; the CA controls above also carry signals for recognising client-built equivalents.

## 13. Differences from `main`

Language and runtime (Node/PowerShell/browser → .NET 8/WPF); authentication model (single app → two apps); safety enforcement (one layer → three); no local HTTP service; no What-If; no CLI; catalogue schema v3 with `expectedProduction` / `safeDeployment` split and `equivalence`; plan digest binding; ambiguous-write handling; tenant-bound evidence. The full comparison is in `docs/REVIEW-REPORT.md` §4.

## 14. Files that replace Codex components

Everything. This is a whole-product replacement; no file from `main` is retained on this branch.

## 15. Files that should be combined with Codex components

None should be merged at code level. Items worth **porting** from `main` into this codebase are listed in `docs/MIGRATION-FROM-TENANT-CONSOLE.md`; the significant one is `Initialize-Applications.ps1`.

## 16. Schema or configuration changes

- Catalogue schema v3 (`standards/*.json`): `collections` map with `api`, `path`, `scope`, `write`, `assignments`, `children`, `relationship`, `singleton`, `supportsQuery`, `nameProperty`; `parameters`; `controls` with `assessment.mode`, `expectedProduction`, `safeDeployment`, `payload`, `equivalence`.
- `standards/manifest.json` (generated by the build; SHA-256 digests, explicitly not a signature).
- `config/toolkit.settings.json` — assessment and deployment client IDs, fallback flag, timeouts. Ships empty.
- Evidence layout: `data/tenants/<tenantId>/` with snapshots, assessments, plans, runs, journals, `managed-objects.json`, deviations, manual checks.

## 17. Breaking changes

Not applicable to `main` (no shared code). For users of the Codex console: saved profiles, evidence and reports are not migrated; the standard file format is different.

## 18. Security decisions

Recorded as ADRs in `docs/REVIEW-REPORT.md` §5. The ones a reviewer is most likely to question:

- **Conditional Access candidates are created `disabled`, not report-only.** Report-only has documented caveats (user-action policies not evaluated; sign-in interruption in some flows) and is an engineering step after review.
- **Nothing is ever updated or overwritten.** An existing object with the standard name is a *Conflict*, a toolkit-created object modified externally is *Drift*; both block. "Update behaviour" in the Codex sense does not exist, deliberately.
- **The operator is always excluded from created Conditional Access policies, exactly once, and creation is blocked if the operator cannot be resolved.** Stronger than "proposed as an exclusion".
- **Intune objects are never assigned by the toolkit** — not "unless the operator chooses otherwise". Assignment is an engineering step.
- **Emergency accounts are a required profile parameter**; no Conditional Access candidate can be built without them.
- **Digest ≠ signature**, stated wherever integrity is mentioned.
- Logs pass through `SensitiveDataScrubber`; bodies and tokens are never written.

## 19. Runtime requirements

Windows 10/11 x64. Self-contained publish; no .NET installation, no Node, no administrator rights, no installer. Outbound HTTPS to `login.microsoftonline.com` and `graph.microsoft.com`.

Build: .NET 8 SDK (or `BUILD-ME-FIRST.cmd`, which installs a user-local one).

## 20. Local setup instructions

`README.md` — Launch, Authentication and permissions, Working with a tenant. Short form: extract ZIP, run `Start.cmd`, create a profile, connect read-only.

## 21. Test instructions

```
dotnet test tests/BDIT.TenantToolkit.Tests -c Release
```

On Linux, build only `src/BDIT.TenantToolkit.Core`, `Graph` and `Engine` first; the App project needs Windows and is covered by CI.

## 22. Automated test results

Last verified run: **89 passed, 0 failed, 0 skipped** (Windows, .NET 8.0.425). Ten further tests added since are unverified until CI runs on this branch.

## 23. Windows testing completed

One machine (Windows 11 Pro for Workstations). Built, packaged, launched, connected read-only to one tenant, ran the access check, captured configuration, ran assessment, exported. Deployment mode not exercised.

## 24. Live tenant testing completed

**Read-only path only, one tenant, once.** Deployment has never been run against a live tenant. Every Graph create payload is therefore unproven; `docs/LIVE-VALIDATION.md` lists exactly what must be validated and the known payload risks (`scheduledActionsForRule`, OMA-URI for CFG-WIN-003, device filter syntax for CA-008, `includeUserActions` for CA-009/010).

## 25. Known defects

- None open from live testing after the `/subscribedSkus` fix. The fix is unverified until CI runs.
- Settings-catalogue equivalence signals (`CFG-WIN-*`, `SEC-WIN-*`) cannot be written without real setting definition IDs.

## 26. Incomplete or mocked functionality

- Application registration creation: planned, not started.
- Live deployment: implemented, untested against Graph.
- Manual check register: implemented, untested.
- No functionality is mocked in shipped code; `FakeGraphClient` exists only in tests.

## 27. Recommended integration sequence

1. Take this branch as the base. Do not merge `main` into it.
2. Port `Initialize-Applications.ps1` as a standalone setup step outside the read-only app (reasoning in `docs/MIGRATION-FROM-TENANT-CONSOLE.md`).
3. Mine `main` for Graph payloads that were validated against a real tenant and reconcile them with the recipes here.
4. Complete `docs/LIVE-VALIDATION.md` on a test tenant before any client use.
5. Retire `main` per the migration document.

## 28. Outstanding questions

- Is hybrid-joined device trust acceptable as equivalent to compliant-device for CA-005? Currently accepted with a caveat.
- Should the Codex baseline's What-If evaluation be reintroduced as a post-deployment engineering aid? Excluded here as beta-only and out of scope for a safe-candidate tool.
- Owner of the GitHub repository and whether a `blue-diamond-it` organisation should hold it.
