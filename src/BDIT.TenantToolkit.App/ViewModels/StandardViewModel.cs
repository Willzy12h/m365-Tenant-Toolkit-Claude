using System.Collections.ObjectModel;
using System.Windows.Input;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Standards;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class StandardViewModel : PageViewModel
{
    private StandardRelease? _selectedRelease;
    private ControlDefinition? _selected;
    private string _search = "";
    private string _category = "All";

    public StandardViewModel(ShellViewModel shell) : base(shell, "Build Standard")
    {
        SelectReleaseCommand = Sync(() => { if (SelectedRelease is not null && !Workspace.TrySelectStandard(SelectedRelease.FileName)) throw new Core.ConfigurationException(Workspace.StandardError ?? "Standard could not be loaded."); }, () => SelectedRelease is not null && Workspace.Idle);
        Refresh();
    }

    public ICommand SelectReleaseCommand { get; }
    public ObservableCollection<StandardRelease> Releases => Workspace.Releases;
    public ObservableCollection<ControlDefinition> Controls { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();

    public StandardRelease? SelectedRelease { get => _selectedRelease; set => SetProperty(ref _selectedRelease, value); }
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) ApplyFilter(); } }
    public string Category { get => _category; set { if (SetProperty(ref _category, value)) ApplyFilter(); } }

    public ControlDefinition? Selected
    {
        get => _selected;
        set { SetProperty(ref _selected, value); OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(PayloadJson)); }
    }

    public string HeaderText => Workspace.Standard is null
        ? "No Build Standard loaded. " + (Workspace.StandardError ?? "")
        : $"{Workspace.Standard.Release} · {Workspace.Standard.Status} · {Workspace.Standard.Controls.Count} controls · {Workspace.Standard.Controls.Count(c => c.HasRecipe)} automated recipes · integrity digest {Workspace.Standard.IntegrityDigest} (SHA-256 manifest check; not a signature)";

    public string Detail
    {
        get
        {
            var c = Selected;
            if (c is null) return "Select a control.";
            var lines = new List<string>
            {
                $"{c.Id} {c.Name} · {c.Category} · severity {c.Severity}",
                "Purpose: " + c.Purpose,
                "Desired state: " + c.DesiredState,
                $"Expected production state: {c.ExpectedProduction.State} · {c.ExpectedProduction.Assignment}{(c.ExpectedProduction.Notes.Length > 0 ? " · " + c.ExpectedProduction.Notes : "")}",
                $"Toolkit safe deployment: {(c.HasRecipe ? c.SafeDeployment.State + " · " + c.SafeDeployment.Assignment : "not automated")}{(c.SafeDeployment.Notes.Length > 0 ? " · " + c.SafeDeployment.Notes : "")}",
                "Business impact: " + c.BusinessImpact,
                "Engineer action: " + c.EngineerAction,
                "Assessment: " + (c.HasRecipe ? "settings comparison" : "manual - " + c.Assessment.ManualInstructions),
                "Collection: " + (c.Collection ?? "none"),
                "Licence: " + (c.Licence.ServicePlans.Count == 0 ? "no specific service plan" : string.Join(", ", c.Licence.ServicePlans)) + (c.Licence.Note.Length > 0 ? " · " + c.Licence.Note : "")
            };
            if (c.Dependencies.Count > 0) lines.Add("Depends on: " + string.Join(", ", c.Dependencies));
            if (c.References.Microsoft.Length > 0) lines.Add("Microsoft: " + c.References.Microsoft);
            if (c.References.Cis.Length > 0) lines.Add("CIS: " + c.References.Cis);
            if (c.References.CyberEssentials.Length > 0) lines.Add("Cyber Essentials: " + c.References.CyberEssentials);
            if (c.DocumentationNotes.Length > 0) lines.Add("Notes: " + c.DocumentationNotes);
            return string.Join(Environment.NewLine, lines);
        }
    }

    public string PayloadJson => Selected?.Payload?.ToJsonString(ToolkitJson.Options) ?? "No automated recipe. {{placeholders}} are replaced with client profile values at assessment and planning time.";

    public override void Refresh()
    {
        if (SelectedRelease is null && Workspace.Standard is not null)
            _selectedRelease = Releases.FirstOrDefault(r => r.FileName == Workspace.Standard.SourceFileName);
        Categories.Clear();
        Categories.Add("All");
        if (Workspace.Standard is not null)
            foreach (var c in Workspace.Standard.Controls.Select(c => c.Category).Distinct()) Categories.Add(c);
        if (!Categories.Contains(_category)) _category = "All";
        ApplyFilter();
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SelectedRelease));
        OnPropertyChanged(nameof(Category));
    }

    private void ApplyFilter()
    {
        Controls.Clear();
        if (Workspace.Standard is null) return;
        var q = Search.Trim();
        foreach (var c in Workspace.Standard.Controls)
        {
            if (Category != "All" && c.Category != Category) continue;
            if (q.Length > 0 && !c.Id.Contains(q, StringComparison.OrdinalIgnoreCase) && !c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !c.Purpose.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Controls.Add(c);
        }
    }
}
