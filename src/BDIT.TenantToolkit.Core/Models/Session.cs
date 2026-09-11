namespace BDIT.TenantToolkit.Core.Models;

public enum SessionMode
{
    /// <summary>Read-only. The Graph client refuses every write and the token was requested from the assessment application.</summary>
    Assessment,
    /// <summary>Write-capable. Established only by an explicit engineer action using the deployment application.</summary>
    Deployment
}

/// <summary>The authenticated, verified connection to one tenant.</summary>
public sealed class TenantSession
{
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string PrimaryDomain { get; set; } = "";
    public string Account { get; set; } = "";
    public string AccountObjectId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientLabel { get; set; } = "";
    public SessionMode Mode { get; set; } = SessionMode.Assessment;
    public string AuthenticationType { get; set; } = "Delegated (system browser)";
    public List<string> Scopes { get; set; } = new();
    public bool TenantVerified { get; set; }
    public string? OperatorObjectId { get; set; }
    public string OperatorDisplayName { get; set; } = "";
    public string OperatorUpn { get; set; } = "";
    public bool OperatorVerified { get; set; }
    public string ConnectedAt { get; set; } = "";
    public string TokenExpiresAt { get; set; } = "";
    public List<string> Notices { get; set; } = new();

    public bool HasWriteScopes => Scopes.Any(s => s.Contains("ReadWrite", StringComparison.OrdinalIgnoreCase) || s.Contains(".Write.", StringComparison.OrdinalIgnoreCase));
}

public sealed class AccessCheck
{
    public string Collection { get; set; } = "";
    public string Label { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class RoleObservation
{
    public string Name { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string Scope { get; set; } = "";
}

/// <summary>Result of the read-only access check performed after connection. It never creates or changes tenant objects.</summary>
public sealed class AccessReport
{
    public string At { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Account { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public List<RoleObservation> Roles { get; set; } = new();
    public string RoleStatus { get; set; } = "Unknown";
    public string? RoleError { get; set; }
    public string GlobalAdministrator { get; set; } = "Not confirmed";
    public List<AccessCheck> Reads { get; set; } = new();
    public List<AccessCheck> Writes { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public int CandidateRecipes { get; set; }
    public int ManualControls { get; set; }
}
