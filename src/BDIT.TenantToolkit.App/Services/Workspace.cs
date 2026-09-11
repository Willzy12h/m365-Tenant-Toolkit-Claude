using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using BDIT.TenantToolkit.App.Infrastructure;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Configuration;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine;
using BDIT.TenantToolkit.Engine.Assessment;
using BDIT.TenantToolkit.Engine.Collection;
using BDIT.TenantToolkit.Engine.Drift;
using BDIT.TenantToolkit.Engine.Evidence;
using BDIT.TenantToolkit.Engine.Execution;
using BDIT.TenantToolkit.Engine.Planning;
using BDIT.TenantToolkit.Engine.Reports;
using BDIT.TenantToolkit.Engine.Standards;
using BDIT.TenantToolkit.Graph;

namespace BDIT.TenantToolkit.App.Services;

/// <summary>
/// Composition root and single source of truth for the UI. Every tenant operation runs through <see cref="RunExclusiveAsync"/>
/// so only one operation touches the connection at a time, and every state change raises <see cref="StateChanged"/>.
/// The workspace mirrors the engineer workflow: connect, capture, assess, plan, acknowledge, deploy, review evidence.
/// </summary>
public sealed class Workspace : ObservableObject
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private bool _busy;
    private string _busyMessage = "";
    private string _progressDetail = "";

    public ToolkitPaths Paths { get; }
    public ToolkitSettings Settings { get; }
    public ToolkitLogger Logger { get; }
    public bool Diagnostics { get; }
    public string Version => ToolkitVersion.Current;

    public EvidenceStore Evidence { get; }
    public StandardsLoader Standards { get; }
    public TenantConnectionService Connections { get; }
    public TenantCollector Collector { get; }
    public AssessmentEngine Engine { get; }
    public DeploymentPlanner Planner { get; }
    public DeploymentExecutor Executor { get; }
    public DriftAnalyser Drift { get; }
    public ReportExporter Exporter { get; }

    public ObservableCollection<TenantProfile> Profiles { get; } = new();
    public ObservableCollection<StandardRelease> Releases { get; } = new();
    public ObservableCollection<LogEntry> Activity { get; } = new();

    public StandardCatalogue? Standard { get; private set; }
    public string? StandardError { get; private set; }
    public TenantProfile? Profile { get; private set; }
    public ConnectedTenant? Connection { get; private set; }
    public TenantSession? Session => Connection?.Session;
    public AccessReport? Access { get; private set; }
    public TenantSnapshot? Snapshot { get; private set; }
    public bool SnapshotIsLive { get; private set; }
    public AssessmentResult? Assessment { get; private set; }
    public DeploymentPlan? Plan { get; private set; }
    public string? AcknowledgedSnapshotId { get; private set; }
    public DeploymentRun? LastRun { get; private set; }
    public DeploymentControl? Control { get; private set; }

    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(Idle)); } }
    public bool Idle => !_busy;
    public string BusyMessage { get => _busyMessage; private set => SetProperty(ref _busyMessage, value); }
    public string ProgressDetail { get => _progressDetail; private set => SetProperty(ref _progressDetail, value); }

    public bool IsConnected => Connection is not null;
    public bool IsDeploymentSession => Session?.Mode == SessionMode.Deployment;

    public event Action? StateChanged;

    public Workspace(ToolkitPaths paths, ToolkitSettings settings, ToolkitLogger logger, bool diagnostics)
    {
        Paths = paths;
        Settings = settings;
        Logger = logger;
        Diagnostics = diagnostics;
        Evidence = new EvidenceStore(paths, logger);
        Standards = new StandardsLoader(paths, logger);
        Connections = new TenantConnectionService(settings, paths, _http, logger, ToolkitVersion.Current);
        Collector = new TenantCollector(logger, SystemClock.Instance, ToolkitVersion.Current);
        Engine = new AssessmentEngine(SystemClock.Instance, ToolkitVersion.Current);
        Planner = new DeploymentPlanner(SystemClock.Instance, ToolkitVersion.Current);
        Executor = new DeploymentExecutor(Evidence, Collector, logger, SystemClock.Instance, ToolkitVersion.Current);
        Drift = new DriftAnalyser(SystemClock.Instance, Engine);
        Exporter = new ReportExporter(paths, settings.CompanyName);
        logger.EntryWritten += OnLogEntry;
    }

    private void OnLogEntry(LogEntry entry)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            Activity.Add(entry);
            while (Activity.Count > 500) Activity.RemoveAt(0);
        });
    }

    public void Initialise()
    {
        foreach (var p in Evidence.LoadProfiles()) Profiles.Add(p);
        foreach (var r in Standards.ListReleases()) Releases.Add(r);
        var preferred = Releases.FirstOrDefault(r => string.Equals(r.Release, Settings.DefaultStandardRelease, StringComparison.OrdinalIgnoreCase)) ?? Releases.FirstOrDefault();
        if (preferred is not null) TrySelectStandard(preferred.FileName);
        foreach (var p in Profiles)
        {
            try { Evidence.MarkInterruptedRuns(p.TenantId); }
            catch (ToolkitException ex) { Logger.Warn("App", $"Could not review previous runs for {p.Company}: {ex.Message}"); }
        }
        Notify();
    }

    public bool TrySelectStandard(string fileName)
    {
        try
        {
            Standard = Standards.Load(fileName);
            StandardError = null;
            Plan = null;
            AcknowledgedSnapshotId = null;
            Assessment = null;
            Notify();
            return true;
        }
        catch (ToolkitException ex)
        {
            Standard = null;
            StandardError = ex.Message;
            Logger.Error("Standards", ex.Message, ex);
            Notify();
            return false;
        }
    }

    public StandardCatalogue RequireStandard() =>
        Standard ?? throw new ConfigurationException(StandardError ?? "No Build Standard release is loaded. Check the standards folder and manifest.");

    private void Notify()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDeploymentSession));
        StateChanged?.Invoke();
    }

    // ---- exclusive operations --------------------------------------------------------------------------------------

    public async Task RunExclusiveAsync(string message, Func<IProgress<string>, Task> operation)
    {
        if (Busy) throw new ToolkitException("Another operation is already running. Wait for it to finish.");
        Busy = true;
        BusyMessage = message;
        ProgressDetail = "";
        var progress = new Progress<string>(p => ProgressDetail = p);
        try { await operation(progress); }
        finally
        {
            Busy = false;
            BusyMessage = "";
            ProgressDetail = "";
            Notify();
        }
    }

    // ---- profiles ------------------------------------------------------------------------------------------------

    public TenantProfile SaveProfile(TenantProfile input)
    {
        var profile = ProfileValidator.Validate(input, DateTimeOffset.UtcNow);
        var all = Profiles.ToList();
        // One saved client per tenant: evidence, managed-object mappings and deviations are all keyed by tenant ID, so a
        // second profile for the same tenant would silently split a client's history in two.
        var sameTenant = all.FirstOrDefault(p => string.Equals(p.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase) && p.Id != profile.Id);
        if (sameTenant is not null)
            throw new ConfigurationException($"'{sameTenant.Company}' is already saved for tenant {profile.TenantId}. Select it in the saved clients list to edit or connect to it rather than creating a second entry.");
        var index = all.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            profile.CreatedAt = all[index].CreatedAt;
            all[index] = profile;
        }
        else all.Add(profile);
        Evidence.SaveProfiles(all);
        Profiles.Clear();
        foreach (var p in all) Profiles.Add(p);
        if (Profile?.Id == profile.Id)
        {
            Profile = profile;
            Plan = null;
            AcknowledgedSnapshotId = null;
        }
        Logger.Info("Profiles", $"Saved profile '{profile.Company}' for tenant {profile.TenantId}.", profile.TenantId);
        Notify();
        return profile;
    }

    public async Task DeleteProfileAsync(string id)
    {
        if (Busy) throw new ToolkitException("Wait for the active operation before deleting a profile.");
        if (Profile?.Id == id) await DisconnectAsync();
        var remaining = Profiles.Where(p => p.Id != id).ToList();
        Evidence.SaveProfiles(remaining);
        Profiles.Clear();
        foreach (var p in remaining) Profiles.Add(p);
        Logger.Info("Profiles", "Saved connection removed. Tenant objects and saved evidence are retained.");
        Notify();
    }

    // ---- connection ----------------------------------------------------------------------------------------------

    public Task ConnectAsync(TenantProfile profile, SessionMode mode) => RunExclusiveAsync(
        mode == SessionMode.Deployment ? "Connecting with deployment access" : "Connecting (read-only)", async progress =>
        {
            var standard = RequireStandard();
            await DisconnectCoreAsync();
            Profile = profile;
            Connection = await Connections.ConnectAsync(profile, mode, standard, progress, CancellationToken.None);
            Evidence.MarkInterruptedRuns(profile.TenantId);
            progress.Report("Checking access (read-only).");
            Access = await Connections.CheckAccessAsync(Connection, standard, progress, CancellationToken.None);
        });

    public Task CheckAccessAsync() => RunExclusiveAsync("Checking access", async progress =>
    {
        var connection = RequireConnection();
        Access = await Connections.CheckAccessAsync(connection, RequireStandard(), progress, CancellationToken.None);
        Plan = null;
        AcknowledgedSnapshotId = null;
    });

    public async Task DisconnectAsync()
    {
        if (Busy) throw new ToolkitException("Stop the active run and wait for evidence collection before disconnecting.");
        await DisconnectCoreAsync();
        Notify();
    }

    private async Task DisconnectCoreAsync()
    {
        var connection = Connection;
        Connection = null;
        Access = null;
        Snapshot = null;
        SnapshotIsLive = false;
        Assessment = null;
        Plan = null;
        AcknowledgedSnapshotId = null;
        Control = null;
        if (connection is not null) await connection.DisposeAsync();
    }

    public ConnectedTenant RequireConnection()
    {
        var connection = Connection ?? throw new ToolkitException("Connect to a tenant first.");
        if (Profile is null || !string.Equals(connection.Session.TenantId, Profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("The connected tenant does not match the selected client profile.");
        return connection;
    }

    // ---- capture, assess, plan, deploy ---------------------------------------------------------------------------------

    public Task CaptureAsync() => RunExclusiveAsync("Reading tenant configuration", async progress =>
    {
        var connection = RequireConnection();
        var standard = RequireStandard();
        Plan = null;
        AcknowledgedSnapshotId = null;
        var collectionProgress = new Progress<CollectionProgress>(p => ((IProgress<string>)progress).Report($"{p.Message} ({p.Completed}/{p.Total})"));
        var snapshot = await Collector.CollectAsync(connection.Graph, connection.Session, Profile!, standard, collectionProgress, CancellationToken.None);
        Evidence.SaveSnapshot(snapshot);
        Snapshot = snapshot;
        SnapshotIsLive = true;
        Assessment = null;
        progress.Report("Comparing with the Build Standard.");
        RunAssessment();
    });

    /// <summary>Loads a stored snapshot for offline review. It is never eligible for deployment.</summary>
    public void LoadStoredSnapshot(string snapshotId)
    {
        var profile = Profile ?? throw new ToolkitException("Select a client first.");
        Snapshot = Evidence.LoadSnapshot(profile.TenantId, snapshotId) ?? throw new ToolkitException("Snapshot not found.");
        SnapshotIsLive = false;
        Plan = null;
        AcknowledgedSnapshotId = null;
        RunAssessment();
        Notify();
    }

    public void RunAssessment()
    {
        var profile = Profile ?? throw new ToolkitException("Select a client first.");
        var snapshot = Snapshot ?? throw new ToolkitException("Read the tenant configuration first.");
        var standard = RequireStandard();
        var mappings = Evidence.LoadMappings(profile.TenantId);
        var deviations = Evidence.LoadDeviations(profile.TenantId);
        Assessment = Engine.Assess(snapshot, standard, profile, mappings, deviations, Session?.Account ?? "offline review");
        Evidence.SaveAssessment(Assessment);
        Logger.Info("Assessment", $"Assessment {Assessment.Id}: {Assessment.Summary.Compliant} compliant, {Assessment.Summary.Missing} missing, {Assessment.Summary.PartialMatch} partial, {Assessment.Summary.UnableToAssess} unknown.", profile.TenantId);
        Notify();
    }

    public DeploymentPlan BuildPlan(IReadOnlyList<string> controlIds)
    {
        if (Busy) throw new ToolkitException("Wait for the active operation.");
        var connection = RequireConnection();
        var snapshot = Snapshot ?? throw new ToolkitException("Read the tenant configuration first.");
        if (!SnapshotIsLive) throw new ToolkitException("A stored snapshot is loaded for review. Read the live tenant configuration before planning.");
        var profile = Profile!;
        var plan = Planner.Build(new PlanRequest
        {
            Profile = profile,
            Standard = RequireStandard(),
            Snapshot = snapshot,
            Mappings = Evidence.LoadMappings(profile.TenantId),
            Deviations = Evidence.LoadDeviations(profile.TenantId),
            SelectedControlIds = controlIds,
            Session = connection.Session
        });
        Evidence.SavePlan(plan);
        Plan = plan;
        AcknowledgedSnapshotId = null;
        Logger.Info("Plan", $"Plan {plan.Id} built: {plan.Rows.Count(r => r.Action == PlanAction.Create)} create, {plan.Rows.Count(r => r.Action == PlanAction.Update)} update, {plan.Rows.Count(r => !r.IsWrite)} not automated.", profile.TenantId);
        Notify();
        return plan;
    }

    public void AcknowledgeSnapshot()
    {
        var snapshot = Snapshot ?? throw new ToolkitException("Capture a snapshot first.");
        if (!SnapshotIsLive) throw new ToolkitException("Only the live capture can be acknowledged for deployment.");
        AcknowledgedSnapshotId = snapshot.Id;
        Logger.Info("Plan", $"Before-change snapshot {snapshot.Id} acknowledged by the engineer.", snapshot.TenantId);
        Notify();
    }

    public void ValidatePlanForExecution()
    {
        var connection = RequireConnection();
        var plan = Plan ?? throw new ToolkitException("Build and review a plan first.");
        var snapshot = Snapshot ?? throw new ToolkitException("Capture a snapshot first.");
        var profile = Profile!;
        DeploymentPlanner.Validate(plan, new PlanValidationContext
        {
            Profile = profile,
            Standard = RequireStandard(),
            Snapshot = snapshot,
            Mappings = Evidence.LoadMappings(profile.TenantId),
            Session = connection.Session,
            AcknowledgedSnapshotId = AcknowledgedSnapshotId,
            Now = DateTimeOffset.UtcNow,
            MaxSnapshotAge = TimeSpan.FromMinutes(Settings.SnapshotMaxAgeMinutes),
            MaxPlanAge = TimeSpan.FromMinutes(Settings.PlanMaxAgeMinutes)
        });
        if (Access is not null)
        {
            var missing = plan.WriteRows.Select(r => r.Collection).Distinct()
                .Where(c => Access.Writes.Any(w => w.Collection == c && w.Status == "Missing scope")).ToList();
            if (missing.Count > 0)
                throw new PlanValidationException("A required delegated write scope is missing for: " + string.Join(", ", missing) + ". Reconnect with deployment access and consent to the write permissions.");
        }
    }

    public Task DeployAsync(string typedTenantId) => RunExclusiveAsync("Deploying reviewed changes", async progress =>
    {
        var connection = RequireConnection();
        var profile = Profile!;
        if (!string.Equals(typedTenantId?.Trim(), profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("The typed tenant ID does not match the connected tenant. Deployment refused.");
        ValidatePlanForExecution();
        var plan = Plan!;
        var snapshot = Snapshot!;
        var mappings = Evidence.LoadMappings(profile.TenantId);
        Control = new DeploymentControl();
        LastRun = await Executor.StartAsync(new ExecutionRequest
        {
            Plan = plan,
            Profile = profile,
            Standard = RequireStandard(),
            Snapshot = snapshot,
            Mappings = mappings,
            Session = connection.Session,
            Graph = connection.Graph
        }, Control, progress);
        Plan = null;
        AcknowledgedSnapshotId = null;
        Snapshot = null;
        SnapshotIsLive = false;
        Assessment = null;
        Control = null;
    });

    public void PauseDeployment() => Control?.Pause();
    public void ResumeDeployment() => Control?.Resume();
    public void StopDeployment() => Control?.Stop();

    // ---- deviations and manual checks ---------------------------------------------------------------------------------

    public IReadOnlyList<Deviation> LoadDeviations() => Profile is null ? Array.Empty<Deviation>() : Evidence.LoadDeviations(Profile.TenantId);

    public void SaveDeviation(Deviation deviation)
    {
        var profile = Profile ?? throw new ToolkitException("Select a client first.");
        var standard = RequireStandard();
        if (standard.FindControl(deviation.ControlId) is null) throw new ConfigurationException($"'{deviation.ControlId}' is not a control in the loaded standard.");
        if (string.IsNullOrWhiteSpace(deviation.Reason) || deviation.Reason.Trim().Length < 8) throw new ConfigurationException("Record a meaningful reason for the deviation (at least 8 characters).");
        if (string.IsNullOrWhiteSpace(deviation.ApprovedBy)) throw new ConfigurationException("Record who approved the deviation.");
        if (deviation.ReviewBy.Length > 0 && !DateOnly.TryParse(deviation.ReviewBy, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new ConfigurationException("Review-by must be a date in yyyy-MM-dd format or empty.");
        deviation.TenantId = profile.TenantId;
        deviation.Id = string.IsNullOrWhiteSpace(deviation.Id) ? Guid.NewGuid().ToString() : deviation.Id;
        deviation.RecordedAt = Timestamps.Format(DateTimeOffset.UtcNow);
        deviation.RecordedByAccount = Session?.Account ?? "local operator - not independently verified";
        var list = Evidence.LoadDeviations(profile.TenantId).Where(d => !string.Equals(d.ControlId, deviation.ControlId, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(deviation);
        Evidence.SaveDeviations(profile.TenantId, list);
        Plan = null;
        AcknowledgedSnapshotId = null;
        Logger.Info("Deviations", $"Deviation recorded for {deviation.ControlId} ({deviation.Kind}).", profile.TenantId, deviation.ControlId);
        if (Snapshot is not null) RunAssessment(); else Notify();
    }

    public void DeleteDeviation(string controlId)
    {
        var profile = Profile ?? throw new ToolkitException("Select a client first.");
        var list = Evidence.LoadDeviations(profile.TenantId).Where(d => !string.Equals(d.ControlId, controlId, StringComparison.OrdinalIgnoreCase)).ToList();
        Evidence.SaveDeviations(profile.TenantId, list);
        Plan = null;
        AcknowledgedSnapshotId = null;
        Logger.Info("Deviations", $"Deviation removed for {controlId}.", profile.TenantId, controlId);
        if (Snapshot is not null) RunAssessment(); else Notify();
    }

    public ManualCheckRegister LoadManualChecks() => Profile is null ? new ManualCheckRegister() : Evidence.LoadManualChecks(Profile.TenantId);

    public void SaveManualCheck(string controlId, string status, string note)
    {
        var profile = Profile ?? throw new ToolkitException("Select a client first.");
        if (RequireStandard().FindControl(controlId) is null) throw new ConfigurationException("Unknown control.");
        if (status is not ("Pending" or "Pass" or "Fail" or "Unknown")) throw new ConfigurationException("Invalid outcome.");
        if (status != "Pending" && (note ?? "").Trim().Length < 8) throw new ConfigurationException("Record an evidence note of at least 8 characters.");
        var register = Evidence.LoadManualChecks(profile.TenantId);
        register.Checks[controlId] = new ManualCheck
        {
            ControlId = controlId,
            Status = status,
            Note = (note ?? "").Trim().Length > 2000 ? note!.Trim()[..2000] : (note ?? "").Trim(),
            RecordedAt = Timestamps.Format(DateTimeOffset.UtcNow),
            RecordedBy = Session?.Account ?? "local operator - not independently verified"
        };
        Evidence.SaveManualChecks(register);
        Notify();
    }

    // ---- drift ---------------------------------------------------------------------------------------------------------

    public DriftReport CompareSnapshots(string beforeId, string afterId)
    {
        var profile = Profile ?? throw new ToolkitException("Select a client first.");
        var before = Evidence.LoadSnapshot(profile.TenantId, beforeId) ?? throw new ToolkitException("Before snapshot not found.");
        var after = Evidence.LoadSnapshot(profile.TenantId, afterId) ?? throw new ToolkitException("After snapshot not found.");
        return Drift.Compare(before, after, RequireStandard(), profile, Evidence.LoadMappings(profile.TenantId), Evidence.LoadDeviations(profile.TenantId));
    }

    // ---- shutdown ------------------------------------------------------------------------------------------------------

    /// <summary>Requests a stop at the next safe boundary, waits for evidence, then removes cached tokens.</summary>
    public async Task ShutdownAsync()
    {
        if (Executor.IsRunning)
        {
            Logger.Warn("App", "Shutdown requested while a deployment is in flight; waiting for the current write and after-change evidence.");
            await Executor.WaitForCompletionAsync(Control);
        }
        await DisconnectCoreAsync();
        Logger.Info("App", "Toolkit closed.");
        Logger.Flush();
    }
}
