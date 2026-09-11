using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BDIT.TenantToolkit.Core.Models;

public static class CaptureStatus
{
    public const string Collected = "Collected";
    public const string Error = "Error";
}

/// <summary>A read-only capture of tenant configuration, stored locally as evidence and used for assessment, planning and drift.</summary>
public sealed class TenantSnapshot
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string PrimaryDomain { get; set; } = "";
    public string ClientLabel { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public string CapturedBy { get; set; } = "";
    public string SessionMode { get; set; } = "";
    public string StandardRelease { get; set; } = "";
    public string ToolkitVersion { get; set; } = "";
    public string IdentitySource { get; set; } = "";
    public Dictionary<string, CollectionCapture> Collections { get; set; } = new(StringComparer.Ordinal);
    public bool Complete { get; set; }
    /// <summary>Evidence integrity digest of the canonical content excluding this field. Detects accidental or casual modification; it is not a signature.</summary>
    public string IntegrityDigest { get; set; } = "";

    [JsonIgnore] public IEnumerable<string> BetaCollections =>
        Collections.Where(c => string.Equals(c.Value.Api, "beta", StringComparison.OrdinalIgnoreCase)).Select(c => c.Key);
}

public sealed class CollectionCapture
{
    public string Status { get; set; } = CaptureStatus.Error;
    public string Api { get; set; } = "v1.0";
    public string Path { get; set; } = "";
    public List<JsonObject> Items { get; set; } = new();
    public int Count { get; set; }
    public string? Error { get; set; }
    /// <summary>True when assignments, child settings or relationships could not be read for at least one object.</summary>
    public bool DetailIncomplete { get; set; }

    [JsonIgnore] public bool Usable => Status == CaptureStatus.Collected && !DetailIncomplete;
}
