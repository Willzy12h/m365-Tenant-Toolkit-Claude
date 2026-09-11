# Equivalent configuration: how signals work

## The problem

A client tenant that is already well configured will fail a name-based or recipe-based comparison almost every time. Their MFA policy is called "MFA for staff (set up by previous IT)", it targets all users and all cloud apps, it requires MFA — and it matches the BDIT recipe on none of the incidental properties the standard happens to specify. Reporting that as *Missing* is wrong and, worse, invites an engineer to create a duplicate policy.

Equivalence answers a narrower question that can actually be answered from a capture: **does an object exist whose values satisfy the conditions this control genuinely cares about?**

## The three rules

1. **Signals are catalogue data, not code.** Every condition is declared in the standard release file. You can read the whole test, change it, and version it with the standard. Nothing about "what counts as MFA" is buried in C#.
2. **Equivalence never reports Compliant.** A satisfied control becomes a *partial match* that names the object and quotes the properties that satisfied each condition. An engineer confirms it, then records an approved deviation or a manual check. Recognition is evidence for a decision, not the decision.
3. **Caveats are always shown.** A policy can satisfy every signal and still exclude half the tenant, be disabled, or apply to one client app type. Those facts appear on the finding whenever they are true. A matcher that hid them would be worse than no matcher at all.

Equivalence also never lowers a result: a control already assessed by settings comparison keeps that stronger outcome.

## Writing a signal

```json
"equivalence": {
  "note": "What this test does and does not prove. Required.",
  "signals": [
    { "key": "allUsers", "label": "Targets all users",
      "path": "conditions.users.includeUsers", "operator": "contains", "value": "All" },
    { "key": "mfaControl", "label": "Requires multi-factor authentication",
      "path": "grantControls.builtInControls", "operator": "containsAny", "value": [ "mfa" ], "group": "mfa" },
    { "key": "mfaStrength", "label": "Requires an authentication strength",
      "path": "grantControls.authenticationStrength", "operator": "present", "group": "mfa" }
  ],
  "caveats": [
    { "key": "excludedUsers", "label": "Excludes named users",
      "path": "conditions.users.excludeUsers", "operator": "nonEmpty" }
  ]
}
```

| Field | Meaning |
|---|---|
| `key` | Unique within the control. Identifies the signal in results. |
| `label` | Quoted verbatim in reports and in the app. Write it so a client-facing engineer can read it aloud. |
| `path` | Dotted path into the captured object, for example `conditions.users.includeUsers`. |
| `operator` | See below. |
| `value` | Comparison value. A list for the `*Any` / `*All` operators. |
| `required` | Defaults to `true`. Optional signals are shown but do not decide coverage. |
| `group` | Required signals sharing a group are alternatives (OR); groups combine with AND. |

### Operators

`equals`, `equalsAny`, `contains`, `containsAny`, `containsAll`, `present`, `absent`, `nonEmpty`, `empty`, `atMost`, `atLeast`.

String comparison is case-insensitive, because Graph is inconsistent about casing in values such as `All` and `iOS`.

### Groups

Microsoft usually offers more than one route to the same outcome. MFA can be required through `grantControls.builtInControls` or through an authentication strength; device trust through `compliantDevice` or `domainJoinedDevice`. Put the alternatives in the same `group` and a tenant that took the other route is still recognised.

## Guidance

- **Signal the outcome, not the shape.** "Requires MFA for all users and all cloud apps" is the control. Session lifetime, client app types and named locations usually are not — make those caveats.
- **Three to five required signals.** Fewer and you claim coverage from too little; more and no real tenant ever matches.
- **Anything you cannot interpret becomes a caveat.** The CA-008 device filter rule is free text: showing it and asking the engineer to read it is honest; parsing it would not be.
- **Assignment is a caveat, not a signal**, for Intune objects. An unassigned policy protects nobody, and the capture may not include assignments at all.
- **Write the `note` as a disclaimer.** It is validated as mandatory precisely because the limits matter as much as the test.

## Validation

The catalogue validator rejects a control whose equivalence block has no signals, no required signal, no note, a duplicate key, a missing label or path, an unknown collection, or an operator that needs a value and has none. A malformed signal fails the load; it is never silently skipped.

## Reviewing the result

The engineer report and the Excel export carry an equivalence table per control: condition, whether it was required, the expected test, the observed value and the result — plus a caveats sheet. The point is that you can disagree with the conclusion by reading the evidence beside it.
