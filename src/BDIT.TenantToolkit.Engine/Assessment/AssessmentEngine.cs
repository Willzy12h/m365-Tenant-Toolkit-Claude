using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Core.Safety;
using BDIT.TenantToolkit.Engine.Collection;

namespace BDIT.TenantToolkit.Engine.Assessment;

/// <summary>
/// Compares a snapshot with a Build Standard release. Settings are compared property by property against every object
/// in the relevant collection, so renamed or duplicated policies are found, and nothing is inferred from missing data.
/// </summary>
public sealed class AssessmentEngine
{
    public const string LicenceCollectionKey = "licences";

    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.Ordinal)
    {
        "id", "displayName", "name", "description", "createdDateTime", "lastModifiedDateTime", "modifiedDateTime",
        "@odata.context", "@odata.etag", "version", "state", "roleScopeTagIds", "isAssigned", "settingCount",
        "creationSource", "priorityMetaData", "supportsScopeTags", "deviceManagementApplicabilityRuleOsEdition",
        "deviceManagementApplicabilityRuleOsVersion", "deviceManagementApplicabilityRuleDeviceMode", "templateId"
    };

    private readonly IClock _clock;
    private readonly string _toolkitVersion;

    public AssessmentEngine(IClock clock, string toolkitVersion)
    {
        _clock = clock;
        _toolkitVersion = toolkitVersion;
    }

    public AssessmentResult Assess(TenantSnapshot snapshot, StandardCatalogue standard, TenantProfile profile, ManagedObjectMappings mappings, IReadOnlyList<Deviation> deviations, string assessedBy)
    {
        if (!string.Equals(snapshot.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("The snapshot and the selected client profile belong to different tenants.");
        if (!string.Equals(mappings.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("The managed-object mapping belongs to a different tenant.");
        foreach (var d in deviations)
            if (!string.Equals(d.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
                throw new TenantMismatchException($"Deviation {d.Id} belongs to a different tenant.");

        var names = NameResolver.FromSnapshot(snapshot);
        var parameters = profile.Parameters.ToTemplateValues(profile.TenantId);
        var result = new AssessmentResult
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = snapshot.TenantId,
            TenantName = snapshot.TenantName,
            PrimaryDomain = snapshot.PrimaryDomain,
            ClientLabel = profile.Company,
            SnapshotId = snapshot.Id,
            CapturedAt = snapshot.CapturedAt,
            AssessedAt = Timestamps.Format(_clock.UtcNow),
            AssessedBy = assessedBy,
            Release = standard.Release,
            StandardDigest = standard.IntegrityDigest,
            ToolkitVersion = _toolkitVersion,
            SnapshotComplete = snapshot.Complete
        };

        foreach (var (key, def) in standard.Collections)
        {
            var status = snapshot.Collections.TryGetValue(key, out var capture)
                ? capture.Status != CaptureStatus.Collected ? "Not collected: " + (capture.Error ?? "unknown error")
                    : capture.DetailIncomplete ? "Partially collected (assignment or setting details missing)"
                    : capture.Count == 0 ? "Collected; no objects returned" : "Collected"
                : "Not present in this snapshot";
            result.CollectionStatus[def.Label] = status;
            if (!snapshot.Collections.TryGetValue(key, out var c) || !c.Usable)
                result.Limitations.Add($"{def.Label}: {status}. Controls depending on it are reported as unable to assess.");
        }
        if (snapshot.BetaCollections.Any())
            result.Limitations.Add("Beta Graph endpoints were used for: " + string.Join(", ", snapshot.BetaCollections.Select(k => standard.FindCollection(k)?.Label ?? k)) + ". Beta APIs can change without notice.");
        if (!string.Equals(snapshot.StandardRelease, standard.Release, StringComparison.OrdinalIgnoreCase))
            result.Limitations.Add($"The snapshot was captured under standard release {snapshot.StandardRelease}; it is being assessed against {standard.Release}. Collections added in the newer release may be absent.");

        var licence = LicenceEvaluator.FromSnapshot(snapshot);
        if (!licence.Available) result.Limitations.Add("Subscribed licences could not be read; licence requirements are not verified.");

        foreach (var control in standard.Controls)
        {
            var deviation = deviations.FirstOrDefault(d => string.Equals(d.ControlId, control.Id, StringComparison.OrdinalIgnoreCase));
            var finding = AssessControl(control, standard, snapshot, mappings, deviation, names, parameters, licence);
            result.Findings.Add(finding);
        }

        result.Summary = Summarise(result.Findings);
        return result;
    }

    private static ControlFinding AssessControl(ControlDefinition control, StandardCatalogue standard, TenantSnapshot snapshot, ManagedObjectMappings mappings,
        Deviation? deviation, NameResolver names, IReadOnlyDictionary<string, JsonNode?> parameters, LicenceEvaluator licence)
    {
        var finding = new ControlFinding
        {
            ControlId = control.Id,
            Name = control.Name,
            Category = control.Category,
            Severity = control.Severity,
            Collection = control.Collection,
            DesiredState = control.DesiredState,
            ExpectedProductionState = control.ExpectedProduction.State,
            ExpectedProductionAssignment = control.ExpectedProduction.Assignment,
            BusinessImpact = control.BusinessImpact,
            EngineerAction = control.EngineerAction,
            ManualInstructions = control.Assessment.ManualInstructions,
            Deviation = deviation
        };

        var mapping = mappings.Find(control.Id);
        if (mapping is not null)
        {
            finding.Owned = true;
            finding.OwnedObjectId = mapping.ObjectId;
        }

        if (deviation is { Kind: DeviationKind.NotApplicable })
        {
            finding.Status = FindingStatus.NotApplicable;
            finding.Reason = "Recorded as not applicable to this client: " + deviation.Reason;
            return finding;
        }

        if (control.Licence.ServicePlans.Count > 0)
        {
            if (licence.Available)
            {
                var missing = control.Licence.ServicePlans.Where(p => !licence.Has(p)).ToList();
                if (missing.Count > 0)
                {
                    finding.Status = FindingStatus.LicenceUnavailable;
                    finding.Reason = "Required service plan(s) not found in subscribed licences: " + string.Join(", ", missing) + (string.IsNullOrEmpty(control.Licence.Note) ? "" : ". " + control.Licence.Note);
                    return finding;
                }
            }
            else
            {
                finding.Notes.Add("Licence requirement (" + string.Join(", ", control.Licence.ServicePlans) + ") could not be verified because subscribed licences were not collected.");
            }
        }

        var def = standard.FindCollection(control.Collection);
        snapshot.Collections.TryGetValue(control.Collection ?? "", out var capture);

        if (def is null || control.Assessment.Mode == AssessmentMode.Manual || control.Payload is null)
        {
            finding.Status = FindingStatus.RequiresManualReview;
            finding.Reason = "Manual assessment: automated comparison is unavailable for this control. This does not mean a policy or setting is missing. Inspect the stated requirement against the captured configuration.";
            if (def is not null)
            {
                if (capture is null || capture.Status != CaptureStatus.Collected)
                    finding.Notes.Add($"The related collection ({def.Label}) was not collected, so nothing can be shown here.");
                else
                    finding.ObservedObjects = capture.Items.Select(i => ObjectLabel(i, def)).Take(40).ToList();
            }
            ApplyEquivalence(control, standard, snapshot, names, finding);
            if (deviation is not null)
            {
                finding.Status = FindingStatus.CompliantWithDeviation;
                finding.Reason = "Approved deviation recorded: " + deviation.Reason;
            }
            return finding;
        }

        if (capture is null || !capture.Usable)
        {
            finding.Status = FindingStatus.UnableToAssess;
            finding.Reason = capture is null
                ? $"The {def.Label} collection is not present in this snapshot. Absence cannot be inferred."
                : capture.Status != CaptureStatus.Collected
                    ? $"The {def.Label} collection could not be read ({capture.Error}). Absence cannot be inferred."
                    : $"Assignment or setting details are incomplete for {def.Label}. Absence and state cannot be inferred.";
            if (deviation is not null) finding.Notes.Add("An approved deviation is recorded, but it cannot be applied while the underlying data is unknown.");
            return finding;
        }

        JsonObject payload;
        try
        {
            payload = (JsonObject)CanonicalJson.Resolve(control.Payload, parameters)!;
        }
        catch (MissingParameterException ex)
        {
            finding.Status = FindingStatus.RequiresManualReview;
            finding.Reason = ex.Message;
            finding.Notes.Add("Automated comparison needs every client value referenced by the recipe.");
            if (capture.Items.Count > 0) finding.ObservedObjects = capture.Items.Select(i => ObjectLabel(i, def)).Take(40).ToList();
            return finding;
        }
        if (ConditionalAccessSafety.IsConditionalAccess(def)) ConditionalAccessSafety.EnforceSafeState(payload);
        finding.Proposed = payload;

        var candidates = FindCandidates(capture.Items, payload, control.Assessment, def, names, mappings);
        finding.Candidates = candidates.ToList();

        if (mapping is not null && !capture.Items.Any(i => string.Equals(i["id"]?.GetValue<string>(), mapping.ObjectId, StringComparison.OrdinalIgnoreCase)))
            finding.Notes.Add($"The object the toolkit created earlier ({mapping.ObjectId}) is no longer present in the tenant. Investigate before creating a replacement.");

        var exact = candidates.FirstOrDefault(c => c.SettingsMatch);
        if (exact is not null)
        {
            var enforced = ExpectedEnforcementMet(control, def, exact);
            if (enforced == true)
            {
                finding.Status = FindingStatus.Compliant;
                finding.Reason = $"Settings match '{exact.Name}' and it is {DescribeEnforcement(exact.Enforcement)}. Review overlapping policies and exclusions before accepting coverage.";
                if (deviation is not null) finding.Notes.Add("An approved deviation is recorded although the control is compliant; consider retiring the deviation.");
            }
            else if (enforced == false)
            {
                finding.Status = FindingStatus.SettingsMatchNotEnforced;
                finding.Reason = $"Settings match '{exact.Name}' but it is {DescribeEnforcement(exact.Enforcement)}. Expected production state: {control.ExpectedProduction.State} / {control.ExpectedProduction.Assignment}. Complete staged validation, then assign or enable it deliberately.";
            }
            else
            {
                finding.Status = FindingStatus.SettingsMatchNotEnforced;
                finding.Reason = $"Settings match '{exact.Name}' but its enforcement state could not be determined.";
            }
        }
        else if (candidates.Count > 0)
        {
            finding.Status = FindingStatus.PartialMatch;
            var managed = candidates.FirstOrDefault(c => c.ToolkitManaged);
            finding.Reason = managed is not null
                ? $"'{managed.Name}' is the object the toolkit created for this control, but its settings no longer match the recipe exactly. A safe candidate is created with the deploying operator excluded, so this is expected until that exclusion is removed; any other difference was made outside the toolkit. Review the differences before treating this control as covered."
                : candidates.Any(c => c.NameMatch && !c.SettingsMatch)
                    ? "An existing object uses the standard name but its settings differ. Review side by side; existing objects are never adopted or overwritten automatically."
                    : "Potential overlap: an existing object matches part of the recipe. Review side by side; existing policies are not adopted automatically.";
            // A toolkit-created object already has a precise explanation; for anything else, the equivalence test gives a
            // better answer than "part of the recipe matched" - it says which of the control's real conditions are met.
            if (managed is null) ApplyEquivalence(control, standard, snapshot, names, finding);
        }
        else
        {
            finding.Status = FindingStatus.Missing;
            finding.Reason = $"No object in {def.Label} matches the recipe. Equivalent settings across other policy types, or several policies together, still require manual review before this is treated as a gap.";
            // A client's own policy will not match the recipe property for property, so before reporting a gap, ask the
            // narrower question the equivalence signals encode: is this control covered by something already here?
            ApplyEquivalence(control, standard, snapshot, names, finding);
        }

        if (deviation is not null && finding.Status != FindingStatus.Compliant)
        {
            finding.Notes.Add("Underlying finding before the deviation was applied: " + finding.Status + " — " + finding.Reason);
            finding.Status = FindingStatus.CompliantWithDeviation;
            finding.Reason = "Approved deviation recorded: " + deviation.Reason;
        }
        return finding;
    }

    /// <summary>
    /// Runs the control's declared equivalence signals and, when an object satisfies every required one, reports a
    /// partial match naming that object instead of "Missing" or "Requires manual review".
    ///
    /// It never raises a finding to Compliant: equivalence shows that something in the tenant looks like it covers the
    /// control, which an engineer confirms (recording a deviation or a manual check). It also never lowers a finding -
    /// a control already assessed by settings comparison keeps that result.
    /// </summary>
    private static void ApplyEquivalence(ControlDefinition control, StandardCatalogue standard, TenantSnapshot snapshot, NameResolver names, ControlFinding finding)
    {
        var rule = control.Equivalence;
        if (rule is null || rule.Signals.Count == 0) return;

        var collectionKey = rule.Collection ?? control.Collection;
        var def = standard.FindCollection(collectionKey);
        if (def is null) return;
        if (!snapshot.Collections.TryGetValue(collectionKey ?? "", out var capture) || !capture.Usable)
        {
            finding.Notes.Add($"Equivalent configuration could not be looked for: the {def.Label} collection was not usable in this snapshot.");
            return;
        }

        var observations = EquivalenceEvaluator.Evaluate(rule, capture, def, names);
        finding.Equivalence = observations.Take(10).ToList();
        if (rule.Note.Length > 0) finding.Notes.Add("Equivalence test: " + rule.Note);

        var covered = observations.FirstOrDefault(o => o.Covered);
        if (covered is null)
        {
            if (observations.Count > 0)
                finding.Notes.Add($"{observations.Count} object(s) matched some but not all of the equivalence signals; the closest is '{observations[0].Name}'. Review before treating this control as a gap.");
            return;
        }

        var matched = string.Join(", ", covered.Signals.Where(s => s.Required && s.Matched).Select(s => s.Label));
        finding.Status = FindingStatus.PartialMatch;
        finding.Reason = $"Equivalent configuration observed in '{covered.Name}' ({covered.Collection}): {matched}. "
            + "This object does not match the Build Standard recipe property for property, and client policies rarely do. "
            + "Confirm it genuinely covers this control, then record an approved deviation or a manual check; it is not counted as compliant on its own.";
        if (covered.Enforcement is EnforcementState.Disabled or EnforcementState.ReportOnly or EnforcementState.Unassigned)
            finding.Notes.Add($"'{covered.Name}' is {DescribeEnforcement(covered.Enforcement)}, so it is not protecting anyone in its current state.");
        foreach (var caveat in covered.Caveats)
            finding.Notes.Add($"Qualifies the match on '{covered.Name}' - {caveat}");
        var others = observations.Count(o => o.Covered) - 1;
        if (others > 0) finding.Notes.Add($"{others} further object(s) also satisfied every signal; overlapping policies interact and must be reviewed together.");
    }

    /// <summary>true = enforced as expected; false = not enforced; null = unknown.</summary>
    private static bool? ExpectedEnforcementMet(ControlDefinition control, CollectionDefinition def, CandidateMatch candidate)
    {
        if (ConditionalAccessSafety.IsConditionalAccess(def))
            return candidate.Enforcement switch
            {
                EnforcementState.Enforced => true,
                EnforcementState.ReportOnly or EnforcementState.Disabled => false,
                _ => null
            };
        if (def.Assignments)
            return candidate.Enforcement switch
            {
                EnforcementState.Assigned => true,
                EnforcementState.Unassigned => false,
                _ => null
            };
        return true;
    }

    private static string DescribeEnforcement(EnforcementState state) => state switch
    {
        EnforcementState.Enforced => "enabled",
        EnforcementState.ReportOnly => "in report-only mode (not enforced)",
        EnforcementState.Disabled => "disabled",
        EnforcementState.Assigned => "assigned",
        EnforcementState.Unassigned => "unassigned",
        EnforcementState.NotApplicable => "in place",
        _ => "in an unknown state"
    };

    public static IReadOnlyList<CandidateMatch> FindCandidates(IReadOnlyList<JsonObject> items, JsonObject payload, AssessmentRule rule, CollectionDefinition def, NameResolver names, ManagedObjectMappings mappings)
    {
        var ignore = new HashSet<string>(IgnoredKeys, StringComparer.Ordinal);
        foreach (var extra in rule.IgnoreProperties) ignore.Add(extra);

        var target = Settings(payload, ignore) as JsonObject ?? new JsonObject();
        var wanted = CanonicalJson.Leaves(target).Where(l => l.Path != "@odata.type").ToList();
        var targetType = target["@odata.type"]?.GetValue<string>();
        var targetGrant = target["grantControls"];
        var standardName = payload[def.NameProperty]?.GetValue<string>() ?? "";
        var isConditionalAccess = ConditionalAccessSafety.IsConditionalAccess(def);

        var results = new List<CandidateMatch>();
        foreach (var item in items)
        {
            var id = item["id"]?.GetValue<string>() ?? "";
            var name = item[def.NameProperty]?.GetValue<string>() ?? item["displayName"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? id;
            var nameMatch = standardName.Length > 0 && string.Equals(name, standardName, StringComparison.OrdinalIgnoreCase);
            var typeMatch = targetType is null || string.Equals(item["@odata.type"]?.GetValue<string>(), targetType, StringComparison.OrdinalIgnoreCase);
            var grantMatch = targetGrant is null || CanonicalJson.IsSubset(item["grantControls"], targetGrant);

            if (wanted.Count == 0 && !nameMatch) continue;
            var differences = new List<PropertyDifference>();
            var matched = 0;
            foreach (var (path, value) in wanted)
            {
                var current = CanonicalJson.At(item, path);
                var match = CanonicalJson.IsSubset(current, value);
                if (match) matched++;
                differences.Add(new PropertyDifference
                {
                    Setting = path,
                    Current = current is null ? "Missing — not returned" : names.Render(current),
                    Standard = names.Render(value),
                    Match = match
                });
            }
            var settingsMatch = typeMatch && grantMatch && wanted.Count > 0 && matched == wanted.Count;
            var partial = typeMatch && grantMatch && wanted.Count > 1 && (double)matched / wanted.Count >= rule.PartialMatchThreshold;
            if (!settingsMatch && !partial && !nameMatch) continue;

            var candidate = new CandidateMatch
            {
                ObjectId = id,
                Name = name,
                SettingsMatch = settingsMatch,
                NameMatch = nameMatch,
                Matched = matched,
                Total = wanted.Count,
                State = item["state"]?.GetValue<string>(),
                Enforcement = Enforcement(item, def, isConditionalAccess),
                AssignmentSummary = names.AssignmentSummary(item),
                Differences = differences,
                ToolkitManaged = mappings.IsManagedObject(id)
            };
            results.Add(candidate);
        }
        return results.OrderByDescending(c => c.SettingsMatch).ThenByDescending(c => c.Score).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static EnforcementState Enforcement(JsonObject item, CollectionDefinition def, bool isConditionalAccess)
    {
        if (isConditionalAccess)
        {
            var state = item["state"]?.GetValue<string>() ?? "";
            return state.ToLowerInvariant() switch
            {
                "enabled" => EnforcementState.Enforced,
                "enabledforreportingbutnotenforced" => EnforcementState.ReportOnly,
                "disabled" => EnforcementState.Disabled,
                _ => EnforcementState.Unknown
            };
        }
        if (def.Assignments)
        {
            if (item[TenantCollector.AssignmentsUnknownKey] is not null) return EnforcementState.Unknown;
            if (item[TenantCollector.AssignmentsKey] is JsonArray a) return a.Count > 0 ? EnforcementState.Assigned : EnforcementState.Unassigned;
            return EnforcementState.Unknown;
        }
        return EnforcementState.NotApplicable;
    }

    /// <summary>Removes identity/metadata keys and toolkit annotations so only material settings are compared.</summary>
    public static JsonNode? Settings(JsonNode? node, ISet<string> ignore)
    {
        switch (node)
        {
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var i in arr) result.Add(Settings(i, ignore));
                return result;
            }
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var pair in obj)
                {
                    if (ignore.Contains(pair.Key) || pair.Key.StartsWith("_", StringComparison.Ordinal)) continue;
                    result[pair.Key] = Settings(pair.Value, ignore);
                }
                return result;
            }
            default:
                return node?.DeepClone();
        }
    }

    private static string ObjectLabel(JsonObject item, CollectionDefinition def)
    {
        var name = item[def.NameProperty]?.GetValue<string>() ?? item["displayName"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? item["id"]?.GetValue<string>() ?? "(unnamed)";
        var state = item["state"]?.GetValue<string>();
        var type = item["@odata.type"]?.GetValue<string>();
        var suffix = state is not null ? $" (state: {state})" : type is not null ? $" ({type.Replace("#microsoft.graph.", "", StringComparison.Ordinal)})" : "";
        return name + suffix;
    }

    public static AssessmentSummary Summarise(IReadOnlyList<ControlFinding> findings)
    {
        var s = new AssessmentSummary { Total = findings.Count };
        foreach (var f in findings)
        {
            switch (f.Status)
            {
                case FindingStatus.Compliant: s.Compliant++; break;
                case FindingStatus.CompliantWithDeviation: s.CompliantWithDeviation++; break;
                case FindingStatus.SettingsMatchNotEnforced: s.SettingsMatchNotEnforced++; break;
                case FindingStatus.PartialMatch: s.PartialMatch++; break;
                case FindingStatus.Missing: s.Missing++; break;
                case FindingStatus.UnableToAssess: s.UnableToAssess++; break;
                case FindingStatus.RequiresManualReview: s.RequiresManualReview++; break;
                case FindingStatus.LicenceUnavailable: s.LicenceUnavailable++; break;
                case FindingStatus.NotApplicable: s.NotApplicable++; break;
            }
            if (!f.IsActionable) continue;
            switch (f.Severity.ToLowerInvariant())
            {
                case "critical": s.CriticalActionable++; break;
                case "high": s.HighActionable++; break;
                case "medium": s.MediumActionable++; break;
                case "low": s.LowActionable++; break;
            }
        }
        return s;
    }
}

/// <summary>Reads subscribed SKUs from the snapshot and answers whether a service plan is provisioned.</summary>
public sealed class LicenceEvaluator
{
    private readonly HashSet<string> _plans = new(StringComparer.OrdinalIgnoreCase);
    public bool Available { get; private init; }

    public static LicenceEvaluator FromSnapshot(TenantSnapshot snapshot)
    {
        if (!snapshot.Collections.TryGetValue(AssessmentEngine.LicenceCollectionKey, out var capture) || capture.Status != CaptureStatus.Collected)
            return new LicenceEvaluator { Available = false };
        var evaluator = new LicenceEvaluator { Available = true };
        foreach (var sku in capture.Items)
        {
            if (sku["servicePlans"] is not JsonArray plans) continue;
            foreach (var plan in plans)
            {
                if (plan is not JsonObject p) continue;
                var name = p["servicePlanName"]?.GetValue<string>();
                var status = p["provisioningStatus"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(name) && !string.Equals(status, "Disabled", StringComparison.OrdinalIgnoreCase)) evaluator._plans.Add(name);
            }
        }
        return evaluator;
    }

    public bool Has(string servicePlan) => _plans.Contains(servicePlan);
}
