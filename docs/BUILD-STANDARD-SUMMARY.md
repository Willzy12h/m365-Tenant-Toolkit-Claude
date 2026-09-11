# BDIT Build Standard 2026.09.3 - summary

The catalogue in `standards\2026.09.3.json` is the standard. This page explains how to read it and, in particular, the difference between the **expected production state** and what the toolkit actually creates.

## Two states that must never be confused

| | Expected production state | Toolkit safe deployment |
|---|---|---|
| Meaning | What a correctly configured BDIT tenant looks like after staged validation | The only state the toolkit is permitted to create automatically |
| Conditional Access | `enabled`, targeting all users (or the stated group), emergency accounts and office location excluded | `disabled`, expected targeting written, emergency accounts, optional exclusion group and the signed-in operator excluded |
| Intune compliance and configuration | Assigned to the standard device or user group | Created with **no assignment** |
| Tenant-wide settings, connectors, applications | Configured as described in the control | Never automated; assessed or recorded as manual checks |

Reports state both, for example:

> Expected BDIT configuration: enabled, All users, emergency accounts excluded.
> Current deployment status: safe candidate, disabled, operator excluded.

## Why Conditional Access candidates are created disabled rather than report-only

- Disabled is the only state with zero user impact. Report-only policies are evaluated at every sign-in; Microsoft documents that report-only policies requiring a compliant device can prompt users on macOS, iOS and Android to pick a device certificate, and that report-only does not evaluate user-action scopes (security-info and device registration, CA-009 and CA-010).
- The invariant `state == disabled` is enforced three times (catalogue validation, planner, Graph client) and is testable without a live tenant.
- Report-only telemetry is valuable, so the rollout procedure moves a reviewed candidate to report-only as its first engineer-driven step.

## Rollout procedure for every automated control

1. **Create** the candidate with the toolkit (disabled or unassigned). Review the run report and readback status.
2. **Review** the object in the Entra or Intune portal: targeting, exclusions, settings, naming.
3. **Report-only / pilot.** Conditional Access: switch to report-only for at least seven days and review the sign-in log and policy impact. Intune: assign to the pilot group and confirm compliance and configuration state on representative devices.
4. **Enable / assign** to the expected production population.
5. **Verify** with a functional test (sign-in, device compliance, app protection prompt) and record the result on the Manual checks page.
6. **Remove the operator exclusion** once the emergency accounts and the rollout are proven. The toolkit records the exclusion in the managed-object mapping and reports it; it never removes it.

## Control families

| Family | Controls | Automated by the toolkit |
|---|---|---|
| Identity | ID-001 emergency access accounts, ID-002 authentication methods and TAP, ID-003 administrator access | No (assessment and manual review) |
| Conditional Access | CA-001 require MFA, CA-003 block legacy authentication, CA-004 block unsupported platforms, CA-005 compliant desktops, CA-006 compliant mobiles, CA-007 mobile app protection, CA-008 corporate mobile compliance, CA-009 secure security-info registration, CA-010 secure device registration | Yes: disabled candidates |
| Enrolment | ENR-001 automatic MDM enrolment, ENR-002 enrolment restrictions, ENR-003 Autopilot profile, ENR-004 Enrolment Status Page, ENR-005 Apple MDM certificate, ENR-006 managed Google Play | No |
| Compliance | CMP-001 no-policy default, CMP-WIN-001 Windows core compliance, CMP-WIN-002 Defender supplement, CMP-IOS-001 iOS compliance, CMP-AND-001 / CMP-AND-002 Android compliance | CMP-WIN-001 and CMP-IOS-001: unassigned candidates |
| Device configuration | CFG-WIN-001 BitLocker, CFG-WIN-002 LAPS, CFG-WIN-003 Windows Hello, CFG-WIN-004 core restrictions, CFG-WIN-005 OneDrive KFM, CFG-WIN-006 Edge, CFG-WIN-007 long paths | CFG-WIN-003: unassigned candidate |
| Endpoint security | SEC-WIN-001 antivirus, SEC-WIN-002 EDR, SEC-WIN-003 firewall | No (provider-specific) |
| App protection | MAM-IOS-001, MAM-AND-001 | No |
| Applications | APP-WIN-001 to APP-WIN-008 | No |
| Updates | UPD-001 Windows Autopatch | No |

Twelve recipes are automated. Every recipe requires validation against a real tenant before it is trusted; see `docs\LIVE-VALIDATION.md`.

## Client parameters

Recorded on the client profile and substituted into recipes:

| Parameter | Used by | Required |
|---|---|---|
| `emergencyAccountIds` | every Conditional Access recipe (`excludeUsers`) | Yes, before any Conditional Access candidate is created |
| `officeLocationId` | CA-001, CA-005, CA-006, CA-008, CA-009, CA-010 | For those recipes |
| `mamGroupId` | CA-006 (exclusion), CA-008 (inclusion) | For those recipes |
| `caExclusionGroupId` | every Conditional Access recipe (additional `excludeGroups`) | No |
| `pilotGroupId` | documentation of the pilot population | No |
| `tenantId` | CFG-WIN-003 OMA-URIs | Automatic |

## Licensing

Business Premium includes Entra ID P1 (`AAD_PREMIUM`), Intune Plan 1 (`INTUNE_A`), Defender for Business and Windows Autopatch. Controls declare the service plans they need; when the `licences` collection is readable the assessment reports *Licence unavailable* instead of *Missing* for controls the tenant cannot use.

## Deviations

A deviation is an approved, owned, dated departure from the standard for one client and one control. It changes the reported status to *Compliant (approved deviation)* or *Not applicable*. It never changes what the toolkit writes (deviated controls are excluded from plans) and never hides a collection that could not be read.
