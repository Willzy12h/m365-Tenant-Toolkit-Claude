using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Core.Safety;
using BDIT.TenantToolkit.Engine.Assessment;
using BDIT.TenantToolkit.Engine.Collection;

namespace BDIT.TenantToolkit.Engine.Planning;

public sealed class PlanRequest
{
    public required TenantProfile Profile { get; init; }
    public required StandardCatalogue Standard { get; init; }
    public required TenantSnapshot Snapshot { get; init; }
    public required ManagedObjectMappings Mappings { get; init; }
    public required IReadOnlyList<Deviation> Deviations { get; init; }
    public required IReadOnlyList<string> SelectedControlIds { get; init; }
    public required TenantSession Session { get; init; }
}

public sealed class PlanValidationContext
{
    public required TenantProfile Profile { get; init; }
    public required StandardCatalogue Standard { get; init; }
    public required TenantSnapshot Snapshot { get; init; }
    public required ManagedObjectMappings Mappings { get; init; }
    public required TenantSession Session { get; init; }
    public string? AcknowledgedSnapshotId { get; init; }
    public required DateTimeOffset Now { get; init; }
    public TimeSpan MaxSnapshotAge { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan MaxPlanAge { get; init; } = TimeSpan.FromMinutes(20);
}

/// <summary>
/// Turns selected controls into an integrity-bound plan of safe candidate writes. The planner never adopts existing
/// objects by name, never proposes activation or assignment, and forces Conditional Access candidates to the disabled
/// state with the emergency accounts and the verified operator excluded.
/// </summary>
public sealed class DeploymentPlanner
{
    private readonly IClock _clock;
    private readonly string _toolkitVersion;

    public DeploymentPlanner(IClock clock, string toolkitVersion)
    {
        _clock = clock;
        _toolkitVersion = toolkitVersion;
    }

    public static string StandardDigest(StandardCatalogue standard) =>
        string.IsNullOrEmpty(standard.IntegrityDigest) ? CanonicalJson.Sha256Value(standard) : standard.IntegrityDigest;

    public static string SnapshotDigest(TenantSnapshot snapshot) =>
        string.IsNullOrEmpty(snapshot.IntegrityDigest) ? Evidence.EvidenceIntegrity.Compute(snapshot) : snapshot.IntegrityDigest;

    public static string ComputeDigest(DeploymentPlan plan)
    {
        var node = ToolkitJson.ToNode(plan) as JsonObject ?? throw new ConfigurationException("Plan must serialise to an object.");
        node["planDigest"] = "";
        return CanonicalJson.Sha256(node);
    }

    public DeploymentPlan Build(PlanRequest request)
    {
        var profile = request.Profile;
        var standard = request.Standard;
        var snapshot = request.Snapshot;
        var session = request.Session;
        if (!string.Equals(snapshot.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("Snapshot tenant does not match the selected client.");
        if (!string.Equals(session.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("The connected tenant does not match the selected client.");
        if (!string.Equals(request.Mappings.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("Managed-object mapping belongs to a different tenant.");

        var names = NameResolver.FromSnapshot(snapshot);
        var parameters = profile.Parameters.ToTemplateValues(profile.TenantId);
        var rows = new List<PlanRow>();
        foreach (var id in request.SelectedControlIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var control = standard.FindControl(id);
            if (control is null) continue;
            rows.Add(BuildRow(control, standard, snapshot, profile, request.Mappings, request.Deviations, session, names, parameters));
        }

        var plan = new DeploymentPlan
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = profile.TenantId,
            TenantName = snapshot.TenantName,
            ProfileId = profile.Id,
            ProfileDigest = CanonicalJson.Sha256Value(profile),
            Release = standard.Release,
            StandardDigest = StandardDigest(standard),
            SnapshotId = snapshot.Id,
            SnapshotDigest = SnapshotDigest(snapshot),
            MappingsDigest = CanonicalJson.Sha256Value(request.Mappings),
            OperatorObjectId = session.OperatorObjectId ?? "",
            OperatorAccount = session.Account,
            ClientId = session.ClientId,
            CreatedAt = Timestamps.Format(_clock.UtcNow),
            ToolkitVersion = _toolkitVersion,
            Rows = rows
        };
        plan.PlanDigest = ComputeDigest(plan);
        return plan;
    }

    private static PlanRow BuildRow(ControlDefinition control, StandardCatalogue standard, TenantSnapshot snapshot, TenantProfile profile,
        ManagedObjectMappings mappings, IReadOnlyList<Deviation> deviations, TenantSession session, NameResolver names, IReadOnlyDictionary<string, JsonNode?> parameters)
    {
        var row = new PlanRow
        {
            ControlId = control.Id,
            Name = control.Name,
            Collection = control.Collection ?? "",
            Action = PlanAction.Blocked,
            ExpectedProductionState = control.ExpectedProduction.State,
            ExpectedProductionAssignment = control.ExpectedProduction.Assignment,
            SafeState = control.SafeDeployment.State
        };
        foreach (var dep in control.Dependencies) row.Warnings.Add($"Depends on {dep}; confirm it is in place before enabling this control.");

        var deviation = deviations.FirstOrDefault(d => string.Equals(d.ControlId, control.Id, StringComparison.OrdinalIgnoreCase));
        if (deviation is not null)
        {
            row.Action = PlanAction.Deviation;
            row.Reason = (deviation.Kind == DeviationKind.NotApplicable ? "Recorded as not applicable: " : "Approved deviation: ") + deviation.Reason;
            return row;
        }

        var def = standard.FindCollection(control.Collection);
        if (def is null || !control.HasRecipe)
        {
            row.Action = PlanAction.Manual;
            row.Reason = string.IsNullOrWhiteSpace(control.Assessment.ManualInstructions) ? "No reviewed deployment recipe; follow the build standard manually." : control.Assessment.ManualInstructions;
            return row;
        }
        if (!def.Writable)
        {
            row.Action = PlanAction.Manual;
            row.Reason = $"The {def.Label} collection is assessed but not written by this release of the toolkit.";
            return row;
        }
        if (!snapshot.Collections.TryGetValue(control.Collection!, out var capture) || !capture.Usable)
        {
            row.Reason = $"The {def.Label} collection is unavailable or incomplete in the snapshot; absence cannot be inferred, so nothing will be created.";
            return row;
        }

        JsonObject payload;
        try
        {
            payload = (JsonObject)CanonicalJson.Resolve(control.Payload, parameters)!;
        }
        catch (MissingParameterException ex)
        {
            row.Reason = ex.Message;
            return row;
        }
        payload.Remove("id");
        payload.Remove("assignments");

        var isConditionalAccess = ConditionalAccessSafety.IsConditionalAccess(def);
        var mapping = mappings.Find(control.Id);
        if (isConditionalAccess)
        {
            ConditionalAccessSafety.EnforceSafeState(payload);
            if (profile.Parameters.EmergencyAccountIds.Count == 0)
            {
                row.Reason = "Emergency access account object IDs are required in the client profile before any Conditional Access candidate is created.";
                return row;
            }
            ConditionalAccessSafety.InjectUserExclusions(payload, profile.Parameters.EmergencyAccountIds);
            if (profile.Parameters.CaExclusionGroupId.Length > 0) InjectGroupExclusion(payload, profile.Parameters.CaExclusionGroupId);
            if (mapping?.OperatorExclusion is not null && ProfileValidator.IsGuid(mapping.OperatorExclusion.ObjectId))
                ConditionalAccessSafety.InjectUserExclusions(payload, new[] { mapping.OperatorExclusion.ObjectId });

            var operatorId = session.OperatorObjectId ?? "";
            if (!session.OperatorVerified || !ProfileValidator.IsGuid(operatorId) || !string.Equals(session.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                row.Reason = "The signed-in operator's object ID could not be verified in this tenant. Conditional Access candidates are not created without a verified operator exclusion. Reconnect and check permissions.";
                return row;
            }
            ConditionalAccessSafety.InjectUserExclusions(payload, new[] { operatorId });
            row.OperatorExclusion = new OperatorExclusion
            {
                ObjectId = operatorId,
                DisplayName = session.OperatorDisplayName,
                UserPrincipalName = session.OperatorUpn,
                TenantId = profile.TenantId
            };
            row.Warnings.Add($"The signed-in operator {session.OperatorUpn} [{operatorId}] is excluded from this policy to prevent lockout. The exclusion does not expire; remove it deliberately after testing.");
        }

        try { WritePayloadGuard.Assert(def, payload); }
        catch (SafetyViolationException ex) { row.Reason = ex.Message; return row; }

        var items = capture.Items;
        var nameKey = def.NameProperty;
        var proposedName = payload[nameKey]?.GetValue<string>() ?? "";
        var owned = mapping is null ? null : items.FirstOrDefault(i => string.Equals(i["id"]?.GetValue<string>(), mapping.ObjectId, StringComparison.OrdinalIgnoreCase));
        var sameName = items.Where(i => string.Equals(i[nameKey]?.GetValue<string>(), proposedName, StringComparison.OrdinalIgnoreCase)
                                        && (owned is null || !ReferenceEquals(i, owned))).ToList();
        row.Payload = payload;

        if (mapping is null && sameName.Count > 0)
        {
            row.Action = PlanAction.Conflict;
            row.Reason = $"An unmanaged object already uses the name '{proposedName}'. Review it; the toolkit never adopts objects by name and never creates a duplicate.";
            return row;
        }
        if (mapping is not null && owned is null)
        {
            row.Action = PlanAction.Conflict;
            row.Reason = $"The object the toolkit created earlier ({mapping.ObjectId}) is missing from the tenant. Investigate before replacing it.";
            return row;
        }
        if (mapping is null)
        {
            var overlapping = AssessmentEngine.FindCandidates(items, payload, control.Assessment, def, names, mappings)
                .Where(c => c.SettingsMatch || c.Score >= control.Assessment.PartialMatchThreshold).ToList();
            if (overlapping.Count > 0)
            {
                row.Action = PlanAction.Conflict;
                row.Reason = "A differently named object has overlapping settings (" + string.Join(", ", overlapping.Select(c => c.Name)) + "). Review the comparison; automatic adoption and duplicate creation are blocked.";
                return row;
            }
            row.Action = PlanAction.Create;
            row.Reason = isConditionalAccess
                ? "Create a disabled Conditional Access candidate with the expected targeting, emergency accounts and the signed-in operator excluded. Enabling remains a separate reviewed step."
                : "Create an unassigned candidate. Assignment remains a separate reviewed step.";
            return row;
        }

        var current = owned!;
        row.Before = current;
        if (isConditionalAccess && !string.Equals(current["state"]?.GetValue<string>(), ConditionalAccessSafety.SafeState, StringComparison.OrdinalIgnoreCase))
        {
            row.Action = PlanAction.Manual;
            row.Reason = "The toolkit-created policy is no longer disabled. Active Conditional Access policies are changed only through an explicitly scoped change procedure.";
            return row;
        }
        if (current[TenantCollector.AssignmentsUnknownKey] is not null || current[TenantCollector.SettingsUnknownKey] is not null || current[TenantCollector.RelationshipUnknownKey] is not null
            || (current[TenantCollector.AssignmentsKey] is JsonArray assignments && assignments.Count > 0))
        {
            row.Action = PlanAction.Manual;
            row.Reason = "The toolkit-created object is assigned, or its assignments could not be read. Only inactive, unassigned objects are updated automatically.";
            return row;
        }
        if (mapping.LastApplied is not null && !CanonicalJson.IsSubset(current, mapping.LastApplied))
        {
            row.Action = PlanAction.Drift;
            row.Reason = "The current settings differ from what the toolkit last applied. Something else changed this object; review before the toolkit touches it again.";
            return row;
        }
        if (!string.IsNullOrEmpty(def.Relationship) && !CanonicalJson.IsSubset(current, payload))
        {
            row.Action = PlanAction.Manual;
            row.Reason = "Updating this object's related settings (for example compliance actions) requires a separate procedure; review manually.";
            return row;
        }
        if (CanonicalJson.IsSubset(current, payload))
        {
            row.Action = PlanAction.NoChange;
            row.Reason = "The toolkit-created object already matches the recipe.";
            row.ObjectId = current["id"]?.GetValue<string>();
            return row;
        }
        row.Action = PlanAction.Update;
        row.ObjectId = current["id"]?.GetValue<string>();
        row.Reason = "Update the inactive, toolkit-created object to the current recipe. It stays " + (isConditionalAccess ? "disabled." : "unassigned.");
        return row;
    }

    private static void InjectGroupExclusion(JsonObject payload, string groupId)
    {
        if (payload["conditions"] is not JsonObject conditions || conditions["users"] is not JsonObject users) return;
        if (users["excludeGroups"] is not JsonArray groups)
        {
            groups = new JsonArray();
            users["excludeGroups"] = groups;
        }
        var present = groups.Any(g => g is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, groupId, StringComparison.OrdinalIgnoreCase));
        if (!present) groups.Add(groupId);
    }

    /// <summary>Throws <see cref="PlanValidationException"/> (or a tenant mismatch) unless the plan can still be executed safely.</summary>
    public static void Validate(DeploymentPlan plan, PlanValidationContext ctx)
    {
        if (!string.Equals(plan.TenantId, ctx.Profile.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(plan.TenantId, ctx.Snapshot.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(plan.TenantId, ctx.Session.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException($"Plan is bound to tenant {plan.TenantId}; the connected tenant, profile or snapshot differ. Execution blocked.");
        if (ctx.Session.Mode != SessionMode.Deployment)
            throw new PlanValidationException("Connect with deployment access before executing a plan.");
        if (!string.Equals(plan.OperatorObjectId, ctx.Session.OperatorObjectId ?? "", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(plan.ClientId, ctx.Session.ClientId, StringComparison.OrdinalIgnoreCase))
            throw new PlanValidationException("The authentication context changed since the plan was built (different account or application). Rebuild the plan.");
        if (plan.ProfileDigest != CanonicalJson.Sha256Value(ctx.Profile))
            throw new PlanValidationException("The client profile changed after the plan was built. Rebuild the plan.");
        if (plan.StandardDigest != StandardDigest(ctx.Standard))
            throw new PlanValidationException("The Build Standard changed after the plan was built. Rebuild the plan.");
        if (plan.SnapshotId != ctx.Snapshot.Id || plan.SnapshotDigest != SnapshotDigest(ctx.Snapshot))
            throw new PlanValidationException("The snapshot changed after the plan was built. Capture and review a new plan.");
        if (plan.MappingsDigest != CanonicalJson.Sha256Value(ctx.Mappings))
            throw new PlanValidationException("The managed-object mapping changed after the plan was built. Rebuild the plan.");
        if (ComputeDigest(plan) != plan.PlanDigest)
            throw new PlanValidationException("Plan integrity check failed: the plan content differs from its digest. Rebuild the plan.");
        if (!Timestamps.TryParse(ctx.Snapshot.CapturedAt, out var captured) || ctx.Now - captured > ctx.MaxSnapshotAge)
            throw new PlanValidationException($"The snapshot is older than {ctx.MaxSnapshotAge.TotalMinutes:0} minutes. Capture the tenant again and rebuild the plan.");
        if (!Timestamps.TryParse(plan.CreatedAt, out var created) || ctx.Now - created > ctx.MaxPlanAge)
            throw new PlanValidationException($"The plan is older than {ctx.MaxPlanAge.TotalMinutes:0} minutes. Rebuild it.");
        if (!string.Equals(ctx.AcknowledgedSnapshotId, ctx.Snapshot.Id, StringComparison.OrdinalIgnoreCase))
            throw new PlanValidationException("Review and acknowledge the before-change snapshot before deploying.");
        var writes = plan.WriteRows.ToList();
        if (writes.Count == 0) throw new PlanValidationException("The plan contains no supported changes.");
        foreach (var row in writes)
        {
            if (row.Payload is null) throw new PlanValidationException($"{row.ControlId}: write row has no payload.");
            var def = ctx.Standard.FindCollection(row.Collection) ?? throw new PlanValidationException($"{row.ControlId}: unknown collection '{row.Collection}'.");
            WritePayloadGuard.Assert(def, row.Payload);
            if (ConditionalAccessSafety.IsConditionalAccess(def))
            {
                if (row.OperatorExclusion is null || !ProfileValidator.IsGuid(row.OperatorExclusion.ObjectId))
                    throw new PlanValidationException($"{row.ControlId}: Conditional Access row has no verified operator exclusion.");
                var count = ConditionalAccessSafety.ExcludedUsers(row.Payload).Count(u => string.Equals(u, row.OperatorExclusion.ObjectId, StringComparison.OrdinalIgnoreCase));
                if (count != 1) throw new PlanValidationException($"{row.ControlId}: the operator exclusion must appear exactly once (found {count}).");
                if (!string.Equals(row.OperatorExclusion.ObjectId, ctx.Session.OperatorObjectId, StringComparison.OrdinalIgnoreCase))
                    throw new PlanValidationException($"{row.ControlId}: the plan excludes a different operator than the one signed in. Rebuild the plan.");
            }
            if (row.Action == PlanAction.Update && !ProfileValidator.IsGuid(row.ObjectId ?? ""))
                throw new PlanValidationException($"{row.ControlId}: update row has no valid object ID.");
        }
    }
}
