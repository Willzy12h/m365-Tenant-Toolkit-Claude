namespace BDIT.TenantToolkit.Core.Models;

public enum DeviationKind
{
    /// <summary>An approved departure from the standard; the control is reported as compliant with deviation.</summary>
    ApprovedDeviation,
    /// <summary>The control does not apply to this client (for example no Android estate).</summary>
    NotApplicable
}

/// <summary>An approved departure from the BDIT standard, bound to one tenant and one control.</summary>
public sealed class Deviation
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ControlId { get; set; } = "";
    public DeviationKind Kind { get; set; } = DeviationKind.ApprovedDeviation;
    public string Reason { get; set; } = "";
    public string ApprovedState { get; set; } = "";
    public string Owner { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public string RecordedAt { get; set; } = "";
    public string RecordedByAccount { get; set; } = "";
    /// <summary>ISO date (yyyy-MM-dd) when the deviation must be reviewed again. Empty means no expiry recorded.</summary>
    public string ReviewBy { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Notes { get; set; } = "";

    public bool IsReviewOverdue(DateTimeOffset now) =>
        DateOnly.TryParse(ReviewBy, System.Globalization.CultureInfo.InvariantCulture, out var date)
        && date < DateOnly.FromDateTime(now.UtcDateTime);
}
