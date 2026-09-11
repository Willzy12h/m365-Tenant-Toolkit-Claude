using System.Text.Json.Nodes;

namespace BDIT.TenantToolkit.Core.Models;

/// <summary>
/// Records which tenant object the toolkit created for a control. Ownership is established by this mapping plus a
/// live readback, never by display name alone.
/// </summary>
public sealed class ManagedObjectMapping
{
    public string ControlId { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string Collection { get; set; } = "";
    public JsonObject? LastApplied { get; set; }
    public string LastAppliedDigest { get; set; } = "";
    public string Release { get; set; } = "";
    public string RunId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public OperatorExclusion? OperatorExclusion { get; set; }
}

public sealed class ManagedObjectMappings
{
    public string TenantId { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public Dictionary<string, ManagedObjectMapping> ByControl { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ManagedObjectMapping? Find(string controlId) => ByControl.TryGetValue(controlId, out var m) ? m : null;

    public bool IsManagedObject(string? objectId) =>
        objectId is not null && ByControl.Values.Any(m => string.Equals(m.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));

    public ManagedObjectMapping? FindByObject(string? objectId) =>
        objectId is null ? null : ByControl.Values.FirstOrDefault(m => string.Equals(m.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
}
