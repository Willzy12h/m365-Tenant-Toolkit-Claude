using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BDIT.TenantToolkit.Core.Models;

public enum PlanAction
{
    /// <summary>Create a new safe candidate object.</summary>
    Create,
    /// <summary>Update an object the toolkit created earlier and that is still inactive.</summary>
    Update,
    /// <summary>The owned object already matches the recipe.</summary>
    NoChange,
    /// <summary>Automation blocked (incomplete data, missing parameter, unresolved operator).</summary>
    Blocked,
    /// <summary>An unmanaged object collides by name or settings; review required.</summary>
    Conflict,
    /// <summary>The owned object no longer matches what the toolkit last applied.</summary>
    Drift,
    /// <summary>No automated recipe; manual procedure.</summary>
    Manual,
    /// <summary>An approved deviation or not-applicable record excludes the control.</summary>
    Deviation
}

public sealed class OperatorExclusion
{
    public string ObjectId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string TenantId { get; set; } = "";
}

public sealed class PlanRow
{
    public string ControlId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Collection { get; set; } = "";
    public PlanAction Action { get; set; }
    public string Reason { get; set; } = "";
    public string? ObjectId { get; set; }
    public JsonObject? Payload { get; set; }
    public JsonObject? Before { get; set; }
    public OperatorExclusion? OperatorExclusion { get; set; }
    public string SafeState { get; set; } = "";
    public string ExpectedProductionState { get; set; } = "";
    public string ExpectedProductionAssignment { get; set; } = "";
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore] public bool IsWrite => Action is PlanAction.Create or PlanAction.Update;
}

/// <summary>An integrity-bound proposal. Every input that influenced it is digested so any change invalidates the plan.</summary>
public sealed class DeploymentPlan
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string ProfileDigest { get; set; } = "";
    public string Release { get; set; } = "";
    public string StandardDigest { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string SnapshotDigest { get; set; } = "";
    public string MappingsDigest { get; set; } = "";
    public string OperatorObjectId { get; set; } = "";
    public string OperatorAccount { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string ToolkitVersion { get; set; } = "";
    public List<PlanRow> Rows { get; set; } = new();
    /// <summary>SHA-256 over the canonical plan with this property blank. Recomputed before execution.</summary>
    public string PlanDigest { get; set; } = "";

    [JsonIgnore] public IEnumerable<PlanRow> WriteRows => Rows.Where(r => r.IsWrite);
}
