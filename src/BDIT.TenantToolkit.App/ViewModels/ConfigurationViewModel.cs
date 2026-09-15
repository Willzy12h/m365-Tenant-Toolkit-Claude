using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;
using BDIT.TenantToolkit.Engine.Evidence;
using BDIT.TenantToolkit.Engine.Reports;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class CollectionRow
{
    public string Collection { get; init; } = "";
    public string Api { get; init; } = "";
    public string Status { get; init; } = "";
    public int Count { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class ObjectRow
{
    public string Collection { get; init; } = "";
    public string CollectionKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string ObjectId { get; init; } = "";
    public string Type { get; init; } = "";
    public string State { get; init; } = "";
    public string Targeting { get; init; } = "";
    public JsonObject Item { get; init; } = new();
}

public sealed class ConfigurationViewModel : PageViewModel
{
    private string _filterCollection = "All";
    private string _search = "";
    private ObjectRow? _selectedObject;
    private SnapshotSummary? _selectedStored;
    private NameResolver _names = new();

    public ConfigurationViewModel(ShellViewModel shell) : base(shell, "Configuration")
    {
        CaptureCommand = Command(Workspace.CaptureAsync, () => Workspace.IsConnected && Workspace.Idle);
        LoadStoredCommand = Sync(() => { if (SelectedStored is not null) Workspace.LoadStoredSnapshot(SelectedStored.Id); }, () => SelectedStored is not null && Workspace.Idle);
        ExportJsonCommand = Sync(() => Export(ExportFormat.Json), () => Workspace.Snapshot is not null);
        ExportCsvCommand = Sync(() => Export(ExportFormat.Csv), () => Workspace.Snapshot is not null);
        ExportXlsxCommand = Sync(() => Export(ExportFormat.Xlsx), () => Workspace.Snapshot is not null);
        OpenExportCommand = Sync(() => Infrastructure.ShellFolders.RevealFile(_lastExportFile), () => _lastExportFile.Length > 0);
        CopySummaryCommand = CopyText(() => SnapshotText);
        Refresh();
    }

    public ICommand CaptureCommand { get; }
    public ICommand LoadStoredCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportXlsxCommand { get; }
    public ICommand OpenExportCommand { get; }
    public ICommand CopySummaryCommand { get; }

    public ObservableCollection<CollectionRow> Collections { get; } = new();
    public ObservableCollection<ObjectRow> Objects { get; } = new();
    public ObservableCollection<string> CollectionFilters { get; } = new();
    public ObservableCollection<SnapshotSummary> StoredSnapshots { get; } = new();
    public ObservableCollection<PropertyDifference> SelectedSettings { get; } = new();

    private readonly List<ObjectRow> _allObjects = new();

    public string FilterCollection { get => _filterCollection; set { if (SetProperty(ref _filterCollection, value)) ApplyFilter(); } }
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) ApplyFilter(); } }
    public SnapshotSummary? SelectedStored { get => _selectedStored; set => SetProperty(ref _selectedStored, value); }
    public string LastExport { get; private set; } = "";
    private string _lastExportFile = "";

    public ObjectRow? SelectedObject
    {
        get => _selectedObject;
        set
        {
            if (!SetProperty(ref _selectedObject, value)) return;
            SelectedSettings.Clear();
            if (value is null) return;
            foreach (var (path, node) in CanonicalJson.Leaves(value.Item))
            {
                if (path.StartsWith("_", StringComparison.Ordinal) && path != "_assignments") continue;
                SelectedSettings.Add(new PropertyDifference { Setting = path, Current = _names.Render(node), Standard = "", Match = true });
            }
            OnPropertyChanged(nameof(SelectedObjectJson));
        }
    }

    public string SelectedObjectJson => SelectedObject?.Item.ToJsonString(ToolkitJson.Options) ?? "";

    public string SnapshotText
    {
        get
        {
            var s = Workspace.Snapshot;
            if (s is null) return "No configuration loaded. Connect, then read the tenant configuration. Stored captures can be opened for offline review.";
            var live = Workspace.SnapshotIsLive ? "Live capture" : "Stored capture (offline review - not eligible for deployment)";
            return $"{live} · {s.TenantName} ({s.PrimaryDomain}) · captured {s.CapturedAt} by {s.CapturedBy} · standard {s.StandardRelease} · {(s.Complete ? "complete" : "INCOMPLETE - review collection errors")} · id {s.Id}";
        }
    }

    private void Export(ExportFormat format)
    {
        var snapshot = Workspace.Snapshot ?? throw new ToolkitException("Read the tenant configuration first.");
        _lastExportFile = Workspace.Exporter.ExportSnapshot(snapshot, Workspace.Standard, format);
        var objects = snapshot.Collections.Values.Sum(c => c.Count);
        LastExport = $"Exported {objects} object(s) from {snapshot.Collections.Count(c => c.Value.Status == CaptureStatus.Collected)} collection(s): {_lastExportFile}";
        OnPropertyChanged(nameof(LastExport));
        RaiseAll();
    }

    public override void Refresh()
    {
        Collections.Clear();
        _allObjects.Clear();
        CollectionFilters.Clear();
        CollectionFilters.Add("All");
        StoredSnapshots.Clear();
        if (Workspace.Profile is not null)
        {
            try { foreach (var s in Workspace.Evidence.ListSnapshots(Workspace.Profile.TenantId)) StoredSnapshots.Add(s); }
            catch (ToolkitException ex) { Shell.ShowError(ex); }
        }
        var snapshot = Workspace.Snapshot;
        _names = NameResolver.FromSnapshot(snapshot);
        if (snapshot is not null)
        {
            foreach (var (key, c) in snapshot.Collections)
            {
                var def = Workspace.Standard?.FindCollection(key);
                var label = def?.Label ?? key;
                Collections.Add(new CollectionRow
                {
                    Collection = label,
                    Api = c.Api,
                    Status = c.Status != CaptureStatus.Collected ? "Not collected" : c.DetailIncomplete ? "Partially collected" : c.Count == 0 ? "No objects returned" : "Collected",
                    Count = c.Count,
                    Detail = c.Error ?? (c.DetailIncomplete ? "Assignment or setting details incomplete for at least one object" : "")
                });
                CollectionFilters.Add(label);
                foreach (var item in c.Items)
                {
                    var id = item["id"]?.GetValue<string>() ?? "";
                    _allObjects.Add(new ObjectRow
                    {
                        Collection = label,
                        CollectionKey = key,
                        Name = item[def?.NameProperty ?? "displayName"]?.GetValue<string>() ?? item["displayName"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? item["userPrincipalName"]?.GetValue<string>() ?? id,
                        ObjectId = id,
                        Type = (item["@odata.type"]?.GetValue<string>() ?? label).Replace("#microsoft.graph.", "", StringComparison.Ordinal),
                        State = item["state"]?.GetValue<string>() ?? "",
                        Targeting = _names.AssignmentSummary(item),
                        Item = item
                    });
                }
            }
        }
        if (!CollectionFilters.Contains(_filterCollection)) _filterCollection = "All";
        ApplyFilter();
        OnPropertyChanged(nameof(SnapshotText));
        OnPropertyChanged(nameof(FilterCollection));
    }

    private void ApplyFilter()
    {
        Objects.Clear();
        var q = Search.Trim();
        foreach (var row in _allObjects)
        {
            if (FilterCollection != "All" && row.Collection != FilterCollection) continue;
            if (q.Length > 0 && !row.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !row.ObjectId.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !row.Type.Contains(q, StringComparison.OrdinalIgnoreCase) && !row.Targeting.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Objects.Add(row);
        }
    }
}
