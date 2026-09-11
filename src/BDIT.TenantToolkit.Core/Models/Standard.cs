using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BDIT.TenantToolkit.Core.Models;

public enum GraphApi { V1, Beta }

/// <summary>A versioned BDIT Build Standard release loaded from standards/&lt;release&gt;.json.</summary>
public sealed class StandardCatalogue
{
    public const int SupportedSchemaVersion = 3;

    public int SchemaVersion { get; set; }
    public string Release { get; set; } = "";
    public string Status { get; set; } = "";
    public string Description { get; set; } = "";
    public string PublishedOn { get; set; } = "";
    public string Owner { get; set; } = "";
    public Dictionary<string, CollectionDefinition> Collections { get; set; } = new(StringComparer.Ordinal);
    public List<ParameterDefinition> Parameters { get; set; } = new();
    public List<ControlDefinition> Controls { get; set; } = new();

    /// <summary>SHA-256 of the file bytes as verified against standards/manifest.json. Set by the loader, not part of the file.</summary>
    [JsonIgnore] public string IntegrityDigest { get; set; } = "";
    [JsonIgnore] public string SourceFileName { get; set; } = "";

    public ControlDefinition? FindControl(string id) =>
        Controls.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public CollectionDefinition? FindCollection(string? key) =>
        key is not null && Collections.TryGetValue(key, out var def) ? def : null;

    public IEnumerable<string> ReadScopes() =>
        Collections.Values.Select(c => c.Scope).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> WriteScopes() =>
        Collections.Values.Select(c => c.Write).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Describes one Microsoft Graph collection that the assessment reads.</summary>
public sealed class CollectionDefinition
{
    /// <summary>"v1.0" or "beta". Beta use is isolated per collection and flagged in evidence.</summary>
    public string Api { get; set; } = "v1.0";
    public string Path { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? Write { get; set; }
    public bool Assignments { get; set; }
    public string? Children { get; set; }
    public string? Relationship { get; set; }
    public bool Singleton { get; set; }
    public string Label { get; set; } = "";
    public string NameProperty { get; set; } = "displayName";
    /// <summary>
    /// False for Graph collections that reject OData query parameters (for example /subscribedSkus, which answers
    /// HTTP 400 UnsupportedQuery to <c>$top</c>). The access check then probes the collection without <c>$top=1</c>
    /// so a supported-query limitation is never reported as a permission failure.
    /// </summary>
    public bool SupportsQuery { get; set; } = true;

    [JsonIgnore] public GraphApi ApiVersion => string.Equals(Api, "beta", StringComparison.OrdinalIgnoreCase) ? GraphApi.Beta : GraphApi.V1;
    [JsonIgnore] public string BasePath => Path.Split('?')[0];
    [JsonIgnore] public bool Writable => !string.IsNullOrWhiteSpace(Write);
}

public sealed class ParameterDefinition
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>"guid" or "guidList".</summary>
    public string Type { get; set; } = "guid";
    public bool Required { get; set; }
    public string Description { get; set; } = "";
}

public sealed class ControlDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string DesiredState { get; set; } = "";
    /// <summary>Critical, High, Medium, Low or Informational.</summary>
    public string Severity { get; set; } = "Medium";
    public string BusinessImpact { get; set; } = "";
    public string EngineerAction { get; set; } = "";
    public string DocumentationNotes { get; set; } = "";
    public LicenceRequirement Licence { get; set; } = new();
    public string? Collection { get; set; }
    public AssessmentRule Assessment { get; set; } = new();
    public ExpectedProduction ExpectedProduction { get; set; } = new();
    public SafeDeployment SafeDeployment { get; set; } = new();
    public JsonObject? Payload { get; set; }
    /// <summary>Optional declared test for "is this control covered by whatever the tenant already has". See <see cref="EquivalenceRule"/>.</summary>
    public EquivalenceRule? Equivalence { get; set; }
    public List<string> Dependencies { get; set; } = new();
    public ControlReferences References { get; set; } = new();

    [JsonIgnore] public bool HasRecipe => Payload is not null && Assessment.Mode == AssessmentMode.Settings;
}

/// <summary>
/// Declares, as data rather than code, what an existing object must look like for a control to be considered covered
/// by equivalent configuration. Client tenants rarely use the standard's policy names, so coverage has to be judged
/// from values; these signals make that judgement explicit, reviewable and quotable in a report.
///
/// Equivalence never produces Compliant on its own. Every required signal matching yields a partial match that names
/// the object and the properties that satisfied each signal, for an engineer to confirm. Caveats (exclusions, narrow
/// scoping) are always reported alongside, because a policy that matches every signal but excludes half the tenant is
/// not coverage.
/// </summary>
public sealed class EquivalenceRule
{
    /// <summary>Collection to search. Defaults to the control's own collection.</summary>
    public string? Collection { get; set; }
    /// <summary>Explains to the engineer what "equivalent" means for this control, and what the signals cannot prove.</summary>
    public string Note { get; set; } = "";
    /// <summary>All signals marked required must match one single object before coverage is reported.</summary>
    public List<EquivalenceSignal> Signals { get; set; } = new();
    /// <summary>Conditions that weaken or qualify a match; reported whenever they are true, never used to reject one.</summary>
    public List<EquivalenceSignal> Caveats { get; set; } = new();

    [JsonIgnore] public IEnumerable<EquivalenceSignal> Required => Signals.Where(s => s.Required);
}

/// <summary>One checkable assertion about a captured object, addressed by dotted path.</summary>
public sealed class EquivalenceSignal
{
    public string Key { get; set; } = "";
    /// <summary>Engineer-facing wording, used verbatim in reports ("Targets all users").</summary>
    public string Label { get; set; } = "";
    /// <summary>Dotted path into the captured object, for example <c>conditions.users.includeUsers</c>.</summary>
    public string Path { get; set; } = "";
    public SignalOperator Operator { get; set; } = SignalOperator.Equals;
    /// <summary>Comparison value(s). Scalar for equals/contains; list for the *Any/*All operators.</summary>
    public JsonNode? Value { get; set; }
    /// <summary>Required signals must all match. Optional signals are reported but do not decide coverage.</summary>
    public bool Required { get; set; } = true;
    /// <summary>
    /// Optional name grouping alternative ways of satisfying the same requirement: required signals sharing a group
    /// are combined with OR, and the groups themselves with AND. Microsoft usually offers several routes to one
    /// outcome - MFA through <c>builtInControls</c> or through an authentication strength, for example - and a tenant
    /// that took the other route is still covered.
    /// </summary>
    public string? Group { get; set; }
}

public enum SignalOperator
{
    /// <summary>Scalar equality after normalisation.</summary>
    Equals,
    /// <summary>Scalar equals any listed value.</summary>
    EqualsAny,
    /// <summary>Array at the path contains the value.</summary>
    Contains,
    /// <summary>Array contains at least one listed value.</summary>
    ContainsAny,
    /// <summary>Array contains every listed value.</summary>
    ContainsAll,
    /// <summary>Path resolves to anything other than null.</summary>
    Present,
    /// <summary>Path is absent or null.</summary>
    Absent,
    /// <summary>Array or string at the path has at least one entry.</summary>
    NonEmpty,
    /// <summary>Array or string at the path is absent or empty.</summary>
    Empty,
    /// <summary>Numeric value is less than or equal to the comparison value.</summary>
    AtMost,
    /// <summary>Numeric value is greater than or equal to the comparison value.</summary>
    AtLeast
}

public sealed class LicenceRequirement
{
    /// <summary>Service plan names that must be present (for example AAD_PREMIUM, INTUNE_A). Empty means no licence dependency.</summary>
    public List<string> ServicePlans { get; set; } = new();
    public string Note { get; set; } = "";
}

public enum AssessmentMode { Settings, Manual }

public sealed class AssessmentRule
{
    public AssessmentMode Mode { get; set; } = AssessmentMode.Manual;
    public string ManualInstructions { get; set; } = "";
    /// <summary>Additional property names ignored when comparing settings (server-managed values, for example).</summary>
    public List<string> IgnoreProperties { get; set; } = new();
    /// <summary>Minimum fraction of recipe settings that must match before an existing object is reported as a partial match.</summary>
    public double PartialMatchThreshold { get; set; } = 0.5;
}

/// <summary>What a correctly configured BDIT tenant should ultimately look like. Documentation and assessment only; never written.</summary>
public sealed class ExpectedProduction
{
    /// <summary>For Conditional Access: enabled. For Intune objects: assigned.</summary>
    public string State { get; set; } = "";
    public string Assignment { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>What the toolkit is permitted to create before engineering validation.</summary>
public sealed class SafeDeployment
{
    /// <summary>For Conditional Access this is always "disabled"; for Intune objects always "unassigned".</summary>
    public string State { get; set; } = "";
    public string Assignment { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class ControlReferences
{
    public string Microsoft { get; set; } = "";
    public string Cis { get; set; } = "";
    public string CyberEssentials { get; set; } = "";
}
