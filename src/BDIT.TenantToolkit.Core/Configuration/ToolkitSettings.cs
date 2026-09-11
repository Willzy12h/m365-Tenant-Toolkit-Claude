using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Core.Configuration;

/// <summary>Engineer-editable settings from config/toolkit.settings.json. No secrets are ever stored here.</summary>
public sealed class ToolkitSettings
{
    /// <summary>Well-known public client ID of the Microsoft Graph PowerShell enterprise application, used only as an assessment fallback.</summary>
    public const string MicrosoftGraphPowerShellClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public string ProductName { get; set; } = "BDIT Tenant Toolkit";
    public string CompanyName { get; set; } = "Blue Diamond IT";
    public string AccentColour { get; set; } = "#1769AA";

    /// <summary>Application (client) ID of the BDIT Tenant Assessment registration (delegated, read scopes only).</summary>
    public string AssessmentClientId { get; set; } = "";
    /// <summary>Application (client) ID of the BDIT Tenant Deployment registration (delegated, read plus write scopes).</summary>
    public string DeploymentClientId { get; set; } = "";
    /// <summary>When no assessment application is configured, allow the shared Microsoft Graph PowerShell application for read-only work.</summary>
    public bool AllowMicrosoftGraphPowerShellFallback { get; set; } = true;

    public string DefaultStandardRelease { get; set; } = "";
    public int SnapshotMaxAgeMinutes { get; set; } = 20;
    public int PlanMaxAgeMinutes { get; set; } = 20;
    public int SignInTimeoutMinutes { get; set; } = 5;
    public int GraphReadTimeoutSeconds { get; set; } = 120;
    public int GraphWriteTimeoutSeconds { get; set; } = 100;
    public int MaxRetryAfterSeconds { get; set; } = 300;
    public string LogLevel { get; set; } = "Information";

    public static ToolkitSettings Load(string file)
    {
        if (!File.Exists(file)) return Validate(new ToolkitSettings());
        try
        {
            return Validate(ToolkitJson.Deserialize<ToolkitSettings>(File.ReadAllText(file)));
        }
        catch (Exception ex) when (ex is not ConfigurationException)
        {
            throw new ConfigurationException($"Settings file is invalid: {file}. {ex.Message}", ex);
        }
    }

    public static ToolkitSettings Validate(ToolkitSettings s)
    {
        s.AssessmentClientId = (s.AssessmentClientId ?? "").Trim().ToLowerInvariant();
        s.DeploymentClientId = (s.DeploymentClientId ?? "").Trim().ToLowerInvariant();
        if (s.AssessmentClientId.Length > 0 && !ProfileValidator.IsGuid(s.AssessmentClientId))
            throw new ConfigurationException("assessmentClientId must be an application (client) ID GUID or empty.");
        if (s.DeploymentClientId.Length > 0 && !ProfileValidator.IsGuid(s.DeploymentClientId))
            throw new ConfigurationException("deploymentClientId must be an application (client) ID GUID or empty.");
        if (s.AssessmentClientId.Length > 0 && s.AssessmentClientId == s.DeploymentClientId)
            throw new ConfigurationException("assessmentClientId and deploymentClientId must be different registrations.");
        if (s.DeploymentClientId == MicrosoftGraphPowerShellClientId)
            throw new ConfigurationException("The Microsoft Graph PowerShell application may not be used for deployment. Register a dedicated BDIT Tenant Deployment application.");
        s.SnapshotMaxAgeMinutes = Math.Clamp(s.SnapshotMaxAgeMinutes, 5, 240);
        s.PlanMaxAgeMinutes = Math.Clamp(s.PlanMaxAgeMinutes, 5, 240);
        s.SignInTimeoutMinutes = Math.Clamp(s.SignInTimeoutMinutes, 1, 15);
        s.GraphReadTimeoutSeconds = Math.Clamp(s.GraphReadTimeoutSeconds, 30, 600);
        s.GraphWriteTimeoutSeconds = Math.Clamp(s.GraphWriteTimeoutSeconds, 30, 300);
        s.MaxRetryAfterSeconds = Math.Clamp(s.MaxRetryAfterSeconds, 30, 900);
        s.ProductName = string.IsNullOrWhiteSpace(s.ProductName) ? "BDIT Tenant Toolkit" : s.ProductName.Trim();
        s.CompanyName = (s.CompanyName ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(s.AccentColour ?? "", "^#[0-9a-fA-F]{6}$")) s.AccentColour = "#1769AA";
        return s;
    }

    /// <summary>Resolves the client ID for a mode, honouring per-tenant overrides. Returns null when deployment is not configured.</summary>
    public (string ClientId, string Label, bool IsSharedFallback)? ResolveClient(SessionMode mode, TenantProfile? profile)
    {
        if (mode == SessionMode.Deployment)
        {
            var id = profile is not null && profile.DeploymentClientId.Length > 0 ? profile.DeploymentClientId : DeploymentClientId;
            return id.Length == 0 ? null : (id, profile is not null && profile.DeploymentClientId.Length > 0 ? "Tenant-specific deployment application" : "BDIT Tenant Deployment application", false);
        }
        var assessment = profile is not null && profile.AssessmentClientId.Length > 0 ? profile.AssessmentClientId : AssessmentClientId;
        if (assessment.Length > 0)
            return (assessment, profile is not null && profile.AssessmentClientId.Length > 0 ? "Tenant-specific assessment application" : "BDIT Tenant Assessment application", false);
        if (AllowMicrosoftGraphPowerShellFallback)
            return (MicrosoftGraphPowerShellClientId, "Microsoft Graph PowerShell (shared Microsoft application)", true);
        return null;
    }
}
