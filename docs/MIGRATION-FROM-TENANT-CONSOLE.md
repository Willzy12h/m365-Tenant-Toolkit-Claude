# Retiring the Tenant Console

`Willzy12h/m365-tenant-console` (Node + PowerShell, "Tenant Console 0.3.2-rc.1") is the predecessor of this toolkit. It is **reference, not a merge source**. The decision to rebuild rather than merge is recorded in `REVIEW-REPORT.md`; the short version is that stitching two LLM-built codebases together inherits the weaknesses of both, and the safety model here (token-level read/write separation, a write guard in three layers, ambiguous-write handling) could not be retrofitted onto the console's architecture.

The console stays available until this table is complete. Nothing is deleted from it; it is marked archived and read-only.

## Parity checklist

Everything the console does that this toolkit must do before the console can be retired. Tick an item only when it works here and has a test or a live-tenant validation behind it.

| # | Capability in the console | Status here | Notes |
|---|---|---|---|
| 1 | Read Conditional Access, Intune configuration, compliance, apps, app protection, enrolment, Autopilot, groups, users, authentication methods | Done | `TenantCollector`; per-collection failure recorded, never inferred as absent |
| 2 | Property-level comparison with the build standard, renamed and overlapping policy detection | Done | `AssessmentEngine`, plus equivalence signals for client-built policies |
| 3 | Deviation register | Done | Tenant-bound, never hides unknown data |
| 4 | Drift between visits | Done | `DriftAnalyser`, classifies toolkit-managed objects |
| 5 | Engineer and client reports | Done | HTML, Markdown, JSON, CSV, XLSX; formula-safe |
| 6 | Safe candidate creation | Done | Disabled CA, unassigned Intune, readback, after-snapshot |
| 7 | **`Initialize-Applications.ps1`** - creates the two app registrations and grants consent | **Not ported** | The highest-value item. See below |
| 8 | Browser-based UI | Not ported, not wanted | Replaced by the WPF window; the console's local HTTP service, loopback binding, token compare and CSP exist only because it was a web app. A native window removes that entire attack surface |
| 9 | Node test suite (`node --test`) | Superseded | 99 xunit tests here cover the same ground plus the safety invariants |
| 10 | Build standard 2026.09.2 content | Done | Carried into `standards/2026.09.3.json` as schema v3, enriched with severity, business impact, engineer action and licence |

### Item 7 is the one that matters

The console can create the application registrations; this toolkit cannot, and the engineer does it by hand in the portal. That is the single capability where the console is ahead, and it is exactly what `README.md` asks an engineer to do manually today.

It should be ported as a **separate one-time setup step, not a feature of the read-only app**. Creating app registrations needs `Application.ReadWrite.All`, which is far more powerful than anything else the toolkit uses; putting it behind the Connect page would undermine the read-only guarantee that the rest of the design exists to provide. Either a standalone script alongside `build/`, or a Setup page with its own explicit third sign-in, clearly outside assessment and deployment modes.

## Anything else worth taking

Before the console is archived, read it for:

- **Graph payloads that were validated against a real tenant.** Every recipe here is unproven until `LIVE-VALIDATION.md` is complete. A payload the console has actually created successfully is worth more than a carefully reasoned one.
- **Controls or checks in the 2026.09.2 standard that did not survive the port.** Compare control IDs directly.
- **Wording.** Client-facing report text that has already been in front of a client and worked.
- **Real settings-catalogue definition IDs.** Needed for equivalence signals on CFG-WIN-* and SEC-WIN-*, which are currently undraftable because the IDs are opaque GUIDs.

## Retirement

When every row above is Done and `LIVE-VALIDATION.md` is complete: archive the console repository on GitHub (Settings, Archive), leave a README pointing here, and record the retirement date in `CHANGELOG.md`. Do not delete it; it is the provenance for decisions in the review report.
