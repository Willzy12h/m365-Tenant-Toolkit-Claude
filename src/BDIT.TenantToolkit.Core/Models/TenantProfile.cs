using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BDIT.TenantToolkit.Core.Models;

/// <summary>A saved client tenant. Contains no credentials; authentication always happens interactively with Microsoft.</summary>
public sealed class TenantProfile
{
    public string Id { get; set; } = "";
    public string Company { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Domain { get; set; } = "";

    /// <summary>Optional per-tenant application (client) ID overrides. Empty means use the toolkit-wide settings.</summary>
    public string AssessmentClientId { get; set; } = "";
    public string DeploymentClientId { get; set; } = "";

    public TenantParameters Parameters { get; set; } = new();
    public string Notes { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>Client-specific values substituted into catalogue templates. All identifiers are Entra object IDs.</summary>
public sealed class TenantParameters
{
    public List<string> EmergencyAccountIds { get; set; } = new();
    public string OfficeLocationId { get; set; } = "";
    public string MamGroupId { get; set; } = "";
    public string PilotGroupId { get; set; } = "";
    public string CaExclusionGroupId { get; set; } = "";

    /// <summary>Builds the dictionary consumed by <see cref="Json.CanonicalJson.Resolve"/>. Empty values resolve to null so they fail loudly.</summary>
    public IReadOnlyDictionary<string, JsonNode?> ToTemplateValues(string tenantId)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["tenantId"] = JsonValue.Create(tenantId),
            ["emergencyAccountIds"] = ToArray(EmergencyAccountIds),
            ["officeLocationId"] = ToScalar(OfficeLocationId),
            ["mamGroupId"] = ToScalar(MamGroupId),
            ["pilotGroupId"] = ToScalar(PilotGroupId),
            ["caExclusionGroupId"] = ToScalar(CaExclusionGroupId)
        };
        var emergencyAndGuests = new JsonArray();
        foreach (var id in EmergencyAccountIds) emergencyAndGuests.Add(id);
        emergencyAndGuests.Add("GuestsOrExternalUsers");
        values["emergencyAndGuestIds"] = EmergencyAccountIds.Count == 0 ? null : emergencyAndGuests;
        return values;
    }

    private static JsonNode? ToScalar(string value) => string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value);

    private static JsonNode? ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) array.Add(v);
        return array.Count == 0 ? null : array;
    }
}

public static partial class ProfileValidator
{
    private static readonly Regex GuidPattern = MyGuidRegex();

    public static bool IsGuid(string? value) => value is not null && GuidPattern.IsMatch(value);

    /// <summary>Validates and normalises a profile. Throws <see cref="ConfigurationException"/> with an engineer-readable reason.</summary>
    public static TenantProfile Validate(TenantProfile input, DateTimeOffset now)
    {
        if (input is null) throw new ConfigurationException("Profile is required.");
        if (!IsGuid(input.TenantId)) throw new ConfigurationException("A valid tenant ID (GUID) is required.");
        if (string.IsNullOrWhiteSpace(input.Company)) throw new ConfigurationException("A company or client label is required.");

        var profile = new TenantProfile
        {
            Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString() : input.Id.Trim().ToLowerInvariant(),
            Company = input.Company.Trim().Length > 120 ? input.Company.Trim()[..120] : input.Company.Trim(),
            TenantId = input.TenantId.Trim().ToLowerInvariant(),
            Domain = (input.Domain ?? "").Trim().Length > 200 ? input.Domain!.Trim()[..200] : (input.Domain ?? "").Trim(),
            AssessmentClientId = (input.AssessmentClientId ?? "").Trim().ToLowerInvariant(),
            DeploymentClientId = (input.DeploymentClientId ?? "").Trim().ToLowerInvariant(),
            Notes = (input.Notes ?? "").Trim().Length > 4000 ? input.Notes!.Trim()[..4000] : (input.Notes ?? "").Trim(),
            CreatedAt = string.IsNullOrWhiteSpace(input.CreatedAt) ? Timestamps.Format(now) : input.CreatedAt,
            UpdatedAt = Timestamps.Format(now),
            Parameters = new TenantParameters
            {
                EmergencyAccountIds = (input.Parameters?.EmergencyAccountIds ?? new List<string>())
                    .Select(v => (v ?? "").Trim().ToLowerInvariant()).Where(v => v.Length > 0).Distinct().ToList(),
                OfficeLocationId = (input.Parameters?.OfficeLocationId ?? "").Trim().ToLowerInvariant(),
                MamGroupId = (input.Parameters?.MamGroupId ?? "").Trim().ToLowerInvariant(),
                PilotGroupId = (input.Parameters?.PilotGroupId ?? "").Trim().ToLowerInvariant(),
                CaExclusionGroupId = (input.Parameters?.CaExclusionGroupId ?? "").Trim().ToLowerInvariant()
            }
        };

        if (!IsGuid(profile.Id)) throw new ConfigurationException("Invalid profile ID.");
        if (profile.AssessmentClientId.Length > 0 && !IsGuid(profile.AssessmentClientId)) throw new ConfigurationException("Assessment application ID must be a GUID.");
        if (profile.DeploymentClientId.Length > 0 && !IsGuid(profile.DeploymentClientId)) throw new ConfigurationException("Deployment application ID must be a GUID.");
        if (profile.AssessmentClientId.Length > 0 && profile.AssessmentClientId == profile.DeploymentClientId)
            throw new ConfigurationException("Assessment and deployment applications must be different registrations so read-only tokens cannot carry write permissions.");
        foreach (var id in profile.Parameters.EmergencyAccountIds)
            if (!IsGuid(id)) throw new ConfigurationException("Emergency accounts must be recorded as user object IDs (GUIDs).");
        foreach (var (label, value) in new[]
                 {
                     ("Office named-location ID", profile.Parameters.OfficeLocationId),
                     ("MAM-only group ID", profile.Parameters.MamGroupId),
                     ("Pilot group ID", profile.Parameters.PilotGroupId),
                     ("Conditional Access exclusion group ID", profile.Parameters.CaExclusionGroupId)
                 })
            if (value.Length > 0 && !IsGuid(value)) throw new ConfigurationException($"{label} must be an object ID (GUID).");
        return profile;
    }

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex MyGuidRegex();
}
