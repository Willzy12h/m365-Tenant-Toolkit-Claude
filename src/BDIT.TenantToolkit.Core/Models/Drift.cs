namespace BDIT.TenantToolkit.Core.Models;

public enum DriftChange { Unchanged, Added, Removed, Changed, UnableToAssess }

public enum DriftClassification
{
    None,
    /// <summary>A toolkit-created object has been deleted from the tenant.</summary>
    ManagedObjectRemoved,
    /// <summary>A toolkit-created object differs from what the toolkit last applied.</summary>
    ManagedObjectModifiedExternally,
    /// <summary>A toolkit-created object changed only in properties the toolkit does not manage (metadata, server fields).</summary>
    ManagedObjectMetadataOnly,
    /// <summary>An object the toolkit does not manage was added, removed or changed.</summary>
    ExternalChange,
    /// <summary>The collection could not be compared because one capture is incomplete.</summary>
    Unknown
}

public sealed class DriftItem
{
    public string Collection { get; set; } = "";
    public string CollectionLabel { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public DriftChange Change { get; set; }
    public DriftClassification Classification { get; set; }
    public bool ToolkitManaged { get; set; }
    public string? ControlId { get; set; }
    public string Reason { get; set; } = "";
    public List<PropertyDifference> Differences { get; set; } = new();
}

public sealed class ControlStatusChange
{
    public string ControlId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public FindingStatus Before { get; set; }
    public FindingStatus After { get; set; }
    public bool Regression { get; set; }
    public string Explanation { get; set; } = "";
}

public sealed class DriftReport
{
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string BeforeSnapshotId { get; set; } = "";
    public string BeforeCapturedAt { get; set; } = "";
    public string BeforeStandardRelease { get; set; } = "";
    public string AfterSnapshotId { get; set; } = "";
    public string AfterCapturedAt { get; set; } = "";
    public string AfterStandardRelease { get; set; } = "";
    public string AssessedRelease { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public bool StandardReleaseChanged { get; set; }
    public List<DriftItem> Items { get; set; } = new();
    public List<ControlStatusChange> ControlChanges { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public int Added => Items.Count(i => i.Change == DriftChange.Added);
    public int Removed => Items.Count(i => i.Change == DriftChange.Removed);
    public int Changed => Items.Count(i => i.Change == DriftChange.Changed);
    public int ManagedAffected => Items.Count(i => i.ToolkitManaged && i.Change != DriftChange.Unchanged);
    public int Regressions => ControlChanges.Count(c => c.Regression);
}
