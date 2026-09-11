using System.Collections.ObjectModel;
using System.Windows.Input;
using BDIT.TenantToolkit.App.Views;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Reports;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class PrerequisiteRow
{
    public string Step { get; init; } = "";
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class DeployViewModel : PageViewModel
{
    private string _lastExport = "";

    public DeployViewModel(ShellViewModel shell) : base(shell, "Deploy")
    {
        EnableDeploymentCommand = Command(EnableDeploymentAsync, () => Workspace.Profile is not null && Workspace.Idle && !Workspace.IsDeploymentSession);
        CaptureCommand = Command(Workspace.CaptureAsync, () => Workspace.IsConnected && Workspace.Idle);
        ExportBeforeCommand = Sync(ExportBefore, () => Workspace.Snapshot is not null && Workspace.SnapshotIsLive);
        AcknowledgeCommand = Sync(Workspace.AcknowledgeSnapshot, () => Workspace.Snapshot is not null && Workspace.SnapshotIsLive && Workspace.Idle);
        DeployCommand = Command(DeployAsync, () => CanDeploy);
        PauseCommand = Sync(Workspace.PauseDeployment, () => IsRunning && !(Workspace.Control?.Paused ?? false));
        ResumeCommand = Sync(Workspace.ResumeDeployment, () => IsRunning && (Workspace.Control?.Paused ?? false));
        StopCommand = Sync(Workspace.StopDeployment, () => IsRunning);
        ExportRunHtmlCommand = Sync(() => ExportRun(ExportFormat.Html), () => Workspace.LastRun is not null);
        ExportRunJsonCommand = Sync(() => ExportRun(ExportFormat.Json), () => Workspace.LastRun is not null);
        ExportRunXlsxCommand = Sync(() => ExportRun(ExportFormat.Xlsx), () => Workspace.LastRun is not null);
        Refresh();
    }

    public ICommand EnableDeploymentCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand ExportBeforeCommand { get; }
    public ICommand AcknowledgeCommand { get; }
    public ICommand DeployCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ExportRunHtmlCommand { get; }
    public ICommand ExportRunJsonCommand { get; }
    public ICommand ExportRunXlsxCommand { get; }

    public ObservableCollection<PrerequisiteRow> Prerequisites { get; } = new();
    public ObservableCollection<RunResult> Results { get; } = new();
    public string LastExport { get => _lastExport; private set => SetProperty(ref _lastExport, value); }

    public bool IsRunning => Workspace.Executor.IsRunning;
    public bool CanDeploy => Workspace.IsDeploymentSession && Workspace.Plan is not null && Workspace.SnapshotIsLive
                             && Workspace.AcknowledgedSnapshotId == Workspace.Snapshot?.Id && Workspace.Idle && Workspace.Plan.WriteRows.Any();

    public string RunText
    {
        get
        {
            var run = Workspace.LastRun;
            if (IsRunning) return "Deployment running. Pause or stop takes effect at the next action boundary; the current write always completes and an after-change snapshot is captured.";
            if (run is null) return "No deployment has been started in this session.";
            return $"Run {run.Id} · {run.Status} · started {run.StartedAt} · ended {run.EndedAt} · before {run.BeforeSnapshotId} · after {run.AfterSnapshotId ?? "not captured"}{(run.AfterComplete == false ? " (INCOMPLETE)" : "")}{(run.Error is null ? "" : " · " + run.Error)}";
        }
    }

    private async Task EnableDeploymentAsync()
    {
        var profile = Workspace.Profile ?? throw new ToolkitException("Select a client first.");
        var confirm = System.Windows.MessageBox.Show(
            "Deployment access signs you in again with the BDIT Tenant Deployment application and requests write permissions for this tenant.\n\n" +
            "Any capture, assessment and plan from the read-only session are discarded and must be repeated in the deployment session.\n\nNothing is written until you confirm a reviewed plan. Continue?",
            "Enable deployment access", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
        await Workspace.ConnectAsync(profile, SessionMode.Deployment);
    }

    private void ExportBefore()
    {
        var snapshot = Workspace.Snapshot ?? throw new ToolkitException("Capture the tenant first.");
        LastExport = "Exported before-change capture: " + Workspace.Exporter.ExportSnapshot(snapshot, Workspace.Standard, ExportFormat.Json);
    }

    private async Task DeployAsync()
    {
        Workspace.ValidatePlanForExecution();
        var plan = Workspace.Plan!;
        var dialog = new ConfirmTenantDialog(Workspace.Profile!, plan) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        await Workspace.DeployAsync(dialog.TypedTenantId);
        Refresh();
    }

    private void ExportRun(ExportFormat format)
    {
        var run = Workspace.LastRun ?? throw new ToolkitException("No run to export.");
        var journal = Workspace.Evidence.ReadJournal(run.TenantId, run.Id);
        LastExport = "Exported: " + Workspace.Exporter.ExportRun(run, journal, format);
    }

    public override void Refresh()
    {
        Prerequisites.Clear();
        var session = Workspace.Session;
        Prerequisites.Add(new PrerequisiteRow
        {
            Step = "1. Deployment access",
            Status = session is null ? "Not connected" : session.Mode == SessionMode.Deployment ? "Ready" : "Read-only session",
            Detail = session is null ? "Connect on the Connect page first." : session.Mode == SessionMode.Deployment ? $"{session.Account} via {session.ClientLabel}" : "Enable deployment access to sign in with the deployment application."
        });
        var missingScopes = Workspace.Access?.Writes.Where(w => w.Status == "Missing scope").Select(w => w.Label).ToList() ?? new List<string>();
        Prerequisites.Add(new PrerequisiteRow
        {
            Step = "2. Write scopes",
            Status = session?.Mode != SessionMode.Deployment ? "Pending" : missingScopes.Count == 0 ? "Observed" : "Missing",
            Detail = missingScopes.Count == 0 ? "Scope observation does not prove API acceptance." : "Missing: " + string.Join(", ", missingScopes)
        });
        Prerequisites.Add(new PrerequisiteRow
        {
            Step = "3. Live capture",
            Status = Workspace.Snapshot is null ? "Pending" : Workspace.SnapshotIsLive ? (Workspace.Snapshot.Complete ? "Ready" : "Incomplete") : "Stored capture",
            Detail = Workspace.Snapshot is null ? "Read the tenant configuration in this session." : $"{Workspace.Snapshot.Id} captured {Workspace.Snapshot.CapturedAt}"
        });
        Prerequisites.Add(new PrerequisiteRow
        {
            Step = "4. Reviewed plan",
            Status = Workspace.Plan is null ? "Pending" : Workspace.Plan.WriteRows.Any() ? "Ready" : "No changes",
            Detail = Workspace.Plan is null ? "Build a plan on the Plan changes page." : $"{Workspace.Plan.WriteRows.Count()} write(s) · digest {Workspace.Plan.PlanDigest[..12]}…"
        });
        Prerequisites.Add(new PrerequisiteRow
        {
            Step = "5. Before-change evidence acknowledged",
            Status = Workspace.Snapshot is not null && Workspace.AcknowledgedSnapshotId == Workspace.Snapshot.Id ? "Ready" : "Pending",
            Detail = "Export the before-change capture and acknowledge it. Plans expire with their snapshot after " + Workspace.Settings.SnapshotMaxAgeMinutes + " minutes."
        });
        Results.Clear();
        if (Workspace.LastRun is not null) foreach (var r in Workspace.LastRun.Results) Results.Add(r);
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(CanDeploy));
        OnPropertyChanged(nameof(IsRunning));
    }
}
