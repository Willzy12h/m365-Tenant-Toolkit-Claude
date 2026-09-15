# Working on this repository

Read this before changing anything. It applies to every contributor, human or model.

This toolkit reads and writes the Microsoft 365 configuration of real client tenants belonging to a UK MSP. A wrong change here does not produce a bug report; it produces a locked-out tenant or a false assurance that a client is protected when they are not. The design is deliberately conservative and the constraints below are not preferences.

## What this is

The BDIT Microsoft 365 Tenant Toolkit connects to one tenant at a time, reads its configuration, compares it with a versioned Build Standard, records approved deviations, detects drift between visits, and can create *safe candidate* objects that an engineer then validates, assigns and enables. Assessment is the primary capability. Deployment is secondary and intentionally limited.

Full context: `README.md` (what it does), `docs/ARCHITECTURE.md` (structure), `docs/REVIEW-REPORT.md` (why it is built this way, including the ADRs), `docs/EQUIVALENCE-SIGNALS.md` (how coverage is judged), `docs/LIVE-VALIDATION.md` (what is still unproven against a real tenant).

## Invariants

These are enforced in code and covered by tests. Do not weaken one to make something else easier; if you believe one is wrong, say so and leave it in place.

1. **Read-only by default.** An assessment session cannot write. The Graph client refuses writes outside a deployment session, before any HTTP request is made.
2. **Conditional Access candidates are created `disabled`.** Never enabled, never report-only. Enforced in three places: the catalogue validator, the planner and the Graph client.
3. **Intune objects are created unassigned.** A payload containing `assignments` is rejected.
4. **Nothing is deleted, adopted or overwritten.** An existing object is never claimed because its name looks right.
5. **Unknown never becomes missing.** A collection that failed to read produces *Unable to assess*, never *Missing*.
6. **Writes are never retried.** A timeout or 502/503/504 raises `AmbiguousWriteException`; the outcome is unknown and a human reconciles it.
7. **Every stored artefact is tenant-bound.** Snapshots, plans, runs, mappings and deviations carry a tenant ID that is checked on load.
8. **Tokens, secrets and request bodies are never logged.** Everything passes through the scrubber.
9. **The integrity digest is a digest, not a signature.** It detects modification; it does not prove origin. Say so wherever it is described.
10. **Equivalence never reports Compliant.** Recognising a client's own equivalent configuration produces an evidence-backed partial match for a human to confirm.

## Rules for changing things

- **Never change a test to make a suite pass.** If a test fails, decide whether the test or the code is wrong, fix that, and say which in the PR. A test asserting inconvenient behaviour is usually describing the product correctly. This has already happened once: see the operator-exclusion finding in `docs/REVIEW-REPORT.md` Section 9.
- **Safety behaviour needs a test.** Anything touching the invariants above ships with a test that would fail if the invariant were removed.
- **The Build Standard is data.** Adding a control, a collection or an equivalence signal is a `standards/*.json` change, not a code change. If you find yourself special-casing a control ID in C#, stop.
- **Never commit tenant evidence.** `data/`, `logs/`, `app/`, `dist/`, `portable/` and `.dotnet/` are ignored and CI fails if any becomes tracked. `config/toolkit.settings.json` ships with empty client IDs.
- **British English** in all user-facing text, comments and documentation: "licence" (noun), "organisation", "authorised". Graph property names keep Microsoft's spelling.
- **Say what is unproven.** Recipes that have not been validated against a live tenant are candidates, and the documentation must keep saying so until `docs/LIVE-VALIDATION.md` is complete.

## Build and test

```
dotnet build BDIT.TenantToolkit.sln -c Release
dotnet test tests/BDIT.TenantToolkit.Tests -c Release
```

### Working in a Linux cloud session

The whole solution except the WPF app targets `net8.0` and builds anywhere. `BDIT.TenantToolkit.App` targets `net8.0-windows` with WPF and needs Windows. The test project does **not** reference the app, so every safety invariant, the Graph client, the assessment engine, the planner, the executor and all the tests run on Linux:

```
dotnet build src/BDIT.TenantToolkit.Core src/BDIT.TenantToolkit.Graph src/BDIT.TenantToolkit.Engine -c Release
dotnet test tests/BDIT.TenantToolkit.Tests -c Release
```

That covers almost all of the work. Changes to the WPF app (`src/BDIT.TenantToolkit.App`, views, view models, XAML) cannot be compiled there — CI builds them on `windows-latest`, so open the pull request and let it verify. Do not claim a UI change builds if you could not build it.

No SDK? Run `BUILD-ME-FIRST.cmd`, which installs a user-local .NET 8 SDK into `.dotnet\` without administrator rights and runs the full pipeline, logging to `build\last-build.log`.

CI runs build, tests, Build Standard validation and a check that no evidence or client ID is committed. A pull request that has not gone green has not been verified, whatever its description claims.

## Start here if you are new to this repository

Read in this order. It takes about twenty minutes and will save you from proposing something the design already rejected on purpose.

1. `README.md` — what the product is and what it deliberately refuses to do.
2. This file's **Invariants** section above.
3. `docs/REVIEW-REPORT.md` sections 5 (the ADRs) and 9 (build results, including the one test that was wrongly edited and what it cost). The ADRs record decisions already argued through: Conditional Access `disabled` rather than report-only, two app registrations, no in-tool app setup, ownership by mapping plus live readback, digest not signature.
4. `docs/ARCHITECTURE.md` — project layout and how to add a control or a collector.
5. `docs/EQUIVALENCE-SIGNALS.md` — how a client's own differently named policy is recognised without claiming compliance.
6. `docs/LIVE-VALIDATION.md` — what is still unproven against a real tenant. This is the largest open risk in the product.
7. `docs/MIGRATION-FROM-TENANT-CONSOLE.md` — what the predecessor did, what is left to port, and why it is not a merge source.

The most useful things to work on are listed in `docs/LIVE-VALIDATION.md` and in the parity checklist of the migration document, not in the code.

## Working alongside other agents

Two agents work this product: Claude and Astra/Codex. The full protocol — pre-flight check, claiming work, branch ownership, merge order, conflict resolution — is `docs/integration/AGENT-COORDINATION.md` in `Willzy12h/M365-Buildstandards`. Read it before starting. What follows is what applies specifically here.

**This repository is Claude's preserved source.** Claude maintains it. Astra/Codex reads it — that is encouraged, it is half the point of keeping both sources — but does not rewrite it to match its own implementation. A change wanted here is raised as an issue or in the integration pull request, and its owner makes it.

- One change per branch, named `feat/…`, `fix/…` or `docs/…`. Open a pull request; do not push to `main`. An agent that is not this repository's owner branches under its own prefix (`astra/…`) and never pushes to a branch it does not own.
- Before starting, `git fetch` and read open pull requests here and in the integration repository. If one already touches the files you need, say so rather than producing a conflicting change.
- Open the pull request as a draft on your first commit. It is how the other agent learns you are here; nothing else tells it.
- State plainly in the PR what you verified and what you did not. "Tests pass" means you ran them and saw them pass.
- If you disagree with a decision recorded in `docs/REVIEW-REPORT.md`, raise it as an issue. Do not quietly reverse it. The same applies to `docs/integration/DECISION-LOG.md` in the integration repository.
- Review the other agent's pull requests and say what you think is wrong. Do not approve or merge them; a human does that.
