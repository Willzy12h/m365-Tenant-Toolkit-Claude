using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BDIT.TenantToolkit.Core.Models;

public enum FindingStatus
{
    /// <summary>Settings match and the object is in its expected production state.</summary>
    Compliant,
    /// <summary>An approved deviation is recorded for this control.</summary>
    CompliantWithDeviation,
    /// <summary>Settings match but the object is disabled, report-only or unassigned, so it is not yet protecting anyone.</summary>
    SettingsMatchNotEnforced,
    /// <summary>One or more existing objects partially match or share the standard name; manual equivalence review required.</summary>
    PartialMatch,
    /// <summary>No object in the collection matches the recipe. Equivalent coverage by other policy types still needs manual review.</summary>
    Missing,
    /// <summary>The collection or object details could not be read. Absence cannot be inferred.</summary>
    UnableToAssess,
    /// <summary>Control has no automated comparison; the engineer must review the captured configuration.</summary>
    RequiresManualReview,
    /// <summary>The required Microsoft licence/service plan is not present in the tenant.</summary>
    LicenceUnavailable,
    /// <summary>Recorded as not applicable to this client.</summary>
    NotApplicable
}

public enum EnforcementState { Unknown, Enforced, ReportOnly, Disabled, Assigned, Unassigned, NotApplicable }

public sealed class PropertyDifference
{
    public string Setting { get; set; } = "";
    public string Current { get; set; } = "";
    public string Standard { get; set; } = "";
    public bool Match { get; set; }
}

public sealed class CandidateMatch
{
    public string ObjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool SettingsMatch { get; set; }
    public bool NameMatch { get; set; }
    public int Matched { get; set; }
    public int Total { get; set; }
    public string? State { get; set; }
    public EnforcementState Enforcement { get; set; } = EnforcementState.Unknown;
    public string AssignmentSummary { get; set; } = "";
    public List<PropertyDifference> Differences { get; set; } = new();
    public bool ToolkitManaged { get; set; }
    [JsonIgnore] public double Score => Total == 0 ? 0 : (double)Matched / Total;
}

/// <summary>One captured object measured against a control's equivalence signals.</summary>
public sealed class EquivalenceObservation
{
    public string ObjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Collection { get; set; } = "";
    /// <summary>True when every required signal matched this object.</summary>
    public bool Covered { get; set; }
    public EnforcementState Enforcement { get; set; } = EnforcementState.Unknown;
    public List<SignalResult> Signals { get; set; } = new();
    /// <summary>Conditions that qualify the match, for example "Excludes 12 user(s)". Always shown to the engineer.</summary>
    public List<string> Caveats { get; set; } = new();

    [JsonIgnore] public IEnumerable<SignalResult> Unmet => Signals.Where(s => s.Required && !s.Matched);
}

/// <summary>The outcome of one signal, carrying the observed value so the claim can be checked rather than trusted.</summary>
public sealed class SignalResult
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Required { get; set; }
    public bool Matched { get; set; }
    public string Expected { get; set; } = "";
    public string Observed { get; set; } = "";
}

public sealed class ControlFinding
{
    public string ControlId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public FindingStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public string? Collection { get; set; }
    public string DesiredState { get; set; } = "";
    public string ExpectedProductionState { get; set; } = "";
    public string ExpectedProductionAssignment { get; set; } = "";
    public string BusinessImpact { get; set; } = "";
    public string EngineerAction { get; set; } = "";
    public string ManualInstructions { get; set; } = "";
    public List<CandidateMatch> Candidates { get; set; } = new();
    public JsonObject? Proposed { get; set; }
    public bool Owned { get; set; }
    public string? OwnedObjectId { get; set; }
    public Deviation? Deviation { get; set; }
    public List<string> ObservedObjects { get; set; } = new();
    /// <summary>Objects that satisfied the control's declared equivalence signals. Evidence for confirmation, never proof.</summary>
    public List<EquivalenceObservation> Equivalence { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    [JsonIgnore] public bool IsActionable => Status is FindingStatus.Missing or FindingStatus.PartialMatch or FindingStatus.SettingsMatchNotEnforced;
    [JsonIgnore] public EquivalenceObservation? BestEquivalence => Equivalence.FirstOrDefault(e => e.Covered);
    [JsonIgnore] public CandidateMatch? BestCandidate => Candidates.OrderByDescending(c => c.SettingsMatch).ThenByDescending(c => c.Score).FirstOrDefault();
}

public sealed class AssessmentSummary
{
    public int Total { get; set; }
    public int Compliant { get; set; }
    public int CompliantWithDeviation { get; set; }
    public int SettingsMatchNotEnforced { get; set; }
    public int PartialMatch { get; set; }
    public int Missing { get; set; }
    public int UnableToAssess { get; set; }
    public int RequiresManualReview { get; set; }
    public int LicenceUnavailable { get; set; }
    public int NotApplicable { get; set; }
    public int CriticalActionable { get; set; }
    public int HighActionable { get; set; }
    public int MediumActionable { get; set; }
    public int LowActionable { get; set; }
}

public sealed class AssessmentResult
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string PrimaryDomain { get; set; } = "";
    public string ClientLabel { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public string AssessedAt { get; set; } = "";
    public string AssessedBy { get; set; } = "";
    public string Release { get; set; } = "";
    public string StandardDigest { get; set; } = "";
    public string ToolkitVersion { get; set; } = "";
    public bool SnapshotComplete { get; set; }
    public List<string> Limitations { get; set; } = new();
    public Dictionary<string, string> CollectionStatus { get; set; } = new(StringComparer.Ordinal);
    public List<ControlFinding> Findings { get; set; } = new();
    public AssessmentSummary Summary { get; set; } = new();
}
