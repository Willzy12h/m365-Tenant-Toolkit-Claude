namespace BDIT.TenantToolkit.Core.Models;

/// <summary>An engineer-recorded outcome for a control that cannot be assessed automatically.</summary>
public sealed class ManualCheck
{
    public string ControlId { get; set; } = "";
    /// <summary>Pending, Pass, Fail or Unknown.</summary>
    public string Status { get; set; } = "Pending";
    public string Note { get; set; } = "";
    public string RecordedAt { get; set; } = "";
    public string RecordedBy { get; set; } = "";
}

public sealed class ManualCheckRegister
{
    public string TenantId { get; set; } = "";
    public Dictionary<string, ManualCheck> Checks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
