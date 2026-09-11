using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Graph;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Engine.Collection;

public sealed record CollectionProgress(string Collection, string Label, int Completed, int Total, string Message);

/// <summary>
/// Reads every collection declared by the standard into a snapshot. Failures are recorded per collection and per
/// object detail (assignments, child settings, relationships) and never converted into "absent".
/// </summary>
public sealed class TenantCollector
{
    public const string AssignmentsKey = "_assignments";
    public const string AssignmentsUnknownKey = "_assignmentsUnknown";
    public const string SettingsKey = "_settings";
    public const string SettingsUnknownKey = "_settingsUnknown";
    public const string RelationshipUnknownKey = "_relationshipUnknown";

    private readonly IToolkitLog _log;
    private readonly IClock _clock;
    private readonly string _toolkitVersion;

    public TenantCollector(IToolkitLog log, IClock clock, string toolkitVersion)
    {
        _log = log;
        _clock = clock;
        _toolkitVersion = toolkitVersion;
    }

    public async Task<TenantSnapshot> CollectAsync(IGraphClient graph, TenantSession session, TenantProfile profile, StandardCatalogue standard, IProgress<CollectionProgress>? progress, CancellationToken ct)
    {
        if (!string.Equals(graph.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase) || !string.Equals(session.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("The connected tenant does not match the selected profile.");

        var snapshot = new TenantSnapshot
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = profile.TenantId,
            TenantName = session.TenantName,
            PrimaryDomain = session.PrimaryDomain,
            ClientLabel = profile.Company,
            CapturedAt = Timestamps.Format(_clock.UtcNow),
            CapturedBy = session.Account,
            SessionMode = session.Mode.ToString(),
            StandardRelease = standard.Release,
            ToolkitVersion = _toolkitVersion,
            IdentitySource = session.TenantVerified ? "Authenticated Microsoft Graph organisation (verified)" : "Not verified"
        };

        var total = standard.Collections.Count;
        var completed = 0;
        foreach (var (key, def) in standard.Collections)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CollectionProgress(key, def.Label, completed, total, $"Collecting {def.Label}"));
            var capture = new CollectionCapture { Api = def.ApiVersion == GraphApi.Beta ? "beta" : "v1.0", Path = def.Path };
            try
            {
                var items = new List<JsonObject>();
                if (def.Singleton)
                    items.Add(await graph.GetAsync(def.ApiVersion, def.Path, ct));
                else
                    items.AddRange(await graph.GetAllAsync(def.ApiVersion, def.Path, ct));

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var id = item["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id)) continue;
                    var objectPath = def.BasePath.TrimEnd('/') + "/" + Uri.EscapeDataString(id);
                    if (def.Assignments)
                    {
                        try { item[AssignmentsKey] = ToArray(await graph.GetAllAsync(def.ApiVersion, objectPath + "/assignments", ct)); }
                        catch (ToolkitException ex) { item[AssignmentsUnknownKey] = true; _log.Warn("Collect", $"{def.Label}: assignments unavailable for {id}: {ex.Message}", profile.TenantId); }
                    }
                    if (!string.IsNullOrEmpty(def.Relationship))
                    {
                        var relationshipName = def.Relationship.Split('?')[0].Trim('/');
                        try { item[relationshipName] = ToArray(await graph.GetAllAsync(def.ApiVersion, objectPath + "/" + def.Relationship, ct)); }
                        catch (ToolkitException ex) { item[RelationshipUnknownKey] = true; _log.Warn("Collect", $"{def.Label}: {relationshipName} unavailable for {id}: {ex.Message}", profile.TenantId); }
                    }
                    if (!string.IsNullOrEmpty(def.Children))
                    {
                        try { item[SettingsKey] = ToArray(await graph.GetAllAsync(def.ApiVersion, objectPath + "/" + def.Children.Trim('/'), ct)); }
                        catch (ToolkitException ex) { item[SettingsUnknownKey] = true; _log.Warn("Collect", $"{def.Label}: child settings unavailable for {id}: {ex.Message}", profile.TenantId); }
                    }
                }

                capture.Status = CaptureStatus.Collected;
                capture.Items = items;
                capture.Count = items.Count;
                capture.DetailIncomplete = items.Any(i => i[AssignmentsUnknownKey] is not null || i[SettingsUnknownKey] is not null || i[RelationshipUnknownKey] is not null);
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolkitException ex)
            {
                capture.Status = CaptureStatus.Error;
                capture.Error = ex.Message;
                capture.Items = new List<JsonObject>();
                _log.Warn("Collect", $"{def.Label} could not be collected: {ex.Message}", profile.TenantId);
            }
            snapshot.Collections[key] = capture;
            completed++;
            progress?.Report(new CollectionProgress(key, def.Label, completed, total,
                $"{def.Label}: {(capture.Status == CaptureStatus.Collected ? capture.Count + " object(s)" : "not collected")}{(capture.DetailIncomplete ? " (details incomplete)" : "")}"));
        }

        snapshot.Complete = snapshot.Collections.Values.All(c => c.Usable);
        _log.Info("Collect", $"Snapshot {snapshot.Id} captured: {snapshot.Collections.Count(c => c.Value.Status == CaptureStatus.Collected)}/{snapshot.Collections.Count} collections, complete={snapshot.Complete}.", profile.TenantId);
        return snapshot;
    }

    private static JsonArray ToArray(IReadOnlyList<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return array;
    }
}
