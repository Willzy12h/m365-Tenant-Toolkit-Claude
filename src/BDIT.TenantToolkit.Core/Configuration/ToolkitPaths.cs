namespace BDIT.TenantToolkit.Core.Configuration;

/// <summary>
/// Resolves the portable folder layout. Everything the toolkit reads or writes lives under one root:
///   app/        the application (published output)
///   standards/  BDIT Build Standard releases plus manifest.json
///   config/     toolkit.settings.json
///   data/       profiles and per-tenant evidence (never shared between tenants)
///   logs/       diagnostic logs
///   reports/    exported reports
/// </summary>
public sealed class ToolkitPaths
{
    public string Root { get; }
    public string AppDirectory { get; }
    public string StandardsDirectory => Path.Combine(Root, "standards");
    public string StandardsManifestFile => Path.Combine(StandardsDirectory, "manifest.json");
    public string ConfigDirectory => Path.Combine(Root, "config");
    public string SettingsFile => Path.Combine(ConfigDirectory, "toolkit.settings.json");
    public string DataDirectory => Path.Combine(Root, "data");
    public string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");
    public string TenantsDirectory => Path.Combine(DataDirectory, "tenants");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string ReportsDirectory => Path.Combine(Root, "reports");
    public string DocsDirectory => Path.Combine(Root, "docs");

    private ToolkitPaths(string root, string appDirectory)
    {
        Root = root;
        AppDirectory = appDirectory;
    }

    public string TenantDirectory(string tenantId)
    {
        if (!Models.ProfileValidator.IsGuid(tenantId)) throw new ConfigurationException("Tenant ID must be a GUID.");
        return Path.Combine(TenantsDirectory, tenantId.ToLowerInvariant());
    }

    /// <summary>
    /// Root resolution order: explicit override, BDIT_TOOLKIT_ROOT, then the nearest ancestor of the application
    /// directory that contains a standards folder (portable layout or a source checkout).
    /// </summary>
    public static ToolkitPaths Resolve(string? overrideRoot = null)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = overrideRoot ?? Environment.GetEnvironmentVariable("BDIT_TOOLKIT_ROOT");
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var full = Path.GetFullPath(candidate);
            if (!Directory.Exists(Path.Combine(full, "standards")))
                throw new ConfigurationException($"The toolkit root '{full}' does not contain a standards folder.");
            return new ToolkitPaths(full, appDir);
        }

        var dir = new DirectoryInfo(appDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "standards")))
                return new ToolkitPaths(dir.FullName, appDir);
        }
        throw new ConfigurationException(
            "Could not locate the toolkit root (a folder containing 'standards'). Extract the complete portable package and start it with Start.cmd, or set BDIT_TOOLKIT_ROOT.");
    }

    public void EnsureWritableFolders()
    {
        foreach (var d in new[] { ConfigDirectory, DataDirectory, TenantsDirectory, LogsDirectory, ReportsDirectory })
            Directory.CreateDirectory(d);
    }
}
