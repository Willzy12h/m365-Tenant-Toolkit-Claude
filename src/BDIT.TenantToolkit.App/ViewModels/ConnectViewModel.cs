using System.Collections.ObjectModel;
using System.Windows.Input;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class ConnectViewModel : PageViewModel
{
    private TenantProfile? _selected;
    private string _editId = "", _editCompany = "", _editTenantId = "", _editDomain = "", _editAssessmentClientId = "", _editDeploymentClientId = "";
    private string _editEmergencyIds = "", _editOfficeLocationId = "", _editMamGroupId = "", _editPilotGroupId = "", _editCaExclusionGroupId = "", _editNotes = "";
    private string _accessSummary = "";

    public ConnectViewModel(ShellViewModel shell) : base(shell, "Connect")
    {
        NewProfileCommand = Sync(ResetForm);
        SaveProfileCommand = Sync(SaveProfile);
        DeleteProfileCommand = Command(DeleteProfileAsync, () => Selected is not null && Workspace.Idle);
        ConnectAssessmentCommand = Command(() => ConnectAsync(SessionMode.Assessment), () => Workspace.Idle);
        ConnectSelectedCommand = Command(ConnectSelectedAsync, () => Selected is not null && Workspace.Idle);
        CopyApplicationCommand = CopyText(() => ApplicationText);
        CopyAccessCommand = CopyText(AccessReportText);
        ConnectDeploymentCommand = Command(() => ConnectAsync(SessionMode.Deployment), () => Workspace.Idle);
        CheckAccessCommand = Command(Workspace.CheckAccessAsync, () => Workspace.IsConnected && Workspace.Idle);
        Refresh();
    }

    public ObservableCollection<TenantProfile> Profiles => Workspace.Profiles;
    public ObservableCollection<string> AccessRoles { get; } = new();
    public ObservableCollection<AccessCheck> AccessReads { get; } = new();
    public ObservableCollection<AccessCheck> AccessWrites { get; } = new();
    public ObservableCollection<string> AccessNotes { get; } = new();

    public ICommand NewProfileCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand ConnectAssessmentCommand { get; }
    public ICommand ConnectDeploymentCommand { get; }
    public ICommand ConnectSelectedCommand { get; }
    public ICommand CheckAccessCommand { get; }
    public ICommand CopyApplicationCommand { get; }
    public ICommand CopyAccessCommand { get; }

    /// <summary>Names the client the buttons below will act on, so "selected" is never ambiguous.</summary>
    public string SelectionText => Selected is null
        ? "No saved client selected. Choose one above to edit, connect or remove it, or use New client to add one."
        : $"Selected: {Selected.Company} · tenant {Selected.TenantId}. The details below are this client's saved values.";

    /// <summary>The access check rendered as text, for pasting into a ticket or a message.</summary>
    private string AccessReportText()
    {
        var a = Workspace.Access;
        if (a is null) return AccessSummary;
        var lines = new List<string> { AccessSummary, "", "Roles observed:" };
        lines.AddRange(AccessRoles.Count == 0 ? new[] { "   none observed" } : AccessRoles.Select(r => "   " + r));
        lines.Add("");
        lines.Add("Read access by collection:");
        lines.AddRange(a.Reads.Select(r => $"   [{r.Status}] {r.Label} ({r.Scope}) - {r.Detail}"));
        if (a.Writes.Count > 0)
        {
            lines.Add("");
            lines.Add("Write scopes:");
            lines.AddRange(a.Writes.Select(w => $"   [{w.Status}] {w.Label} ({w.Scope}) - {w.Detail}"));
        }
        lines.Add("");
        lines.AddRange(AccessNotes.Select(n => "Note: " + n));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Selecting a saved client always reloads the form, even when the same profile is selected again: the form is the
    /// edit buffer and may hold unsaved or blank values, so re-selecting must be a reliable way to get back to the
    /// stored values. Never assign the backing field directly - a silently selected profile with a blank form causes
    /// a save to create a duplicate client instead of updating the selected one.
    /// </summary>
    public TenantProfile? Selected
    {
        get => _selected;
        set
        {
            SetProperty(ref _selected, value);
            LoadForm(value);
        }
    }

    public string EditId { get => _editId; set => SetProperty(ref _editId, value); }
    public string EditCompany { get => _editCompany; set => SetProperty(ref _editCompany, value); }
    public string EditTenantId { get => _editTenantId; set => SetProperty(ref _editTenantId, value); }
    public string EditDomain { get => _editDomain; set => SetProperty(ref _editDomain, value); }
    public string EditAssessmentClientId { get => _editAssessmentClientId; set => SetProperty(ref _editAssessmentClientId, value); }
    public string EditDeploymentClientId { get => _editDeploymentClientId; set => SetProperty(ref _editDeploymentClientId, value); }
    public string EditEmergencyIds { get => _editEmergencyIds; set => SetProperty(ref _editEmergencyIds, value); }
    public string EditOfficeLocationId { get => _editOfficeLocationId; set => SetProperty(ref _editOfficeLocationId, value); }
    public string EditMamGroupId { get => _editMamGroupId; set => SetProperty(ref _editMamGroupId, value); }
    public string EditPilotGroupId { get => _editPilotGroupId; set => SetProperty(ref _editPilotGroupId, value); }
    public string EditCaExclusionGroupId { get => _editCaExclusionGroupId; set => SetProperty(ref _editCaExclusionGroupId, value); }
    public string EditNotes { get => _editNotes; set => SetProperty(ref _editNotes, value); }
    public string AccessSummary { get => _accessSummary; private set => SetProperty(ref _accessSummary, value); }

    public string ApplicationText
    {
        get
        {
            var profile = Selected;
            var assessment = Workspace.Settings.ResolveClient(SessionMode.Assessment, profile);
            var deployment = Workspace.Settings.ResolveClient(SessionMode.Deployment, profile);
            return "Assessment sign-in uses: " + (assessment is null ? "nothing configured (set assessmentClientId in config/toolkit.settings.json)" : $"{assessment.Value.Label} ({assessment.Value.ClientId})")
                + Environment.NewLine + "Deployment sign-in uses: " + (deployment is null ? "not configured - deployment unavailable until deploymentClientId is set" : $"{deployment.Value.Label} ({deployment.Value.ClientId})");
        }
    }

    public string ConnectionText
    {
        get
        {
            var s = Workspace.Session;
            if (s is null) return "Not connected. Sign in with your tenant account; Microsoft handles credentials and MFA in your browser. Local administrator rights are not required.";
            return $"Connected to {s.TenantName} ({s.PrimaryDomain}) as {s.Account} in {s.Mode} mode via {s.ClientLabel}.";
        }
    }

    private void ResetForm()
    {
        Selected = null;
        LoadForm(null);
    }

    private void LoadForm(TenantProfile? p)
    {
        EditId = p?.Id ?? "";
        EditCompany = p?.Company ?? "";
        EditTenantId = p?.TenantId ?? "";
        EditDomain = p?.Domain ?? "";
        EditAssessmentClientId = p?.AssessmentClientId ?? "";
        EditDeploymentClientId = p?.DeploymentClientId ?? "";
        EditEmergencyIds = p is null ? "" : string.Join(", ", p.Parameters.EmergencyAccountIds);
        EditOfficeLocationId = p?.Parameters.OfficeLocationId ?? "";
        EditMamGroupId = p?.Parameters.MamGroupId ?? "";
        EditPilotGroupId = p?.Parameters.PilotGroupId ?? "";
        EditCaExclusionGroupId = p?.Parameters.CaExclusionGroupId ?? "";
        EditNotes = p?.Notes ?? "";
        OnPropertyChanged(nameof(ApplicationText));
    }

    private TenantProfile FormToProfile() => new()
    {
        Id = EditId,
        Company = EditCompany,
        TenantId = EditTenantId,
        Domain = EditDomain,
        AssessmentClientId = EditAssessmentClientId,
        DeploymentClientId = EditDeploymentClientId,
        Notes = EditNotes,
        CreatedAt = Selected?.CreatedAt ?? "",
        Parameters = new TenantParameters
        {
            EmergencyAccountIds = EditEmergencyIds.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
            OfficeLocationId = EditOfficeLocationId,
            MamGroupId = EditMamGroupId,
            PilotGroupId = EditPilotGroupId,
            CaExclusionGroupId = EditCaExclusionGroupId
        }
    };

    private void SaveProfile()
    {
        var saved = Workspace.SaveProfile(FormToProfile());
        Selected = Profiles.FirstOrDefault(p => p.Id == saved.Id);
    }

    private async Task DeleteProfileAsync()
    {
        var selected = Selected ?? throw new ToolkitException("Select a saved client first.");
        var confirm = System.Windows.MessageBox.Show(
            $"Remove '{selected.Company}' from this computer?\n\nSaved captures, run evidence and tenant objects are retained. The active session (if any) will disconnect.",
            "Delete saved client", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
        await Workspace.DeleteProfileAsync(selected.Id);
        ResetForm();
    }

    /// <summary>
    /// Connects to the saved client as stored, ignoring the edit form. Re-connecting to a client you saved earlier
    /// should never depend on what the form happens to contain.
    /// </summary>
    private async Task ConnectSelectedAsync()
    {
        var profile = Selected ?? throw new ToolkitException("Select a saved client first.");
        LoadForm(profile);
        await Workspace.ConnectAsync(profile, SessionMode.Assessment);
    }

    private async Task ConnectAsync(SessionMode mode)
    {
        var profile = Workspace.SaveProfile(FormToProfile());
        Selected = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        if (mode == SessionMode.Deployment)
        {
            var confirm = System.Windows.MessageBox.Show(
                "Deployment access signs you in again with the BDIT Tenant Deployment application and requests write permissions.\n\n" +
                "Nothing is written until you build a plan, acknowledge the before-change snapshot and confirm the tenant ID. Continue?",
                "Enable deployment access", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }
        await Workspace.ConnectAsync(profile, mode);
    }

    public override void Refresh()
    {
        if (Selected is null && Workspace.Profile is not null)
            Selected = Profiles.FirstOrDefault(p => p.Id == Workspace.Profile.Id);
        AccessRoles.Clear();
        AccessReads.Clear();
        AccessWrites.Clear();
        AccessNotes.Clear();
        var a = Workspace.Access;
        if (a is null)
        {
            AccessSummary = Workspace.IsConnected ? "Access check not yet run." : "Access checks appear after sign-in. They create no test objects.";
        }
        else
        {
            foreach (var r in a.Roles) AccessRoles.Add($"{r.Name} (scope {r.Scope})");
            foreach (var r in a.Reads) AccessReads.Add(r);
            foreach (var w in a.Writes) AccessWrites.Add(w);
            foreach (var n in a.Notes) AccessNotes.Add(n);
            if (a.RoleError is not null) AccessNotes.Add("Role inspection: " + a.RoleError);
            AccessSummary = $"Checked {a.At} for {a.Account} · Global Administrator: {a.GlobalAdministrator} · {a.CandidateRecipes} automated recipes, {a.ManualControls} manual controls.";
        }
        RaiseAll();
    }
}
