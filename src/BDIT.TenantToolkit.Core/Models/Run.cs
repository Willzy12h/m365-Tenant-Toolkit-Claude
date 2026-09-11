namespace BDIT.TenantToolkit.Core.Models;

public static class RunStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Stopped = "Stopped";
    public const string ReviewRequired = "Review required";
    public const string Interrupted = "Interrupted";
    public const string Error = "Error";
}

public static class ResultStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "In progress";
    public const string Completed = "Completed";
    public const string Error = "Error";
    public const string NotRun = "Not run";
}

public static class ConfigurationVerification
{
    public const string Pending = "Pending";
    public const string Pass = "Pass";
    public const string Unknown = "Unknown";
}

public sealed class RunActor
{
    public string Account { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string AuthenticationType { get; set; } = "";
}

public sealed class RunResult
{
    public string ControlId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Collection { get; set; } = "";
    public string PlannedAction { get; set; } = "";
    public string Status { get; set; } = ResultStatus.Pending;
    public string? ObjectId { get; set; }
    public string Configuration { get; set; } = ConfigurationVerification.Pending;
    public string Verification { get; set; } = "Functional user, device or sign-in testing required";
    public string? Reason { get; set; }
    public string? PayloadDigest { get; set; }
    public string? WrittenAt { get; set; }
    public string? ReadbackDigest { get; set; }
}

/// <summary>Durable record of one deployment attempt: what was planned, what happened, and the before/after evidence.</summary>
public sealed class DeploymentRun
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string PrimaryDomain { get; set; } = "";
    public string Release { get; set; } = "";
    public string PlanId { get; set; } = "";
    public string PlanDigest { get; set; } = "";
    public string BeforeSnapshotId { get; set; } = "";
    public string? AfterSnapshotId { get; set; }
    public bool? AfterComplete { get; set; }
    public string? AfterError { get; set; }
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public string Status { get; set; } = RunStatus.Running;
    public string? Error { get; set; }
    public RunActor Actor { get; set; } = new();
    public string ToolkitVersion { get; set; } = "";
    public List<RunResult> Results { get; set; } = new();
    public string IntegrityDigest { get; set; } = "";
}

public sealed class JournalEntry
{
    public long Sequence { get; set; }
    public string At { get; set; } = "";
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = "";
    public string? ControlId { get; set; }
}
