# Architecture and developer guide

## Stack

- .NET 8, C# 12, WPF (`net8.0-windows`), published self-contained for `win-x64`. No installer, no elevation, no machine-wide changes.
- Dependencies: `Microsoft.Identity.Client` (MSAL) and `System.Security.Cryptography.ProtectedData` (DPAPI). Everything else is the base class library: `System.Text.Json` for models and canonical hashing, `System.IO.Compression` for XLSX and CSV bundles.
- No DI container, no ORM, no database. Evidence is versioned JSON under `data\`.

## Projects

| Project | Responsibility |
|---|---|
| `BDIT.TenantToolkit.Core` | Models (profile, standard, snapshot, assessment, deviation, plan, run, mapping, drift, session), the `IGraphClient` contract, canonical JSON (`CanonicalJson`), the safety rules (`ConditionalAccessSafety`, `WritePayloadGuard`), paths, settings, logging and secret scrubbing. No I/O beyond files and no network. |
| `BDIT.TenantToolkit.Graph` | `MsalAuthenticator` (system browser, tenant-pinned authority, DPAPI cache), `GraphRouteAllowList`, `GraphClient` (v1.0/beta roots, bounded read retries, no write retries, payload guard), `TenantConnectionService` (verify organisation and operator, access check). |
| `BDIT.TenantToolkit.Engine` | `StandardsLoader` and `StandardsManifest`, `TenantCollector`, `AssessmentEngine` and `NameResolver`, `DeploymentPlanner`, `DeploymentExecutor`, `DriftAnalyser`, `EvidenceStore`, reports (`HtmlReports`, `MarkdownReports`, `TabularReports`, `CsvWriter`, `XlsxWriter`, `ReportExporter`). |
| `BDIT.TenantToolkit.App` | WPF shell. `Workspace` is the composition root and state machine; page view models are thin; views are XAML only. |
| `BDIT.TenantToolkit.Tests` | xUnit tests around every safety boundary with a scripted `FakeGraphClient` and a scripted `HttpMessageHandler`. |

Dependency direction: App → Engine → Graph → Core. Tests reference Core, Graph and Engine.

## Graph routing

- A collection in the standard declares `api` (`v1.0` or `beta`), `path`, `scope`, optional `write`, `assignments`, `children`, `relationship`, `singleton`, `nameProperty`.
- `GraphRouteAllowList.FromStandard` turns collections into read routes and, where `write` is present, write routes. Six fixed diagnostic routes are added (`/organization`, `/me`, `/roleManagement/directory/roleAssignments`, `/subscribedSkus`, `/users`, `/groups`).
- `GraphClient` refuses any path that is not on the list, and any write that is not the collection root (POST) or root/`{guid}` (PATCH) of a writable collection.
- Beta and v1.0 are separate roots chosen per call. `@odata.nextLink` must keep the same host, API version and route; otherwise pagination stops with an error and the collection is marked incomplete.
- Reads retry up to five attempts on 429, 500, 502, 503, 504 and transport errors, honouring `Retry-After` up to `maxRetryAfterSeconds` (300 by default). Writes are never retried; timeouts and gateway errors surface as `AmbiguousWriteException`, which stops the run.
- 403 becomes `PermissionException` with the collection's declared scope as the hint. 401 triggers one silent token renewal, then `AuthenticationRequiredException`; there is no interactive prompt mid-operation.

## Catalogue schema (version 3)

```json
{
  "schemaVersion": 3,
  "release": "2026.09.3",
  "status": "...", "description": "...", "publishedOn": "...", "owner": "...",
  "collections": { "<key>": { "api", "path", "scope", "write?", "assignments?", "children?", "relationship?", "singleton?", "label", "nameProperty?" } },
  "parameters": [ { "key", "label", "type": "guid|guidList", "required", "description" } ],
  "controls": [ {
    "id": "CA-001", "name", "category", "severity": "Critical|High|Medium|Low|Informational",
    "purpose", "desiredState", "businessImpact", "engineerAction", "documentationNotes",
    "licence": { "servicePlans": [ "AAD_PREMIUM" ], "note" },
    "collection": "<key or null>",
    "assessment": { "mode": "settings|manual", "manualInstructions", "ignoreProperties": [], "partialMatchThreshold": 0.5 },
    "expectedProduction": { "state", "assignment", "notes" },
    "safeDeployment": { "state", "assignment", "notes" },
    "payload": { ... Graph body template with {{parameter}} placeholders ... },
    "dependencies": [], "references": { "microsoft", "cis", "cyberEssentials" }
  } ]
}
```

Validation rules enforced by `StandardsLoader.Validate`: schema version, unique control IDs of the form `AAA-000` or `AAA-BBB-000`, known collections, `api` without a version in `path`, a payload only for settings-mode controls, a name property in every payload, no `assignments` in any payload, and for Conditional Access recipes `safeDeployment.state == "disabled"` and no payload state other than `disabled`.

Integrity: `standards\manifest.json` lists a SHA-256 digest per release file. `StandardsLoader.Load` refuses to load a file that is unlisted or modified. This is an integrity digest; it is not a signature. The tool is internal and unsigned; publish the ZIP checksum and, where application control is in use, allow the executable by path or hash.

## Assessment

`AssessmentEngine.Assess(snapshot, standard, profile, mappings, deviations)`:

1. Tenant binding is checked across every input.
2. Per control: not-applicable deviation, licence requirement (from the `licences` collection), manual mode, collection usability, template resolution (missing profile parameters produce *Requires manual review*, never *Missing*).
3. `FindCandidates` compares the resolved recipe with every object in the collection: identity and metadata keys are ignored, `@odata.type` and `grantControls` act as prefilters, every remaining leaf is compared with `CanonicalJson.IsSubset`. Objects that match fully, match at least the partial threshold, or share the standard name are returned with property-level differences and enforcement state.
4. Status: `Compliant` (settings match and enabled or assigned), `SettingsMatchNotEnforced`, `PartialMatch`, `Missing`, `UnableToAssess`, `RequiresManualReview`, `LicenceUnavailable`, `NotApplicable`, `CompliantWithDeviation`. A deviation never overrides `UnableToAssess`.

## Planning and execution

`DeploymentPlanner.Build` produces rows with actions Create, Update, NoChange, Blocked, Conflict, Drift, Manual, Deviation. Conditional Access payloads are forced to `disabled`, the emergency accounts, optional exclusion group and the verified operator are injected exactly once, and any earlier operator exclusion recorded in the mapping is preserved. Existing objects are matched by ownership mapping and live readback only; same-name or overlapping unmanaged objects are conflicts.

`DeploymentPlanner.Validate` recomputes the plan digest and checks tenant, session mode, account, application, profile, standard, snapshot, mapping, snapshot age, plan age, acknowledgement and the write guard on every row.

`DeploymentExecutor` runs one write at a time: preflight re-read (name and settings collisions for creates; ownership, drift, state and assignments for updates), journal the intent and payload digest, write, map, read back and compare, then capture the after-change snapshot. Ambiguous failures stop the run with status *Review required*; nothing is retried. `WaitForCompletionAsync` lets the host block exit until the in-flight write and evidence complete.

## Evidence layout

```
data\profiles.json
data\tenants\<tenantId>\snapshots\<stamp>-<id>.json      integrity digest
data\tenants\<tenantId>\assessments\<stamp>-<id>.json
data\tenants\<tenantId>\plans\<stamp>-<id>.json           plan digest
data\tenants\<tenantId>\runs\<stamp>-<id>.json            integrity digest
data\tenants\<tenantId>\runs\journal-<id>.jsonl
data\tenants\<tenantId>\managed-objects.json               ownership mapping
data\tenants\<tenantId>\deviations.json
data\tenants\<tenantId>\manual-checks.json
data\tenants\<tenantId>\msal-<mode>.cache                  DPAPI-protected, removed on disconnect and exit
```

Every load checks the embedded tenant ID against the requested tenant folder.

## Adding a control

1. Add the control to the release file with a new ID. For manual controls set `assessment.mode` to `manual` and write `manualInstructions`. For automated controls set `mode` to `settings`, name a collection and supply a `payload` template using only properties Graph accepts on create.
2. For Conditional Access recipes keep `state: "disabled"` and use `{{emergencyAccountIds}}` (or `{{emergencyAndGuestIds}}`) in `excludeUsers`.
3. Run `build\Update-StandardsManifest.ps1`, then the tests (`StandardsTests.Shipped_standard_release_is_valid` parses every shipped release).
4. Validate the payload against a test tenant before enabling deployment for that control; recipes are not proven by unit tests.

## Adding a collection

1. Add a `collections` entry with the correct API version, path, delegated read scope and (only if the toolkit should write to it) a write scope.
2. The collector, allow-list, access check and reports pick it up automatically. Name resolution uses the `groups`, `users`, `namedLocations`, `apps` and `licences` keys; keep those keys stable.
3. If the collection needs per-object detail, declare `assignments`, `children` or `relationship`.

## Testing

`dotnet test tests\BDIT.TenantToolkit.Tests -c Release` covers canonical JSON, catalogue validation and integrity, tenant binding, assessment statuses, planner safety, plan integrity, Graph client retry and guard behaviour, executor reconciliation, idempotency, ambiguous failures, stop and shutdown, evidence integrity, secret scrubbing, formula-safe exports, deterministic reports and drift classification. Tests never touch the network.

## Packaging

`build\Build-Portable.ps1` restores, builds, tests, regenerates the standards manifest, publishes the App self-contained, stages `standards`, `config`, `docs`, launchers, `README.md` and `CHANGELOG.md`, writes `VERSION.json` (versions, runtime, NuGet packages) and `SHA256SUMS.txt`, and zips the result with a `.sha256` file alongside. Only the build machine needs the .NET 8 SDK.
