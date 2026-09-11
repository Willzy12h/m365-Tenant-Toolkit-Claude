using System.Collections.ObjectModel;
using System.Windows.Input;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Reports;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class FindingRow
{
    public ControlFinding Finding { get; init; } = new();
    public string ControlId => Finding.ControlId;
    public string Name => Finding.Name;
    public string Category => Finding.Category;
    public string Severity => Finding.Severity;
    public string Status => StatusLabels.For(Finding.Status);
    public string StatusKey => Finding.Status.ToString();
    public string Reason => Finding.Reason;
    public string BestMatch => Finding.BestCandidate is { } c ? $"{c.Name} ({StatusLabels.For(c.Enforcement)})" : "";
}

public sealed class AssessmentViewModel : PageViewModel
{
    private string _filterStatus = "Actionable";
    private string _filterCategory = "All";
    private string _search = "";
    private FindingRow? _selected;
    private CandidateMatch? _selectedCandidate;
    private string _lastExport = "";
    private string _lastExportFile = "";

    public AssessmentViewModel(ShellViewModel shell) : base(shell, "Assessment")
    {
        ReassessCommand = Sync(Workspace.RunAssessment, () => Workspace.Snapshot is not null && Workspace.Idle);
        ExportHtmlCommand = Sync(() => Export(ExportFormat.Html), () => Workspace.Assessment is not null);
        ExportMarkdownCommand = Sync(() => Export(ExportFormat.Markdown), () => Workspace.Assessment is not null);
        ExportJsonCommand = Sync(() => Export(ExportFormat.Json), () => Workspace.Assessment is not null);
        ExportCsvCommand = Sync(() => Export(ExportFormat.Csv), () => Workspace.Assessment is not null);
        ExportXlsxCommand = Sync(() => Export(ExportFormat.Xlsx), () => Workspace.Assessment is not null);
        ExportClientCommand = Sync(() => Export(ExportFormat.ClientHtml), () => Workspace.Assessment is not null);
        CopyDetailCommand = CopyText(() => SelectedDetail + Environment.NewLine + string.Join(Environment.NewLine, Notes));
        OpenExportCommand = Sync(() => Infrastructure.ShellFolders.RevealFile(_lastExportFile), () => _lastExportFile.Length > 0);
        Refresh();
    }

    public ICommand ReassessCommand { get; }
    public ICommand ExportHtmlCommand { get; }
    public ICommand ExportMarkdownCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportXlsxCommand { get; }
    public ICommand ExportClientCommand { get; }
    public ICommand CopyDetailCommand { get; }
    public ICommand OpenExportCommand { get; }

    public ObservableCollection<FindingRow> Findings { get; } = new();
    public ObservableCollection<string> StatusFilters { get; } = new() { "Actionable", "All", "Compliant", "CompliantWithDeviation", "SettingsMatchNotEnforced", "PartialMatch", "Missing", "UnableToAssess", "RequiresManualReview", "LicenceUnavailable", "NotApplicable" };
    public ObservableCollection<string> CategoryFilters { get; } = new();
    public ObservableCollection<CandidateMatch> Candidates { get; } = new();
    public ObservableCollection<PropertyDifference> Differences { get; } = new();
    public ObservableCollection<string> Limitations { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    private readonly List<FindingRow> _all = new();

    public string FilterStatus { get => _filterStatus; set { if (SetProperty(ref _filterStatus, value)) ApplyFilter(); } }
    public string FilterCategory { get => _filterCategory; set { if (SetProperty(ref _filterCategory, value)) ApplyFilter(); } }
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) ApplyFilter(); } }
    public string LastExport { get => _lastExport; private set => SetProperty(ref _lastExport, value); }

    public FindingRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            Candidates.Clear();
            Notes.Clear();
            if (value is not null)
            {
                foreach (var c in value.Finding.Candidates) Candidates.Add(c);
                foreach (var n in value.Finding.Notes) Notes.Add(n);
                if (value.Finding.Deviation is { } d) Notes.Add($"Deviation ({d.Kind}): {d.Reason} - approved by {d.ApprovedBy}, review by {(d.ReviewBy.Length == 0 ? "not set" : d.ReviewBy)}");
                foreach (var o in value.Finding.ObservedObjects) Notes.Add("Observed: " + o);
            }
            SelectedCandidate = Candidates.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedDetail));
            OnPropertyChanged(nameof(ProposedJson));
        }
    }

    public CandidateMatch? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            SetProperty(ref _selectedCandidate, value);
            Differences.Clear();
            if (value is not null) foreach (var d in value.Differences) Differences.Add(d);
            OnPropertyChanged(nameof(CandidateText));
        }
    }

    public string CandidateText => SelectedCandidate is null ? "" : $"{SelectedCandidate.Name} [{SelectedCandidate.ObjectId}] · {StatusLabels.For(SelectedCandidate.Enforcement)} · {SelectedCandidate.AssignmentSummary} · {SelectedCandidate.Matched}/{SelectedCandidate.Total} settings match{(SelectedCandidate.ToolkitManaged ? " · created by the toolkit" : "")}";

    public string SelectedDetail
    {
        get
        {
            var f = Selected?.Finding;
            if (f is null) return "Select a finding to see the reason, matching objects and property-level differences.";
            var lines = new List<string>
            {
                $"{f.ControlId} {f.Name} · {f.Category} · severity {f.Severity}",
                "Status: " + StatusLabels.For(f.Status),
                "Finding: " + f.Reason,
                "Desired state: " + f.DesiredState
            };
            if (f.ExpectedProductionState.Length > 0) lines.Add($"Expected production state: {f.ExpectedProductionState} · {f.ExpectedProductionAssignment}");
            if (f.BusinessImpact.Length > 0) lines.Add("Business impact: " + f.BusinessImpact);
            if (f.EngineerAction.Length > 0) lines.Add("Engineer action: " + f.EngineerAction);
            if (f.ManualInstructions.Length > 0) lines.Add("How to review: " + f.ManualInstructions);
            if (f.Owned) lines.Add("Toolkit-managed object: " + f.OwnedObjectId);
            foreach (var e in f.Equivalence)
            {
                lines.Add("");
                lines.Add($"Equivalent configuration test - {e.Name} [{e.ObjectId}] · {StatusLabels.For(e.Enforcement)} · {(e.Covered ? "satisfies every required condition" : "partial")}");
                foreach (var s in e.Signals)
                    lines.Add($"   [{(s.Matched ? "met" : "not met")}] {s.Label} ({s.Path}) · expected {s.Expected} · observed {s.Observed}");
                foreach (var caveat in e.Caveats) lines.Add("   Caveat: " + caveat);
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string ProposedJson => Selected?.Finding.Proposed?.ToJsonString(ToolkitJson.Options) ?? "";

    public string SummaryText
    {
        get
        {
            var a = Workspace.Assessment;
            if (a is null) return Workspace.Snapshot is null ? "Read the tenant configuration to run an assessment." : "No assessment yet.";
            var s = a.Summary;
            return $"{a.TenantName} · standard {a.Release} · assessed {a.AssessedAt} · snapshot {(a.SnapshotComplete ? "complete" : "INCOMPLETE")}\n" +
                   $"Compliant {s.Compliant} (+{s.CompliantWithDeviation} with deviation) · Match not enforced {s.SettingsMatchNotEnforced} · Partial {s.PartialMatch} · Missing {s.Missing} · Manual review {s.RequiresManualReview} · Unable to assess {s.UnableToAssess} · Licence {s.LicenceUnavailable} · N/A {s.NotApplicable}\n" +
                   $"Actionable: {s.CriticalActionable} critical, {s.HighActionable} high, {s.MediumActionable} medium, {s.LowActionable} low.";
        }
    }

    private void Export(ExportFormat format)
    {
        var a = Workspace.Assessment ?? throw new ToolkitException("Run an assessment first.");
        _lastExportFile = Workspace.Exporter.ExportAssessment(a, format);
        LastExport = "Exported: " + _lastExportFile;
        RaiseAll();
    }

    public override void Refresh()
    {
        _all.Clear();
        CategoryFilters.Clear();
        CategoryFilters.Add("All");
        Limitations.Clear();
        var a = Workspace.Assessment;
        if (a is not null)
        {
            foreach (var f in a.Findings) _all.Add(new FindingRow { Finding = f });
            foreach (var c in a.Findings.Select(f => f.Category).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase)) CategoryFilters.Add(c);
            foreach (var l in a.Limitations) Limitations.Add(l);
        }
        if (!CategoryFilters.Contains(_filterCategory)) _filterCategory = "All";
        ApplyFilter();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(FilterCategory));
    }

    private void ApplyFilter()
    {
        Findings.Clear();
        var q = Search.Trim();
        foreach (var row in _all.OrderBy(r => r.Finding.IsActionable ? 0 : 1).ThenBy(r => HtmlReports.SeverityRank(r.Severity)).ThenBy(r => r.ControlId, StringComparer.OrdinalIgnoreCase))
        {
            if (FilterStatus == "Actionable" && !row.Finding.IsActionable) continue;
            if (FilterStatus != "All" && FilterStatus != "Actionable" && row.StatusKey != FilterStatus) continue;
            if (FilterCategory != "All" && row.Category != FilterCategory) continue;
            if (q.Length > 0 && !row.ControlId.Contains(q, StringComparison.OrdinalIgnoreCase) && !row.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !row.Reason.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Findings.Add(row);
        }
        if (Selected is not null && !Findings.Contains(Selected)) Selected = null;
    }
}
