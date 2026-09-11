using System.Collections.ObjectModel;
using System.Windows.Input;
using BDIT.TenantToolkit.Core;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class ManualCheckRow
{
    public string ControlId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Status { get; init; } = "Pending";
    public string Note { get; init; } = "";
    public string RecordedAt { get; init; } = "";
    public string RecordedBy { get; init; } = "";
    public string Instructions { get; init; } = "";
}

public sealed class ManualChecksViewModel : PageViewModel
{
    private ManualCheckRow? _selected;
    private string _editStatus = "Pending";
    private string _editNote = "";

    public ManualChecksViewModel(ShellViewModel shell) : base(shell, "Manual checks")
    {
        SaveCommand = Sync(Save, () => Selected is not null && Workspace.Profile is not null && Workspace.Idle);
        Refresh();
    }

    public ICommand SaveCommand { get; }
    public ObservableCollection<ManualCheckRow> Rows { get; } = new();
    public ObservableCollection<string> Statuses { get; } = new() { "Pending", "Pass", "Fail", "Unknown" };

    public ManualCheckRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value is null) return;
            EditStatus = value.Status;
            EditNote = value.Note;
            OnPropertyChanged(nameof(SelectedText));
        }
    }

    public string EditStatus { get => _editStatus; set => SetProperty(ref _editStatus, value); }
    public string EditNote { get => _editNote; set => SetProperty(ref _editNote, value); }
    public string SelectedText => Selected is null ? "Select a control to record what was checked and where the evidence is held. Never record passwords or recovery keys." : $"{Selected.ControlId} {Selected.Name}\n{Selected.Instructions}";
    public string ContextText => Workspace.Profile is null ? "Select a client on the Connect page." : $"Manual checks for {Workspace.Profile.Company}. Outcomes other than Pending require an evidence note.";

    private void Save()
    {
        var row = Selected ?? throw new ToolkitException("Select a control first.");
        Workspace.SaveManualCheck(row.ControlId, EditStatus, EditNote);
        Selected = Rows.FirstOrDefault(r => r.ControlId == row.ControlId);
    }

    public override void Refresh()
    {
        var selectedId = Selected?.ControlId;
        Rows.Clear();
        if (Workspace.Standard is null) return;
        var register = Workspace.LoadManualChecks();
        foreach (var c in Workspace.Standard.Controls)
        {
            register.Checks.TryGetValue(c.Id, out var check);
            Rows.Add(new ManualCheckRow
            {
                ControlId = c.Id,
                Name = c.Name,
                Category = c.Category,
                Status = check?.Status ?? "Pending",
                Note = check?.Note ?? "",
                RecordedAt = check?.RecordedAt ?? "",
                RecordedBy = check?.RecordedBy ?? "",
                Instructions = c.Assessment.ManualInstructions.Length > 0 ? c.Assessment.ManualInstructions : c.DesiredState
            });
        }
        _selected = selectedId is null ? null : Rows.FirstOrDefault(r => r.ControlId == selectedId);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(ContextText));
    }
}
