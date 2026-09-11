# Live Microsoft 365 validation required

Unit tests prove the safety logic with a scripted Graph client. They cannot prove that Microsoft Graph accepts every payload or that permissions behave as documented. Complete the following in a BDIT test tenant before the toolkit is used against a client tenant. Record outcomes on the Manual checks page of the test tenant profile.

## Environment

1. A test tenant with Business Premium (Entra ID P1, Intune Plan 1) and at least two test users, one Windows device enrolled in Intune, one iOS device if available.
2. Two application registrations in the BDIT tenant: **BDIT Tenant Assessment** (read scopes listed in the standard plus `User.Read`, `Organization.Read.All`, `RoleManagement.Read.Directory`) and **BDIT Tenant Deployment** (read plus write scopes). Public client, redirect URI `http://localhost`. Admin consent granted in the test tenant.
3. Two emergency access accounts, one named location (office), one MAM-only group.

## Authentication and connection

| Check | Expected |
|---|---|
| Connect read-only with the assessment registration | Browser sign-in completes; header shows tenant name, primary domain, "verified", operator resolved and verified |
| Connect with the wrong tenant ID in the profile | Connection refused with a tenant mismatch message; no snapshot possible |
| Connect with an account lacking read roles | Access check lists the failing collections with the expected scope |
| Enable deployment access | Second sign-in with the deployment registration; mode badge turns red; write scopes observed |
| Disconnect, then reconnect | A fresh Microsoft sign-in is required (cache removed) |
| Token expiry during a long capture | Silent renewal works; if Microsoft requires interaction the operation fails with a clear reconnect message |

## Collection

| Check | Expected |
|---|---|
| Read tenant configuration | Every collection *Collected*; beta collections flagged in limitations; users and groups resolve names in reports |
| Revoke `DeviceManagementApps.Read.All` and capture again | Apps and app protection *Not collected*; dependent controls *Unable to assess*, never *Missing* |
| Tenant with more than one page of users | Pagination completes; count matches the portal |

## Assessment

| Check | Expected |
|---|---|
| Existing MFA policy named differently | CA-001 reports *Compliant* (if enabled) or *Settings match, not enforced* with the policy name |
| Policy with the standard name but different settings | *Partial match* with property-level differences |
| Approved deviation for a missing control | *Compliant (approved deviation)*; the underlying finding appears in the notes |

## Deployment (each recipe once)

For each of CA-001, CA-003, CA-004, CA-005, CA-006, CA-007, CA-008, CA-009, CA-010, CMP-WIN-001, CMP-IOS-001 and CFG-WIN-003:

| Check | Expected |
|---|---|
| Graph accepts the create payload | HTTP 201, object ID returned, readback *Pass* |
| Conditional Access object in the portal | State **Off**; emergency accounts and the operator in excluded users; expected include targeting present |
| Intune object in the portal | No assignments |
| Run the same plan again | Plan rows are *NoChange*; no duplicate objects |
| Edit the created object in the portal, then plan again | Row is *Drift*; the toolkit refuses to update it |
| Enable the created Conditional Access policy manually, then plan again | Row is *Manual*; the toolkit refuses to touch active policies |
| Create an unrelated policy with the standard name, then plan | Row is *Conflict*; nothing is adopted or created |

Known payload risks to verify explicitly: `scheduledActionsForRule` on compliance policy creation (v1.0), `osMinimumVersion` formats, OMA-URI custom policy acceptance for CFG-WIN-003, `deviceFilter` rule syntax for CA-008, `includeUserActions` for CA-009 and CA-010.

## Failure handling

| Check | Expected |
|---|---|
| Disconnect the network during a write | Run stops with *Review required*; the result is *Error* with configuration *Unknown*; no mapping recorded; after-change capture attempted; reassessment shows whether the object exists |
| Close the window during a run | Prompt explains the in-flight write completes; window closes after the after-change capture |
| 429 throttling during capture | Reads retry honouring `Retry-After`; log shows the wait |

## Reports

| Check | Expected |
|---|---|
| Engineer HTML and client summary | Names, not GUIDs, for groups, users and locations; client summary contains no raw JSON |
| Excel export opened in Excel | Every cell is text; a display name beginning with `=` does not execute |
