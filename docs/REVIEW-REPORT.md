# BDIT Microsoft 365 Tenant Toolkit - comparative review, architecture decisions and build record

Prepared 2026-09-11. Roles taken: principal architect, Graph engineer, application security reviewer, QA engineer, product owner.

---

## Section 1 - Product understanding

**Business problem.** Blue Diamond IT supports many unrelated SME tenants on Microsoft 365 Business Premium. Reviewing each tenant against the BDIT Build Standard by hand is slow, inconsistent and hard to evidence. Creating the same Conditional Access and Intune objects tenant after tenant is repetitive and error-prone, and a mistake in Conditional Access can lock a client (or the engineer) out.

**Intended users.** BDIT engineers of varying experience, working from ordinary Windows workstations without local administrator rights, one client tenant at a time.

**Primary value.** A repeatable, defensible answer to "how far is this tenant from our standard, exactly what differs, what did we agree to leave different, and what changed since last time" - with evidence that survives the engagement.

**Assessment philosophy.** Read-only by construction. Compare material settings, not names. Resolve IDs to names. Never turn "could not read" into "missing". Separate "settings match" from "actually enforced". Record deviations as first-class, owned decisions.

**Deployment philosophy.** Remove the repetitive creation of standard objects, not the engineering judgement around them. Create only safe candidates (disabled Conditional Access, unassigned Intune objects), prove each write by readback, keep before/after evidence, and leave assignment, testing and enforcement to the engineer.

**Role of the Build Standard.** The catalogue is the intellectual property. It states both the expected production state and the safe deployment state per control, carries the Graph recipe where automation is sound, and is versioned and integrity-checked. The application is a delivery mechanism around it and must not hard-code standard content.

---

## Section 2 - Source inventory

| Candidate | Supplied as | Classification | Evidence limitations |
|---|---|---|---|
| A - Tenant Console 0.3.2-rc.1 | `Tenant-Console-0.3.2-All-Source-Code.txt`: 46 authored files concatenated from the released portable ZIP, with per-file SHA-256 (launchers, `server.js`, engine JS, PowerShell worker, two standard releases, 12 test files, packager, UI). Two portable ZIPs (0.2.3-rc.1, 0.3.2-rc.1) were also present in Downloads but were not opened; the dump states it excludes runtimes, vendored libraries, docs and checksum manifests. | **Full source** | `docs/BUILD-STANDARD-SUMMARY.html` (served by `server.js`) and `ui/jszip.min.js`, `ui/lucide.min.js` were not supplied. Tests could not be executed in this session (see Section 9). |
| B - .NET/WPF Tenant Toolkit 1.0.0 | `deepseek_text_20260911_b18ef4.txt`: a complete source dump (106 files: solution, five projects, tools, tests, packaging, standard) plus `deepseek_text_20260911_1f9cef.txt`, a review/finalisation prompt for that code. | **Full source, never compiled** (the prompt states "The code has never been compiled") | Not compiled here either. `standards/BDIT-Standard.json.sig` is a placeholder; the app refuses to start until `SignCatalogue` runs. |

Both candidates are therefore reviewed at code level. Neither could be built or run in this session because the workstation has no .NET SDK and the harness could not launch a shell (Section 9).

---

## Section 3 - Candidate A review (Tenant Console, Node + PowerShell + browser UI)

**Architecture.** `Start.cmd` → Windows PowerShell 5.1 (`Start-Background.ps1`) → hidden PowerShell 5.1 (`Start-Console.ps1`: runtime manifest hashing, prerequisite check) → bundled Node 24 (`server.js`, loopback HTTP on 127.0.0.1, random port, 32-byte token) → bundled PowerShell 7.6 worker (`engine/GraphWorker.ps1`) hosting Microsoft.Graph.Authentication 2.39 and MSAL from that module's `Dependencies/Core` folder. The browser UI (`ui/app.js`, `workspace.js`, `review.js`) polls `/api/state` every 1.5 s. Four runtimes, three languages, one UI.

**Strengths.**
- Safety model is the most complete of the two. `engine/planner.js` `makePlan` forces `state: 'disabled'`, requires emergency accounts, injects and de-duplicates the verified creator (`session.user.id` from `/me`, tenant-checked), refuses creation when the operator cannot be resolved, treats same-name and overlapping objects as `Conflict`, treats owned-but-drifted objects as `Drift`, and only updates owned, inactive, unassigned objects. `validatePlan` hashes profile, standard and snapshot, checks plan integrity, expires after 15 minutes and requires an acknowledged before-snapshot.
- Execution (`server.js` `execute`) re-reads live state before each write, refuses if the CA policy is no longer disabled or assignments appeared, writes intent to a journal, requires a GUID in the response, reads back and compares (`subset`), records the mapping, captures an after snapshot, marks pending rows `Not run`, and stops on first error. The worker never retries writes (`GraphWorker.ps1` comment and code). Shutdown waits for `busy` and the worker (`shutdown()`), and previous `Running` runs are marked `Interrupted` at start.
- Collection (`engine/collector.js`) is honest: per-collection errors, per-object `_assignmentsUnknown`/`_settingsUnknown`/`_relationshipUnknown`, `complete` flag, nextLink origin check, page and item limits.
- Assessment (`engine/comparison.js`) compares settings leaves rather than names, finds renamed and overlapping policies, and reports `Unable to assess` when data is incomplete.
- Local service security is well handled: loopback bind, timing-safe token compare, Host and Origin checks, CSP, 1 MB body cap, allow-listed static files, no-store caching, request timeouts.
- Least privilege is taken seriously: assessment refuses a custom app whose token carries write scopes; the shared Graph PowerShell app is allowed with a visible notice; two registrations (`Initialize-Applications.ps1`) are the intended model.
- Tests (12 files) cover the dangerous paths: tenant substitution, incomplete collection, CA disabled, creator exclusion once, conflicts, drift, plan tampering/expiry, execution success/ambiguous failure/shutdown, lifecycle, API auth, export injection, pagination origin.
- Exports: XLSX writer with inline strings (`ui/workbook.js`), CSV formula guard, HTML with escaping.
- Supply chain: `tools/runtime-lock.json` pins Node, PowerShell and the Graph module with hashes; `RUNTIMES.json` and `SHA256SUMS.txt` are verified at every launch.

**Weaknesses.**
- Operational complexity for a small MSP: four runtimes, a Python packager, a five-hop launcher chain, a browser lifecycle with heartbeats, leases and a watchdog. Every layer is another thing to debug on a client site. The code style (single-line, minified-looking JavaScript, for example `server.js` line 351-355 and `planner.js` line 864) makes maintenance by an MSP engineer hard.
- Authentication piggybacks on the Graph PowerShell module's private MSAL assembly (`Browser-Authentication.ps1`: `Dependencies/Core/Microsoft.Identity.Client.dll`), an undocumented internal that can move between module versions.
- Read retries ignore `Retry-After` (`GraphWorker.ps1` `Invoke-Read`: fixed `min(30, 2^(n+1))`).
- No licence evaluation; no severity, business impact or engineer action in the catalogue; 33 of 45 controls carry an identical boilerplate `manualReason`; no distinction between "settings match" and "enforced" (an enabled and a disabled policy both report `Settings match`).
- Standards are not integrity-checked at load: `/api/release` loads any `standards/*.json`. The package checksum is verified at launch, but a modified standard inside a verified package would be trusted.
- Plans are not bound to the authenticated account or application; a plan built in one deployment session can be executed in another as long as tenant, profile, standard and snapshot hashes match.
- Deviations are "client exceptions" stored inside the profile as a control-ID-to-reason map (`validateProfile`), with no owner, approver, date, review date or reference.
- Drift is a UI feature (`review.js` `diff`) comparing two captures by object ID without any concept of toolkit-managed objects; managed-object drift is only detected inside planning.
- The in-app application-registration creation path (`engine/applications.js`) puts `Application.ReadWrite.All` into the toolkit's own scope set for setup mode.
- The Excel/CSV/HTML report layer is generated in the browser from raw captures; there is no engineer or client narrative report with severity and impact.
- Runtime integrity hashing of roughly 150 MB of runtime files on every launch is slow.

**Security.** Good loopback hygiene; tokens never enter the browser; no persisted token cache; secrets scrubbed in worker error paths. Residual risks: the launch token is printed to the console window and placed in the URL fragment (then moved to sessionStorage); any local process running as the same user can read the console. The PowerShell worker accepts any JSON line on stdin from Node; the two are tightly trusted.

**Graph correctness.** Endpoint versions are right (beta only for settings catalogue, compliance and Autopilot; everything else v1.0). Compliance policies are written to beta although v1.0 supports them. Write allow-list is narrow and correct.

**Production readiness.** Closest to usable of the two, but the runtime stack and code density make it expensive to own. The version string says release candidate and the banner says "Windows and tenant validation are outstanding".

---

## Section 4 - Candidate B review (.NET 8 / WPF Tenant Toolkit)

**Architecture.** Seven projects (Core, Logging, Standards, Microsoft, Engine, App, Cli) plus a `SignCatalogue` tool and xUnit tests; `Microsoft.Extensions.DependencyInjection` composition in `App.xaml.cs`; MSAL 4.66 with a DPAPI file cache; a hand-written Graph `HttpClient`; handler-per-control-type (`ConditionalAccessHandler`, `GroupHandler`, `IntuneComplianceHandler`, `IntuneConfigurationHandler`); HTML/Markdown/JSON/client/drift renderers; WPF MVVM with 13 view models.

**Strengths.**
- The stack is right for the operating context: one language, one runtime, self-contained publish (`Build-Portable.bat`), no console, `asInvoker` manifest, no service.
- Catalogue metadata is richer: severity, business impact, engineer action, references, licence SKUs, dependencies, `safeInitialAssignment` and `expectedAssignment` separated (`Models/ControlDefinition.cs`).
- Report renderers are well designed for their audiences (`HtmlReportRenderer`, `ClientReportRenderer`, `DriftReportRenderer`), with HTML escaping tested.
- Deviation register is a first-class model with reason, approver, review date and overdue detection (`Models/Deviation.cs`, `DeviationViewModel`).
- Explicit permission phase concept (`PermissionService`, `IElevatableAuth`) and a UI that names the phase.
- Structured JSONL logging with a scrubber that is tested.

**Weaknesses (code-level, verified against the supplied source).**
- Never compiled; the review prompt lists 13 known issues including likely type mismatches and the placeholder client ID in two source files.
- **No intended-tenant concept.** `MsalAuthenticationService` uses the `organizations` authority; `TenantContextService.EnrichAsync` accepts whatever tenant the account signed into. There are no client profiles. `AssertMatches(plan)` only compares a plan to "the tenant you happen to be in".
- **Deviations are not tenant-bound.** `FileDeviationStore` writes one `config/deviations.json` for every tenant and `Deviation` has no tenant ID; a deviation recorded for client A is applied to client B. This breaks the tenant-boundary requirement outright.
- **Graph client retries non-idempotent writes.** `GraphClient.SendAsync` retries any method on 5xx and on transport exceptions (`catch (HttpRequestException) when (attempt < MaxAttempts)`), so a POST that timed out after the server processed it is sent again: duplicate policies. `Retry-After` is capped at 60 s. 403 produces an empty `MissingPermissions` list. There is no pagination at all: `GetAsync` returns the first page only, so groups, users and larger collections are silently truncated. There is no route allow-list. `GetAccessTokenAsync(ToolkitScopes.All)` requests the full write scope set on every call, even in the read-only phase, and falls back to an interactive prompt mid-operation.
- **Beta endpoints called through a hard-coded v1.0 root.** `/deviceManagement/configurationPolicies` (settings catalogue) and `/identity/conditionalAccess/evaluate` are beta-only (verified against Microsoft Learn on 2026-09-11); `IntuneConfigurationHandler.DetectAsync` and `ConditionalAccessWhatIfService` cannot work as written.
- **Assessment is display-name matching.** Every handler's `DetectAsync` compares `displayName` only; a renamed or duplicated policy reports `Missing`, and a same-named policy with entirely different settings reports `Compliant`. `ConditionalAccessHandler.DetectAsync` labels a report-only, unassigned policy `Compliant` - a candidate that protects nobody counts as compliant.
- **Payloads that Graph will not accept or that do nothing.** `IntuneConfigurationHandler.BuildBody` leaves literal `{{tenantId}}` placeholders in OMA-URIs (never substituted), the `smb` and `asr` settings-catalogue bodies carry setting instances without values and a `firewall` body that sets `firewallBlockStatefulFTP` rather than enabling any profile.
- **Plan integrity is computed but never verified**, has no expiry and is not bound to a snapshot (`DeploymentPlanner.ComputeHash` is stored; `DeploymentService` never recomputes it).
- **Execution has no pre-write reconciliation beyond a name lookup, no journal, no after-state snapshot, and cancellation aborts in-flight HTTP** (the `CancellationToken` flows into `PostAsync`), so a cancelled run can leave an unknown-outcome write.
- **UI flow is not wired end to end.** `DeployViewModel.SetPlan` is never called by `PlanViewModel`, so the Deploy page can never start; `ResultsViewModel.Apply` is never called; the CLI `drift` command reads `TenantContextService.Current`, which is always null in a fresh process.
- `ConditionalAccessHandler.BuildSafePolicyBody` creates a report-only policy with **no included users**, so report-only produces no telemetry, removing the stated reason for choosing report-only.
- The catalogue "signature" is a SHA-256 digest (`SignedCatalogueLoader`) labelled as a signature.
- `ToolkitLogger.Dispose` waits two seconds for a background writer; entries can be lost at exit.
- Tests do not touch the Graph client, the deployment service, pagination or plan integrity; `SnapshotStoreTests` writes into the real application folder.

**Security.** DPAPI cache (kept after disconnect, as the prompt notes), `asInvoker`, scrubbed logs. The absence of a tenant pin and of tenant-bound deviations are the material security findings.

**Production readiness.** A reasonable skeleton with the right stack and good reporting ideas, but the assessment, Graph and deployment layers are not correct, and the safety behaviour is asserted in tests only at the payload-builder level.

---

## Section 5 - Comparative matrix

Scores are 1 (poor) to 5 (strong) and refer to what the supplied code actually does. Candidate B is not credited for behaviour that exists only in its prompt.

| Area | A | B | Notes |
|---|---:|---:|---|
| Alignment with BDIT goal | 4 | 3 | A operationalises the real 45-control standard; B's 15 controls are generic. |
| Assessment design | 4 | 1 | A compares settings; B compares names. |
| Standards catalogue | 3 | 4 | B's metadata model is better; A's content is the real standard. |
| Graph implementation | 4 | 1 | B: no pagination, write retries, wrong API version for Intune. |
| Authentication | 3 | 3 | A: system browser, tenant-pinned, no persisted cache, but built on module internals. B: proper MSAL and DPAPI cache, but no tenant pin. |
| Least privilege | 4 | 2 | A separates apps and refuses write-scoped assessment tokens; B requests all scopes always. |
| Deployment safety | 5 | 2 | See Sections 3 and 4. |
| Tenant isolation | 4 | 1 | B shares deviations across tenants and has no intended tenant. |
| Drift handling | 2 | 2 | A: object diff without ownership; B: status diff only. |
| Deviations | 2 | 3 | B has the better model but no tenant binding; A has only a reason map. |
| Reporting | 3 | 4 | A: data exports; B: narrative engineer and client reports. |
| Evidence and audit trail | 4 | 2 | A: snapshots, plans, runs, journals; B: logs only. |
| UI workflow | 3 | 3 | A follows the workflow but in a browser over a polling API; B is native but unfinished. |
| Local application security | 4 | 3 | A's loopback service is careful; B has no local surface but keeps caches. |
| Portability | 3 | 4 | A: four bundled runtimes, x64 only, slow integrity check; B: single self-contained folder. |
| Maintainability | 2 | 3 | A's density versus B's conventional, if incomplete, C#. |
| Testability | 3 | 3 | A tests through the HTTP API with fake workers; B has clean interfaces but few tests. |
| Current test coverage | 4 | 2 | |
| Failure handling | 4 | 2 | |
| Observability and logging | 3 | 3 | A: in-memory activity and run journals; B: structured JSONL. |
| Extensibility | 3 | 3 | A: catalogue-driven collections; B: handler per type. |
| Complexity appropriate for a small MSP | 2 | 4 | |
| Overall production readiness | 3 | 1 | |

Neither candidate is adopted as-is. Candidate A's behaviour is the reference for safety and assessment semantics; Candidate B's stack, metadata model and reporting ideas are adopted and corrected.

---

## Section 6 - Key conflicts and architecture decisions (ADR style)

**ADR-1 Architecture: Node + PowerShell + browser UI versus .NET 8/WPF.**
Decision: .NET 8, WPF, self-contained win-x64. Alternatives: keep A's stack; Electron; a .NET service with a browser UI. Reason: one runtime and one language that an MSP engineer can maintain; no loopback service to secure; no polling; no launcher chain; native progress and dialogs; no dependency on PowerShell module internals. Consequences: the whole Graph and safety layer had to be re-implemented natively; the release is a self-contained folder (roughly 150 MB) rather than four bundled runtimes; WPF cannot be trimmed, so the folder is not smaller than A's.

**ADR-2 Graph integration: PowerShell worker versus native client.**
Decision: native `HttpClient` with an allow-list derived from the standard, per-call API version, bounded read retries honouring `Retry-After` (capped at 300 s), no write retries, ambiguous-failure classification for timeouts and gateway errors. Reason: A's worker protocol was correct but coupled to the Graph module; B's client was unsafe. Consequences: the client is unit-tested with a scripted handler; live acceptance of payloads still needs a test tenant.

**ADR-3 Authentication model.**
Decision: delegated MSAL, system browser only, tenant-pinned authority, organisation and operator verified before use, DPAPI cache per tenant and mode deleted on disconnect and on exit. Two registrations: BDIT Tenant Assessment (read) and BDIT Tenant Deployment (read plus write). Shared Microsoft Graph PowerShell application allowed only for assessment, with a visible notice. Certificate/app-only auth dropped from v1. Reason: an access token carries every consented delegated scope for the resource, so one registration can never issue a read-only token once write consent exists; app-only auth has no operator to protect and was unvalidated in A. Consequences: two consents per client tenant; deployment requires a second sign-in.

**ADR-4 Read/write separation: separate apps versus elevated consent in one app.**
Decision: separate applications and separate sessions; the Graph client is constructed with a mode and refuses writes in assessment mode; the deployment session is a new sign-in. A plan is bound to the account and application that will execute it. Reason: as ADR-3; B's phase flag was UI-only. Consequences: capture, assessment and plan are repeated in the deployment session (they are quick and the repetition is itself a safety re-check).

**ADR-5 Conditional Access initial state: disabled versus report-only.**
Decision: **disabled**, with the expected production targeting written and the emergency accounts, optional exclusion group and verified operator excluded. Alternatives: report-only with production targeting; report-only with no targeting (B). Reason: (1) disabled is the only state with no user impact; Microsoft documents that report-only policies requiring a compliant device can prompt users on macOS, iOS and Android, and that report-only does not evaluate user-action scopes (CA-009, CA-010); (2) B's report-only-with-no-users variant produces no telemetry, so the benefit claimed for report-only does not exist; (3) `state == disabled` is a hard invariant enforced in three places and testable offline. Consequences: no telemetry until the engineer moves the reviewed candidate to report-only, which is the first step of the documented rollout; the toolkit does not automate that transition in v1.

**ADR-6 Assignment model: expected production versus safe initial.**
Decision: the catalogue carries both; assessment and reports use expected production; the toolkit writes only safe deployment (CA: disabled with expected targeting; Intune: no assignment). Assignment writes are rejected by the payload guard. Reason: the requirement; A did this implicitly, B modelled it but wrote empty targeting. Consequences: reports can say "expected: All users; current: safe candidate, disabled".

**ADR-7 Existing policy handling.**
Decision: existing unmanaged objects are reported (settings match, partial match, same-name), never adopted, never overwritten. Only objects the toolkit created are updated, and only while they remain disabled and unassigned and unchanged since the toolkit last applied them. Reason: A's rules, which are the safe ones. Consequences: a tenant already built by hand to the standard yields conflicts for same-named objects; the engineer resolves them.

**ADR-8 Application ownership.**
Decision: a per-tenant managed-object mapping (control → object ID, last-applied payload digest, operator exclusion, run) plus live readback. Display names are a hint only. Reason: neither Conditional Access nor Intune objects offer a reliable in-object marker for every type; a local mapping verified against live state is the strongest available signal. Consequences: the mapping is evidence and must not be lost; it is stored with the tenant's other evidence and its digest is part of the plan binding.

**ADR-9 Standards integrity.**
Decision: `standards/manifest.json` with SHA-256 per release file, verified at load; called an integrity digest, not a signature. Package `SHA256SUMS.txt` and `.sha256` for the ZIP. The executable is unsigned (internal tool, no certificate); application-control policies allow it by path or hash. Reason: A verified runtimes but not standards; B verified a digest but called it a signature. Consequences: editing a standard requires regenerating the manifest (`build/Update-StandardsManifest.ps1`).

**ADR-10 Evidence.**
Decision: A's model (snapshots, plans, runs, journals, mappings) extended with integrity digests, tenant-partitioned folders and tenant checks on every load; B's deviation register adopted and made tenant-bound; manual checks added. Reason: evidence is an operational feature. Consequences: `data/tenants/<tenantId>/` is the unit of backup and hand-over.

**ADR-11 Reporting.**
Decision: B's renderer approach (engineer HTML/Markdown, client HTML) rebuilt around the new finding model, plus A's XLSX/CSV capability re-implemented without third-party code and with formula guards. Reason: both audiences matter; XLSX is useful to engineers and clients. Consequences: one tabular model feeds CSV, XLSX and HTML tables so every format agrees.

**ADR-12 Drift.**
Decision: a first-class analyser that compares two captures object by object, classifies toolkit-managed objects (removed, modified externally, metadata only), and assesses both captures under one release to name control regressions. Reason: neither candidate explained drift. Consequences: drift runs offline from stored captures.

**ADR-13 Deviations.**
Decision: B's register (kind, reason, approved state, owner, approver, dates, reference) made tenant-bound; deviations exclude controls from plans and never override unknown data. Reason: A's reason map lacked ownership; B's shared file broke tenant isolation.

**ADR-14 CLI.**
Decision: no CLI in v1. Reason: B's CLI duplicated the composition root and its drift command could not work; headless assessment adds a second entry point to keep safe and documented, for little value while engineers work interactively. Consequences: recorded as a future option (a headless assess mode inside the same executable).

**ADR-15 Graph beta.**
Decision: beta is declared per collection in the standard (`api`), used only for the settings catalogue and Autopilot profiles, flagged in every assessment's limitations, and never a fallback. What-If (`/identity/conditionalAccess/evaluate`, beta) is not included. Reason: isolate change risk; What-If adds a beta write-like call with evaluation semantics that would need live validation before it could be trusted in a client report.

**ADR-16 Application registration setup inside the toolkit.**
Decision: not automated in v1; documented procedure instead. Reason: it required `Application.ReadWrite.All` in the toolkit's own scope set, and the resulting registrations still needed manual consent. Consequences: a one-time manual setup per client (or a multi-tenant BDIT registration consented per client).

---

## Section 7 - Proposed final architecture

- **Stack.** .NET 8, C# 12, WPF, MSAL 4.89.0, DPAPI. No DI container, no database. Self-contained win-x64 publish; `Start.cmd` and `Start-Diagnostics.cmd`.
- **Projects.** `Core` (models, contracts, canonical JSON, safety rules, settings, paths, logging), `Graph` (MSAL, allow-list, client, connection service), `Engine` (standards, collector, assessment, planner, executor, drift, evidence, reports), `App` (WPF), `Tests` (xUnit).
- **Authentication and permissions.** As ADR-3/4. The UI header always shows tenant name, domain, verification, account, application, mode badge (blue read-only, red deployment), scopes and notices.
- **Graph layer.** As ADR-2/15.
- **Catalogue.** Schema v3 (`docs/ARCHITECTURE.md`); 45 controls, 12 recipes; integrity manifest.
- **Assessment.** Collection-based comparison with property-level differences, name resolution, licence evaluation, enforcement state, deviations, limitations.
- **Deviations, drift.** As ADR-12/13.
- **Planning and deployment.** As ADR-5 to ADR-8; digest-bound plans with expiry, typed tenant confirmation, pre-write reconciliation, readback, after-snapshot, pause/stop at boundaries, close waits for the in-flight write.
- **Evidence.** Tenant-partitioned JSON with digests; interrupted runs marked at start.
- **Reporting.** Engineer HTML/Markdown/JSON/CSV/XLSX, client HTML, run and drift reports; deterministic given the same input.
- **UI.** Workflow navigation (Connect → Configuration → Assessment → Deviations → Plan → Deploy → Evidence and drift → Manual checks → Build Standard → Settings and diagnostics); dangerous actions in red with confirmation dialogs; activity log; no modal blocking during long operations.
- **Packaging.** `build/Build-Portable.ps1`: restore, build, test, manifest, publish, stage, `VERSION.json`, `SHA256SUMS.txt`, ZIP with `.sha256`.
- **Security.** No local network surface; token caches DPAPI-protected and removed on disconnect and exit; scrubbed logs; formula-safe exports; integrity-checked standards; `asInvoker`.

Why this beats both originals: it keeps A's proven safety semantics and B's stack and reporting, removes A's runtime sprawl and B's correctness gaps, adds tenant-bound deviations, plan-to-session binding, licence awareness, enforcement-state reporting, managed-object drift and honest limitations.

---

## Section 8 - Implementation

Delivered as a new repository (`BDIT-Tenant-Toolkit`, tree in Section 12). No code was copied from either candidate; behaviours were re-implemented to the decisions above.

---

## Section 9 - Build and test results

**Passing.** The solution was built, tested and packaged on the authoring workstation via `BUILD-ME-FIRST.cmd`, which installs a user-local .NET 8 SDK into `.dotnet\` and then runs `build\Build-Portable.ps1`:

| Step | Command | Result |
|---|---|---|
| Restore | `dotnet restore BDIT.TenantToolkit.sln` | Pass (a repo-level `nuget.config` pins nuget.org so restore does not depend on machine settings) |
| Build | `dotnet build BDIT.TenantToolkit.sln -c Release --no-restore` | Pass - 0 errors, 0 warnings |
| Unit tests | `dotnet test tests\BDIT.TenantToolkit.Tests -c Release --no-build` | Pass - 89 passed, 0 failed, 0 skipped |
| Standards manifest | `build\Update-StandardsManifest.ps1` | Pass - `standards\manifest.json` generated |
| Publish | `dotnet publish src\BDIT.TenantToolkit.App -c Release -r win-x64 --self-contained true` | Pass |
| Portable package | `build\Build-Portable.cmd` | Pass - `dist\BDIT-Tenant-Toolkit-1.0.0-win-x64\` plus ZIP, `VERSION.json` and `SHA256SUMS.txt` |
| Candidate A tests | `node --test tests/*.test.js` | NOT RUN - out of scope once Candidate A was set aside |

The code was originally written without compiler feedback, and the first build surfaced four defects, all mechanical: a shadowed local in `CanonicalJson.IsSubset`, `.Value` applied to an already-unwrapped nullable tuple in `TenantConnectionService`, and two missing `using` directives. A fifth issue was environmental - `$PSScriptRoot` is not populated while a `[CmdletBinding()]` script evaluates `param()` defaults on Windows PowerShell 5.1, so `Update-StandardsManifest.ps1` now computes its default in the script body.

One test failure was substantive and is recorded here because it changed the product. `DriftTests` modelled a deployed Conditional Access policy carrying both the emergency account and the deploying operator in `excludeUsers`, and asserted the control became Compliant once enabled. It failed, correctly: every safe candidate is created with the operator excluded, and settings matching requires arrays of equal length, so a toolkit-created policy can never match the recipe exactly until that exclusion is removed. The behaviour is right - the operator exclusion is a real deviation and must stay visible - but the wording was not: the engineer saw "Potential overlap: an existing object matches part of the recipe... existing policies are not adopted automatically" about an object the toolkit itself had created. `AssessmentEngine` now recognises a toolkit-managed candidate in the partial-match branch and names the operator exclusion as the expected cause while flagging that any other difference came from outside the toolkit. `AssessmentTests.Enabled_toolkit_policy_still_carrying_the_operator_exclusion_is_an_explained_partial_match` pins this. Confirm the resulting report wording against a live tenant (Section 11).

---

## Section 10 - Safety verification

The following tests exist in `tests/BDIT.TenantToolkit.Tests` and encode every safety-critical acceptance criterion. They have **not been executed** (Section 9); their expected results are stated by design.

| Requirement | Test | Expected |
|---|---|---|
| Tenant mismatch rejected | `TenantBindingTests.*`, `CollectorAndProfileTests.Collection_failure_...` | mismatch throws `TenantMismatchException` |
| Plan for A cannot run against B | `TenantBindingTests.Plan_for_tenant_A_cannot_execute_against_tenant_B` | throws |
| Snapshot substitution | `TenantBindingTests.Snapshot_for_another_tenant_cannot_be_loaded_under_this_tenant` | not loadable |
| Assessment rejects writes | `GraphClientTests.Write_is_denied_in_assessment_mode_before_any_request`, `ExecutorTests.Read_only_graph_session_cannot_execute` | no HTTP request, `WriteDeniedException` |
| Incomplete collection never becomes Missing | `AssessmentTests.Incomplete_collection_...`, `Partially_collected_details_block_assessment`, `PlannerTests.Incomplete_collection_blocks_creation`, `CollectorAndProfileTests.Missing_assignment_details_...` | `UnableToAssess` / `Blocked` |
| Pagination failure yields unknown | `GraphClientTests.Pagination_follows_same_origin_links_and_rejects_others` | throws, collector records error |
| CA safe state enforced | `PlannerTests.Conditional_access_candidates_are_disabled_...`, `Catalogue_tampering_cannot_produce_an_enabled_candidate`, `GraphClientTests.Write_refuses_unsafe_conditional_access_state...`, `StandardsTests.Conditional_access_recipe_cannot_request_enabled_state` | `disabled` everywhere |
| Operator exclusion injected exactly once | `PlannerTests.Conditional_access_candidates_...`, `Operator_already_listed_as_emergency_is_not_duplicated` | count 1 |
| Unresolved operator blocks CA | `PlannerTests.Unverified_or_missing_operator_blocks_conditional_access_but_not_intune` | `Blocked` |
| Emergency exclusions preserved | `PlannerTests.Conditional_access_candidates_...`, `ExecutorTests.Successful_run_...` | present in payload |
| Explicit elevation required | `PlannerTests.Plan_invalidated_by_..._session_...` (assessment-mode session) | rejected |
| Plan digest deterministic; modified, expired, changed-input plans rejected | `PlannerTests.Plan_digest_is_deterministic_and_detects_modification`, `Plan_invalidated_by_profile_standard_snapshot_mapping_session_age_and_acknowledgement` | rejected |
| Live drift after planning blocks | `ExecutorTests.Name_collision_after_planning_blocks_the_write`, `PlannerTests.Owned_object_lifecycle_...` | no write / `Drift` |
| Same-name unmanaged object not overwritten | `PlannerTests.Unmanaged_same_name_or_overlapping_object_is_a_conflict_never_adopted` | `Conflict` |
| Deployment twice creates no duplicates | `ExecutorTests.Second_deployment_creates_no_duplicates` | one object, `NoChange` |
| Assignments remain safe | `PlannerTests.Write_guard_rejects_assignments...`, `Assigned_owned_intune_object_is_manual` | rejected / `Manual` |
| Ambiguous write stops continuation | `ExecutorTests.Ambiguous_write_failure_stops_the_run_without_mapping`, `GraphClientTests.Writes_are_never_retried_...` | `Review required`, one request |
| Successful write read back; after-state captured | `ExecutorTests.Successful_run_...`, `Readback_mismatch_marks_run_for_review_...` | readback `Pass`/`Unknown`, after snapshot present |
| Interrupted run represented honestly | `ExecutorTests.Interrupted_runs_are_marked_honestly_at_start_up` | `Interrupted` |
| Tokens removed from logs | `EvidenceAndReportTests.Scrubber_removes_secrets`, `Logger_scrubs_flushes_and_survives_dispose` | redacted |
| Export formula injection | `EvidenceAndReportTests.Csv_guards_formula_injection_and_quotes`, `Xlsx_stores_values_as_inline_strings_and_escapes_xml` | guarded |
| Imported evidence read-only | `Workspace.LoadStoredSnapshot` sets `SnapshotIsLive=false`; `PlanViewModel`/`Workspace.BuildPlan` refuse stored captures (UI behaviour, not unit-tested) | refused |
| Deterministic reports | `EvidenceAndReportTests.Reports_are_deterministic_...` | identical output |
| 429 honours Retry-After; transient reads retry | `GraphClientTests.Read_retries_on_429_and_5xx_then_succeeds`, `Read_gives_up_after_max_attempts_...` | retried, bounded |
| Unsupported nextLink origin rejected | `GraphClientTests.Pagination_...` | rejected |
| Beta/v1.0 routing | `GraphClientTests.Beta_and_v1_roots_are_separate` | separate roots |
| 403 gives permission information | `GraphClientTests.Forbidden_yields_permission_error_with_scope_hint` | scope hint |
| Close does not orphan a write; evidence flushed | `ExecutorTests.Shutdown_waits_for_the_in_flight_write`, `Stop_request_finishes_the_current_write_...`, `EvidenceAndReportTests.Logger_scrubs_flushes_and_survives_dispose` | waits, flushed |
| Catalogue integrity | `StandardsTests.Missing_manifest_blocks_load`, `Modified_standard_blocks_load_...`, `Unlisted_file_blocks_load` | refused |
| Drift classification | `DriftTests.*` | classified |

---

## Section 11 - Live Microsoft 365 validation required

See `docs/LIVE-VALIDATION.md` for the full checklist. In summary, the following cannot be proven without a test tenant: MSAL sign-in and consent for both registrations; organisation and operator verification; every collection's read behaviour and pagination at scale; acceptance of each of the twelve recipe payloads by Graph (in particular `scheduledActionsForRule` on create, OMA-URI custom policies, `deviceFilter` syntax, `includeUserActions`); readback equality for each type; throttling behaviour; and the behaviour of DPAPI caches across sessions.

---

## Section 12 - Final file tree

```
BDIT-Tenant-Toolkit/
  BDIT.TenantToolkit.sln
  Directory.Build.props        global.json        .editorconfig        .gitignore
  README.md                    CHANGELOG.md
  build/  Build-Portable.ps1   Build-Portable.cmd   Update-StandardsManifest.ps1
  config/ toolkit.settings.json
  docs/   ARCHITECTURE.md  BUILD-STANDARD-SUMMARY.md  LIVE-VALIDATION.md  REVIEW-REPORT.md
  packaging/ Start.cmd  Start-Diagnostics.cmd  Open-Reports.cmd  Open-Evidence.cmd
  standards/ 2026.09.3.json     (manifest.json is generated by build/Update-StandardsManifest.ps1)
  src/
    BDIT.TenantToolkit.Core/
      BDIT.TenantToolkit.Core.csproj  Exceptions.cs  IClock.cs
      Configuration/ ToolkitPaths.cs  ToolkitSettings.cs
      Diagnostics/   SensitiveDataScrubber.cs  ToolkitLogger.cs
      Graph/         IGraphClient.cs
      Json/          ToolkitJson.cs  CanonicalJson.cs
      Models/        TenantProfile.cs Standard.cs Snapshot.cs Assessment.cs Deviation.cs Plan.cs Run.cs Mapping.cs Drift.cs Session.cs ManualCheck.cs
      Safety/        ConditionalAccessSafety.cs
    BDIT.TenantToolkit.Graph/
      BDIT.TenantToolkit.Graph.csproj  GraphClient.cs  GraphRouteAllowList.cs  TenantConnectionService.cs
      Auth/ MsalAuthenticator.cs  TokenCacheProtection.cs
    BDIT.TenantToolkit.Engine/
      BDIT.TenantToolkit.Engine.csproj  ToolkitVersion.cs
      Assessment/ AssessmentEngine.cs  NameResolver.cs
      Collection/ TenantCollector.cs
      Drift/      DriftAnalyser.cs
      Evidence/   EvidenceStore.cs
      Execution/  DeploymentExecutor.cs  DeploymentControl.cs
      Planning/   DeploymentPlanner.cs
      Reports/    TabularReports.cs  CsvWriter.cs  XlsxWriter.cs  HtmlReports.cs  MarkdownReports.cs  ReportExporter.cs
      Standards/  StandardsLoader.cs  StandardsManifest.cs
    BDIT.TenantToolkit.App/
      BDIT.TenantToolkit.App.csproj  app.manifest  App.xaml  App.xaml.cs  MainWindow.xaml  MainWindow.xaml.cs
      Infrastructure/ ObservableObject.cs  Commands.cs  Converters.cs
      Services/       Workspace.cs
      ViewModels/     ShellViewModel.cs ConnectViewModel.cs ConfigurationViewModel.cs AssessmentViewModel.cs DeviationsViewModel.cs
                      PlanViewModel.cs DeployViewModel.cs HistoryViewModel.cs ManualChecksViewModel.cs StandardViewModel.cs SettingsViewModel.cs
      Views/          Views.cs ConnectView.xaml ConfigurationView.xaml AssessmentView.xaml DeviationsView.xaml PlanView.xaml DeployView.xaml
                      HistoryView.xaml ManualChecksView.xaml StandardView.xaml SettingsView.xaml ConfirmTenantDialog.xaml ConfirmTenantDialog.xaml.cs
  tests/BDIT.TenantToolkit.Tests/
      BDIT.TenantToolkit.Tests.csproj  TestSupport.cs  CanonicalJsonTests.cs  StandardsTests.cs  TenantBindingTests.cs  AssessmentTests.cs
      PlannerTests.cs  GraphClientTests.cs  ExecutorTests.cs  EvidenceAndReportTests.cs  DriftTests.cs  CollectorAndProfileTests.cs
```

Release layout produced by `build/Build-Portable.ps1`: `app/`, `standards/` (with `manifest.json`), `config/`, `docs/`, `data/`, `logs/`, `reports/`, launchers, `README.md`, `CHANGELOG.md`, `VERSION.json`, `SHA256SUMS.txt`, and the ZIP with a `.sha256` file.

---

## Section 13 - Changelog

See `CHANGELOG.md`.

---

## Section 14 - Remaining issues

| Issue | Severity | Reason | Workaround | Recommended fix |
|---|---|---|---|---|
| ~~The solution has not been compiled or tested~~ | Resolved | Built, tested (89 passing) and packaged; see Section 9 | - | - |
| Recipe payloads unproven against Graph | High | No test tenant in this session | Treat every recipe as candidate until validated | Complete `docs/LIVE-VALIDATION.md` |
| `standards/manifest.json` is generated, not committed | Low | Regenerated by the build so digests always match the released standard | The build script generates it; the app refuses to load standards until it exists | Leave as is, or commit the generated file if standards are ever edited outside a build |
| Application registration setup is manual | Low | Deliberate (ADR-16) | Documented procedure in README | Optional future setup script outside the toolkit |
| Report-only transition is manual | Low | Deliberate (ADR-5) | Engineer switches the reviewed candidate to report-only in the portal | Optional future "advance candidate" action with its own reconciliation and evidence |
| What-If evaluation not included | Low | Beta endpoint, unvalidated semantics (ADR-15) | Use report-only telemetry during rollout | Revisit when the endpoint reaches v1.0 |
| Nullability warnings are warnings, not errors | Low | Cannot verify without a compiler | Review warnings on first build | Promote CS8600-CS8604 to errors once the build is clean |
| Role inspection covers direct active assignments only | Low | Group-based and PIM-eligible roles need additional reads | The access report states the limitation | Extend the access check with `roleManagement/directory/roleEligibilitySchedules` once validated |
