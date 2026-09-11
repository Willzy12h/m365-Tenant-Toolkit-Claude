using System.Windows.Input;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(ShellViewModel shell) : base(shell, "Settings and diagnostics")
    {
        OpenReportsCommand = Sync(() => OpenFolder(Workspace.Paths.ReportsDirectory));
        OpenLogsCommand = Sync(() => OpenFolder(Workspace.Paths.LogsDirectory));
        OpenDataCommand = Sync(() => OpenFolder(Workspace.Paths.DataDirectory));
        OpenConfigCommand = Sync(() => OpenFolder(Workspace.Paths.ConfigDirectory));
        OpenStandardsCommand = Sync(() => OpenFolder(Workspace.Paths.StandardsDirectory));
    }

    public ICommand OpenReportsCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand OpenConfigCommand { get; }
    public ICommand OpenStandardsCommand { get; }

    public string Text
    {
        get
        {
            var s = Workspace.Settings;
            var p = Workspace.Paths;
            var lines = new List<string>
            {
                $"Toolkit version:            {Workspace.Version}",
                $"Diagnostics mode:           {(Workspace.Diagnostics ? "on (verbose logging)" : "off")}",
                $"Root folder:                {p.Root}",
                $"Application folder:         {p.AppDirectory}",
                $"Settings file:              {p.SettingsFile}",
                $"Standards folder:           {p.StandardsDirectory}",
                $"Evidence (data) folder:     {p.DataDirectory}",
                $"Reports folder:             {p.ReportsDirectory}",
                $"Logs folder:                {p.LogsDirectory} (current file: {Workspace.Logger.FilePath})",
                "",
                $"Assessment application:     {(s.AssessmentClientId.Length == 0 ? "not configured" + (s.AllowMicrosoftGraphPowerShellFallback ? " - shared Microsoft Graph PowerShell app will be used (read-only)" : "") : s.AssessmentClientId)}",
                $"Deployment application:     {(s.DeploymentClientId.Length == 0 ? "not configured - deployment unavailable" : s.DeploymentClientId)}",
                $"Default standard release:   {s.DefaultStandardRelease}",
                $"Loaded standard:            {(Workspace.Standard is null ? "none (" + Workspace.StandardError + ")" : Workspace.Standard.Release + " digest " + Workspace.Standard.IntegrityDigest)}",
                $"Snapshot max age (minutes): {s.SnapshotMaxAgeMinutes}",
                $"Plan max age (minutes):     {s.PlanMaxAgeMinutes}",
                $"Sign-in timeout (minutes):  {s.SignInTimeoutMinutes}",
                $"Graph read timeout (s):     {s.GraphReadTimeoutSeconds}",
                $"Graph write timeout (s):    {s.GraphWriteTimeoutSeconds}",
                $"Max Retry-After honoured:   {s.MaxRetryAfterSeconds} s",
                $"Log level:                  {s.LogLevel}",
                "",
                "Settings are edited in config/toolkit.settings.json and applied at the next start. No secrets are stored; authentication always happens in your browser with Microsoft.",
                "Token caches are DPAPI-protected per tenant and removed on disconnect and on exit.",
                "Run Start-Diagnostics.cmd to launch with verbose logging and a visible start-up log."
            };
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static void OpenFolder(string path) => Infrastructure.ShellFolders.OpenFolder(path);

    public override void Refresh() => OnPropertyChanged(nameof(Text));
}
