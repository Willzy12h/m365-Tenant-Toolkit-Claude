using System.Collections.ObjectModel;
using System.Windows.Input;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class DeviationsViewModel : PageViewModel
{
    private Deviation? _selected;
    private string _controlId = "", _kind = "ApprovedDeviation", _reason = "", _approvedState = "", _owner = "", _approvedBy = "", _reviewBy = "", _reference = "", _notes = "";

    public DeviationsViewModel(ShellViewModel shell) : base(shell, "Deviations")
    {
        NewCommand = Sync(ResetForm);
        SaveCommand = Sync(Save, () => Workspace.Profile is not null && Workspace.Idle);
        DeleteCommand = Sync(Delete, () => Selected is not null && Workspace.Idle);
        ReviewBy = DateOnly.FromDateTime(DateTime.Today).AddMonths(6).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Refresh();
    }

    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }

    public ObservableCollection<Deviation> Items { get; } = new();
    public ObservableCollection<string> ControlIds { get; } = new();
    public ObservableCollection<string> Kinds { get; } = new() { "ApprovedDeviation", "NotApplicable" };

    public Deviation? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value is null) return;
            ControlId = value.ControlId; Kind = value.Kind.ToString(); Reason = value.Reason; ApprovedState = value.ApprovedState; Owner = value.Owner;
            ApprovedBy = value.ApprovedBy; ReviewBy = value.ReviewBy; Reference = value.Reference; Notes = value.Notes;
        }
    }

    public string ControlId { get => _controlId; set => SetProperty(ref _controlId, value); }
    public string Kind { get => _kind; set => SetProperty(ref _kind, value); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public string ApprovedState { get => _approvedState; set => SetProperty(ref _approvedState, value); }
    public string Owner { get => _owner; set => SetProperty(ref _owner, value); }
    public string ApprovedBy { get => _approvedBy; set => SetProperty(ref _approvedBy, value); }
    public string ReviewBy { get => _reviewBy; set => SetProperty(ref _reviewBy, value); }
    public string Reference { get => _reference; set => SetProperty(ref _reference, value); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value); }

    public string StatusText => Workspace.Profile is null ? "Select a client on the Connect page to manage its deviation register."
        : Items.Count == 0 ? $"No deviations recorded for {Workspace.Profile.Company}."
        : $"{Items.Count} deviation(s) recorded for {Workspace.Profile.Company}; {Items.Count(i => i.IsReviewOverdue(DateTimeOffset.UtcNow))} overdue for review.";

    private void ResetForm()
    {
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        ControlId = ""; Kind = "ApprovedDeviation"; Reason = ""; ApprovedState = ""; Owner = ""; ApprovedBy = Workspace.Session?.Account ?? ""; Reference = ""; Notes = "";
        ReviewBy = DateOnly.FromDateTime(DateTime.Today).AddMonths(6).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(ControlId)) throw new ToolkitException("Select a control.");
        Workspace.SaveDeviation(new Deviation
        {
            Id = Selected?.Id ?? "",
            ControlId = ControlId.Trim(),
            Kind = Kind == "NotApplicable" ? DeviationKind.NotApplicable : DeviationKind.ApprovedDeviation,
            Reason = Reason.Trim(),
            ApprovedState = ApprovedState.Trim(),
            Owner = Owner.Trim(),
            ApprovedBy = ApprovedBy.Trim(),
            ReviewBy = ReviewBy.Trim(),
            Reference = Reference.Trim(),
            Notes = Notes.Trim()
        });
        ResetForm();
    }

    private void Delete()
    {
        var d = Selected ?? throw new ToolkitException("Select a deviation first.");
        var confirm = System.Windows.MessageBox.Show($"Remove the deviation for {d.ControlId}?", "Deviation register", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
        Workspace.DeleteDeviation(d.ControlId);
        ResetForm();
    }

    public override void Refresh()
    {
        Items.Clear();
        ControlIds.Clear();
        if (Workspace.Standard is not null) foreach (var c in Workspace.Standard.Controls) ControlIds.Add(c.Id);
        try { foreach (var d in Workspace.LoadDeviations().OrderBy(d => d.ControlId, StringComparer.OrdinalIgnoreCase)) Items.Add(d); }
        catch (ToolkitException ex) { Shell.ShowError(ex); }
        if (ApprovedBy.Length == 0 && Workspace.Session is not null) ApprovedBy = Workspace.Session.Account;
        OnPropertyChanged(nameof(StatusText));
    }
}
