using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;

namespace BDIT.TenantToolkit.Engine.Drift;

/// <summary>
/// Compares two snapshots of the same tenant object by object and explains each change: what changed, whether the
/// toolkit created the object, and whether the change diverges from what the toolkit last applied. Also compares the
/// control status of both snapshots under one standard release so regressions are named, not just counted.
/// </summary>
public sealed class DriftAnalyser
{
    private static readonly HashSet<string> DroppedKeys = new(StringComparer.Ordinal)
    {
        "@odata.context", "@odata.etag", "lastModifiedDateTime", "modifiedDateTime", "version", "_groupDisplayName", "settingCount"
    };

    private readonly IClock _clock;
    private readonly AssessmentEngine _engine;

    public DriftAnalyser(IClock clock, AssessmentEngine engine)
    {
        _clock = clock;
        _engine = engine;
    }

    public DriftReport Compare(TenantSnapshot before, TenantSnapshot after, StandardCatalogue standard, TenantProfile profile, ManagedObjectMappings mappings, IReadOnlyList<Deviation> deviations)
    {
        if (!string.Equals(before.TenantId, after.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("Both snapshots must belong to the same tenant.");
        if (!string.Equals(before.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new TenantMismatchException("The snapshots belong to a different tenant than the selected client.");

        var names = NameResolver.FromSnapshot(after);
        var report = new DriftReport
        {
            TenantId = after.TenantId,
            TenantName = after.TenantName,
            BeforeSnapshotId = before.Id,
            BeforeCapturedAt = before.CapturedAt,
            BeforeStandardRelease = before.StandardRelease,
            AfterSnapshotId = after.Id,
            AfterCapturedAt = after.CapturedAt,
            AfterStandardRelease = after.StandardRelease,
            AssessedRelease = standard.Release,
            GeneratedAt = Timestamps.Format(_clock.UtcNow),
            StandardReleaseChanged = !string.Equals(before.StandardRelease, after.StandardRelease, StringComparison.OrdinalIgnoreCase)
        };
        if (report.StandardReleaseChanged)
            report.Notes.Add($"The snapshots were captured under different standard releases ({before.StandardRelease} and {after.StandardRelease}). Control status changes may reflect a deliberate standard change rather than tenant drift. Both are assessed here against {standard.Release}.");

        var unchanged = 0;
        foreach (var key in before.Collections.Keys.Union(after.Collections.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var label = standard.FindCollection(key)?.Label ?? key;
            var nameKey = standard.FindCollection(key)?.NameProperty ?? "displayName";
            before.Collections.TryGetValue(key, out var a);
            after.Collections.TryGetValue(key, out var b);
            if (a is null || b is null || !a.Usable || !b.Usable)
            {
                report.Items.Add(new DriftItem
                {
                    Collection = key, CollectionLabel = label, Change = DriftChange.UnableToAssess, Classification = DriftClassification.Unknown,
                    Reason = "Incomplete or missing collection in one or both captures; this collection cannot be compared."
                });
                continue;
            }
            var old = a.Items.Where(i => i["id"] is not null).ToDictionary(i => i["id"]!.GetValue<string>(), i => i, StringComparer.OrdinalIgnoreCase);
            var now = b.Items.Where(i => i["id"] is not null).ToDictionary(i => i["id"]!.GetValue<string>(), i => i, StringComparer.OrdinalIgnoreCase);
            foreach (var id in old.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                old.TryGetValue(id, out var x);
                now.TryGetValue(id, out var y);
                var mapping = mappings.FindByObject(id);
                var item = new DriftItem
                {
                    Collection = key,
                    CollectionLabel = label,
                    ObjectId = id,
                    Name = (y ?? x)?[nameKey]?.GetValue<string>() ?? (y ?? x)?["displayName"]?.GetValue<string>() ?? (y ?? x)?["name"]?.GetValue<string>() ?? id,
                    ToolkitManaged = mapping is not null,
                    ControlId = mapping?.ControlId
                };
                if (x is null)
                {
                    item.Change = DriftChange.Added;
                    item.Classification = mapping is not null ? DriftClassification.None : DriftClassification.ExternalChange;
                    item.Reason = mapping is not null ? "Created by the toolkit." : "Created outside the toolkit.";
                }
                else if (y is null)
                {
                    item.Change = DriftChange.Removed;
                    item.Classification = mapping is not null ? DriftClassification.ManagedObjectRemoved : DriftClassification.ExternalChange;
                    item.Reason = mapping is not null ? "A toolkit-created object has been deleted from the tenant. Confirm whether this was deliberate." : "An object was deleted outside the toolkit.";
                }
                else
                {
                    var nx = CanonicalJson.Normalise(x, DroppedKeys);
                    var ny = CanonicalJson.Normalise(y, DroppedKeys);
                    if (CanonicalJson.Serialize(nx) == CanonicalJson.Serialize(ny))
                    {
                        unchanged++;
                        continue;
                    }
                    item.Change = DriftChange.Changed;
                    item.Differences = Differences(nx, ny, names);
                    if (mapping is null)
                    {
                        item.Classification = DriftClassification.ExternalChange;
                        item.Reason = "Settings, state or assignments changed outside the toolkit.";
                    }
                    else if (mapping.LastApplied is not null && CanonicalJson.IsSubset(y, mapping.LastApplied))
                    {
                        item.Classification = DriftClassification.ManagedObjectMetadataOnly;
                        item.Reason = "Toolkit-created object changed only in properties the toolkit does not manage (for example metadata or server-side fields).";
                    }
                    else
                    {
                        item.Classification = DriftClassification.ManagedObjectModifiedExternally;
                        item.Reason = "Toolkit-created object no longer matches what the toolkit last applied. Something else modified it; the toolkit will not update it until this is reviewed.";
                    }
                }
                report.Items.Add(item);
            }
        }
        report.Notes.Add($"{unchanged} object(s) were unchanged and are not listed.");

        var beforeAssessment = _engine.Assess(before, standard, profile, mappings, deviations, "drift comparison");
        var afterAssessment = _engine.Assess(after, standard, profile, mappings, deviations, "drift comparison");
        foreach (var f in afterAssessment.Findings)
        {
            var previous = beforeAssessment.Findings.FirstOrDefault(p => string.Equals(p.ControlId, f.ControlId, StringComparison.OrdinalIgnoreCase));
            if (previous is null || previous.Status == f.Status) continue;
            var regression = Rank(f.Status) > Rank(previous.Status);
            report.ControlChanges.Add(new ControlStatusChange
            {
                ControlId = f.ControlId,
                Name = f.Name,
                Category = f.Category,
                Severity = f.Severity,
                Before = previous.Status,
                After = f.Status,
                Regression = regression,
                Explanation = Explain(previous, f, report.StandardReleaseChanged)
            });
        }
        report.ControlChanges = report.ControlChanges.OrderByDescending(c => c.Regression).ThenBy(c => c.ControlId, StringComparer.OrdinalIgnoreCase).ToList();
        return report;
    }

    private static int Rank(FindingStatus status) => status switch
    {
        FindingStatus.Compliant or FindingStatus.CompliantWithDeviation or FindingStatus.NotApplicable => 0,
        FindingStatus.RequiresManualReview or FindingStatus.UnableToAssess => 1,
        FindingStatus.SettingsMatchNotEnforced => 2,
        FindingStatus.PartialMatch or FindingStatus.LicenceUnavailable => 3,
        FindingStatus.Missing => 4,
        _ => 2
    };

    private static string Explain(ControlFinding before, ControlFinding after, bool releaseChanged)
    {
        if (after.Status == FindingStatus.UnableToAssess) return "The newer capture could not read this control's collection; this is unknown, not a confirmed regression.";
        if (before.Status == FindingStatus.UnableToAssess) return "The earlier capture could not read this control's collection, so this is the first reliable result.";
        if (after.Status == FindingStatus.CompliantWithDeviation) return "An approved deviation now covers this control.";
        if (before.Status == FindingStatus.CompliantWithDeviation) return "The deviation previously covering this control is no longer recorded.";
        if (releaseChanged) return "May result from the standard release change rather than tenant drift. " + after.Reason;
        return after.Reason;
    }

    private static List<PropertyDifference> Differences(JsonNode? before, JsonNode? after, NameResolver names)
    {
        var a = CanonicalJson.Leaves(before).ToDictionary(l => l.Path, l => l.Value, StringComparer.Ordinal);
        var b = CanonicalJson.Leaves(after).ToDictionary(l => l.Path, l => l.Value, StringComparer.Ordinal);
        var list = new List<PropertyDifference>();
        foreach (var path in a.Keys.Union(b.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            a.TryGetValue(path, out var x);
            b.TryGetValue(path, out var y);
            if (CanonicalJson.Serialize(x) == CanonicalJson.Serialize(y)) continue;
            list.Add(new PropertyDifference
            {
                Setting = path,
                Standard = a.ContainsKey(path) ? names.Render(x) : "(absent)",
                Current = b.ContainsKey(path) ? names.Render(y) : "(absent)",
                Match = false
            });
        }
        return list;
    }
}
