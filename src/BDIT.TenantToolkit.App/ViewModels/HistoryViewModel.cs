using System.Collections.ObjectModel;
using System.Windows.Input;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Evidence;
using BDIT.TenantToolkit.Engine.Reports;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class HistoryViewModel : PageViewModel
{
    private DeploymentRun? _selectedRun;
    private SnapshotSummary? _before;
    private SnapshotSummary? _after;
    private DriftReport? _drift;
    private DriftItem? _selectedDriftItem;
    private string _lastExport = "";

    public HistoryViewModel(ShellViewModel shell) : base(shell, "Evidence and drift")
    {
        RefreshCommand = Sync(Refresh);
        CompareCommand = Sync(Compare, () => Before is not null && After is not null && Before.Id != After.Id);
        ExportRunHtmlCommand = Sync(() => ExportRun(ExportFormat.Html), () => SelectedRun is not null);
        ExportRunJsonCommand = Sync(() => ExportRun(ExportFormat.Json), () => SelectedRun is not null);
        ExportRunXlsxCommand = Sync(() => ExportRun(ExportFormat.Xlsx), () => SelectedRun is not null);
        ExportDriftHtmlCommand = Sync(() => ExportDrift(ExportFormat.Html), () => Drift is not null);
        ExportDriftMarkdownCommand = Sync(() => ExportDrift(ExportFormat.Markdown), () => Drift is not null);
        ExportDriftXlsxCommand = Sync(() => ExportDrift(ExportFormat.Xlsx), () => Drift is not null);
        OpenSnapshotCommand = Sync(() => { if (Before is not null) Workspace.LoadStoredSnapshot(Before.Id); }, () => Before is not null && Workspace.Idle);
        Refresh();
    }

    public ICommand RefreshCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand ExportRunHtmlCommand { get; }
    public ICommand ExportRunJsonCommand { get; }
    public ICommand ExportRunXlsxCommand { get; }
    public ICommand ExportDriftHtmlCommand { get; }
    public ICommand ExportDriftMarkdownCommand { get; }
    public ICommand ExportDriftXlsxCommand { get; }
    public ICommand OpenSnapshotCommand { get; }

    public ObservableCollection<SnapshotSummary> Snapshots { get; } = new();
    public ObservableCollection<DeploymentRun> Runs { get; } = new();
    public ObservableCollection<RunResult> RunResults { get; } = new();
    public ObservableCollection<JournalEntry> Journal { get; } = new();
    public ObservableCollection<ControlStatusChange> ControlChanges { get; } = new();
    public ObservableCollection<DriftItem> DriftItems { get; } = new();
    public ObservableCollection<PropertyDifference> DriftDifferences { get; } = new();

    public SnapshotSummary? Before { get => _before; set => SetProperty(ref _before, value); }
    public SnapshotSummary? After { get => _after; set => SetProperty(ref _after, value); }
    public DriftReport? Drift { get => _drift; private set { SetProperty(ref _drift, value); OnPropertyChanged(nameof(DriftText)); } }
    public string LastExport { get => _lastExport; private set => SetProperty(ref _lastExport, value); }

    public DeploymentRun? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (!SetProperty(ref _selectedRun, value)) return;
            RunResults.Clear();
            Journal.Clear();
            if (value is null) return;
            foreach (var r in value.Results) RunResults.Add(r);
            try { foreach (var j in Workspace.Evidence.ReadJournal(value.TenantId, value.Id)) Journal.Add(j); }
            catch (ToolkitException ex) { Shell.ShowError(ex); }
            OnPropertyChanged(nameof(RunText));
        }
    }

    public DriftItem? SelectedDriftItem
    {
        get => _selectedDriftItem;
        set
        {
            SetProperty(ref _selectedDriftItem, value);
            DriftDifferences.Clear();
            if (value is not null) foreach (var d in value.Differences) DriftDifferences.Add(d);
        }
    }

    public string RunText => SelectedRun is null ? "Select a run to see its results and journal." :
        $"Run {SelectedRun.Id} · {SelectedRun.Status} · plan {SelectedRun.PlanId} · actor {SelectedRun.Actor.Account} · before {SelectedRun.BeforeSnapshotId} · after {SelectedRun.AfterSnapshotId ?? "none"}{(SelectedRun.Error is null ? "" : " · " + SelectedRun.Error)}";

    public string DriftText => Drift is null ? "Choose two captures of this tenant and compare them. Managed objects are classified by whether they still match what the toolkit last applied." :
        $"Before {Drift.BeforeCapturedAt} (standard {Drift.BeforeStandardRelease}) → after {Drift.AfterCapturedAt} (standard {Drift.AfterStandardRelease}) · assessed against {Drift.AssessedRelease} · {Drift.Regressions} control regression(s) · {Drift.Changed} changed, {Drift.Added} added, {Drift.Removed} removed · {Drift.ManagedAffected} toolkit-managed object(s) affected." +
        (Drift.Notes.Count == 0 ? "" : "\n" + string.Join("\n", Drift.Notes));

    public string ContextText => Workspace.Profile is null ? "Select a client on the Connect page to browse its evidence." : $"Evidence for {Workspace.Profile.Company} in {Workspace.Evidence.TenantDirectory(Workspace.Profile.TenantId)}";

    private void Compare()
    {
        var before = Before ?? throw new ToolkitException("Choose the earlier capture.");
        var after = After ?? throw new ToolkitException("Choose the later capture.");
        Drift = Workspace.CompareSnapshots(before.Id, after.Id);
        ControlChanges.Clear();
        DriftItems.Clear();
        foreach (var c in Drift.ControlChanges) ControlChanges.Add(c);
        foreach (var i in Drift.Items.Where(i => i.Change != DriftChange.Unchanged)) DriftItems.Add(i);
        SelectedDriftItem = null;
    }

    private void ExportRun(ExportFormat format)
    {
        var run = SelectedRun ?? throw new ToolkitException("Select a run first.");
        LastExport = "Exported: " + Workspace.Exporter.ExportRun(run, Workspace.Evidence.ReadJournal(run.TenantId, run.Id), format);
    }

    private void ExportDrift(ExportFormat format)
    {
        var drift = Drift ?? throw new ToolkitException("Compare two captures first.");
        LastExport = "Exported: " + Workspace.Exporter.ExportDrift(drift, format);
    }

    public override void Refresh()
    {
        Snapshots.Clear();
        Runs.Clear();
        if (Workspace.Profile is not null)
        {
            try
            {
                foreach (var s in Workspace.Evidence.ListSnapshots(Workspace.Profile.TenantId)) Snapshots.Add(s);
                foreach (var r in Workspace.Evidence.LoadRuns(Workspace.Profile.TenantId)) Runs.Add(r);
            }
            catch (ToolkitException ex) { Shell.ShowError(ex); }
        }
        if (Before is not null && Snapshots.All(s => s.Id != Before.Id)) Before = null;
        if (After is not null && Snapshots.All(s => s.Id != After.Id)) After = null;
        if (SelectedRun is not null && Runs.All(r => r.Id != SelectedRun.Id)) SelectedRun = null;
        OnPropertyChanged(nameof(ContextText));
    }
}
