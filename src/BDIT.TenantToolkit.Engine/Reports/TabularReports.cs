using System.Globalization;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;

namespace BDIT.TenantToolkit.Engine.Reports;

/// <summary>A named table. The first row is the header. Every cell is text; nothing is ever emitted as a formula.</summary>
public sealed class Sheet
{
    public string Name { get; }
    public List<string[]> Rows { get; } = new();

    public Sheet(string name, IEnumerable<string> header)
    {
        Name = name;
        Rows.Add(header.ToArray());
    }

    public Sheet Add(params object?[] values)
    {
        Rows.Add(values.Select(Text).ToArray());
        return this;
    }

    public static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}

public static class StatusLabels
{
    public static string For(FindingStatus status) => status switch
    {
        FindingStatus.Compliant => "Compliant",
        FindingStatus.CompliantWithDeviation => "Compliant (approved deviation)",
        FindingStatus.SettingsMatchNotEnforced => "Settings match, not enforced",
        FindingStatus.PartialMatch => "Partial match / potential overlap",
        FindingStatus.Missing => "Missing",
        FindingStatus.UnableToAssess => "Unable to assess",
        FindingStatus.RequiresManualReview => "Requires manual review",
        FindingStatus.LicenceUnavailable => "Licence unavailable",
        FindingStatus.NotApplicable => "Not applicable",
        _ => status.ToString()
    };

    public static string For(EnforcementState state) => state switch
    {
        EnforcementState.Enforced => "Enabled",
        EnforcementState.ReportOnly => "Report-only",
        EnforcementState.Disabled => "Disabled",
        EnforcementState.Assigned => "Assigned",
        EnforcementState.Unassigned => "Unassigned",
        EnforcementState.NotApplicable => "n/a",
        _ => "Unknown"
    };
}

/// <summary>Builds the tables shared by CSV, XLSX and HTML outputs so every format says the same thing.</summary>
public static class TabularReports
{
    public static IReadOnlyList<Sheet> AssessmentSheets(AssessmentResult r)
    {
        var s = r.Summary;
        var summary = new Sheet("Summary", new[] { "Field", "Value" })
            .Add("Tenant", r.TenantName).Add("Primary domain", r.PrimaryDomain).Add("Tenant ID", r.TenantId).Add("Client label", r.ClientLabel)
            .Add("Standard release", r.Release).Add("Standard digest", r.StandardDigest).Add("Snapshot", r.SnapshotId).Add("Captured", r.CapturedAt)
            .Add("Assessed", r.AssessedAt).Add("Assessed by", r.AssessedBy).Add("Toolkit version", r.ToolkitVersion).Add("Snapshot complete", r.SnapshotComplete)
            .Add("Controls", s.Total).Add("Compliant", s.Compliant).Add("Compliant with deviation", s.CompliantWithDeviation)
            .Add("Settings match, not enforced", s.SettingsMatchNotEnforced).Add("Partial match", s.PartialMatch).Add("Missing", s.Missing)
            .Add("Unable to assess", s.UnableToAssess).Add("Requires manual review", s.RequiresManualReview).Add("Licence unavailable", s.LicenceUnavailable)
            .Add("Not applicable", s.NotApplicable).Add("Actionable critical", s.CriticalActionable).Add("Actionable high", s.HighActionable)
            .Add("Actionable medium", s.MediumActionable).Add("Actionable low", s.LowActionable)
            .Add("Interpretation", "Assessment compares captured settings with the BDIT Build Standard. It is read-only and is not a security certification. Unknown data is reported as unknown, never as absent.");

        var findings = new Sheet("Findings", new[] { "Control", "Name", "Category", "Severity", "Status", "Reason", "Expected production state", "Expected production assignment", "Best matching object", "Object state", "Toolkit-managed", "Deviation" });
        var differences = new Sheet("Differences", new[] { "Control", "Candidate object", "Object ID", "Setting", "Current value", "BDIT standard", "Match" });
        foreach (var f in r.Findings)
        {
            var best = f.BestCandidate;
            findings.Add(f.ControlId, f.Name, f.Category, f.Severity, StatusLabels.For(f.Status), f.Reason, f.ExpectedProductionState, f.ExpectedProductionAssignment,
                best is null ? "" : $"{best.Name} [{best.ObjectId}]", best is null ? "" : StatusLabels.For(best.Enforcement), f.Owned, f.Deviation?.Reason ?? "");
            foreach (var c in f.Candidates)
                foreach (var d in c.Differences)
                    differences.Add(f.ControlId, c.Name, c.ObjectId, d.Setting, d.Current, d.Standard, d.Match ? "Match" : "Different");
        }

        var collections = new Sheet("Collection status", new[] { "Collection", "Status" });
        foreach (var (label, status) in r.CollectionStatus.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) collections.Add(label, status);

        var deviations = new Sheet("Deviations", new[] { "Control", "Kind", "Reason", "Approved state", "Owner", "Approved by", "Recorded", "Review by", "Reference", "Notes" });
        foreach (var f in r.Findings.Where(f => f.Deviation is not null))
        {
            var d = f.Deviation!;
            deviations.Add(f.ControlId, d.Kind.ToString(), d.Reason, d.ApprovedState, d.Owner, d.ApprovedBy, d.RecordedAt, d.ReviewBy, d.Reference, d.Notes);
        }

        // Equivalence explains why a control the client satisfies their own way is reported as a partial match rather
        // than a gap. Every observed value travels with the claim so the judgement can be checked outside the app.
        var equivalence = new Sheet("Equivalent configuration", new[] { "Control", "Object", "Object ID", "Collection", "Object state", "Covers control", "Condition", "Required", "Expected", "Observed", "Result" });
        var caveats = new Sheet("Equivalence caveats", new[] { "Control", "Object", "Caveat" });
        foreach (var f in r.Findings)
        {
            foreach (var e in f.Equivalence)
            {
                foreach (var sig in e.Signals)
                    equivalence.Add(f.ControlId, e.Name, e.ObjectId, e.Collection, StatusLabels.For(e.Enforcement),
                        e.Covered ? "Yes" : "No", sig.Label, sig.Required ? "Required" : "Supporting", sig.Expected, sig.Observed, sig.Matched ? "Met" : "Not met");
                foreach (var caveat in e.Caveats) caveats.Add(f.ControlId, e.Name, caveat);
            }
        }

        var limitations = new Sheet("Limitations", new[] { "Limitation" });
        foreach (var l in r.Limitations) limitations.Add(l);

        return new[] { summary, findings, differences, equivalence, caveats, collections, deviations, limitations };
    }

    public static IReadOnlyList<Sheet> RunSheets(DeploymentRun run, IReadOnlyList<JournalEntry> journal)
    {
        var meta = new Sheet("Run", new[] { "Field", "Value" })
            .Add("Tenant", run.TenantName).Add("Primary domain", run.PrimaryDomain).Add("Tenant ID", run.TenantId).Add("Run", run.Id)
            .Add("Plan", run.PlanId).Add("Plan digest", run.PlanDigest).Add("Standard release", run.Release).Add("Status", run.Status)
            .Add("Started", run.StartedAt).Add("Ended", run.EndedAt ?? "").Add("Actor", run.Actor.Account).Add("Actor object ID", run.Actor.ObjectId)
            .Add("Application", run.Actor.ClientId).Add("Before snapshot", run.BeforeSnapshotId).Add("After snapshot", run.AfterSnapshotId ?? "")
            .Add("After snapshot complete", run.AfterComplete?.ToString() ?? "").Add("After snapshot error", run.AfterError ?? "").Add("Error", run.Error ?? "")
            .Add("Validation", "Configuration readback confirms Graph accepted the settings. It does not prove effective user, device or sign-in behaviour; functional testing is still required.");
        var results = new Sheet("Results", new[] { "Control", "Name", "Collection", "Planned action", "Outcome", "Object ID", "Configuration readback", "Functional verification", "Reason", "Payload digest", "Written" });
        foreach (var x in run.Results)
            results.Add(x.ControlId, x.Name, x.Collection, x.PlannedAction, x.Status, x.ObjectId ?? "", x.Configuration, x.Verification, x.Reason ?? "", x.PayloadDigest ?? "", x.WrittenAt ?? "");
        var log = new Sheet("Journal", new[] { "Sequence", "At", "Level", "Control", "Message" });
        foreach (var j in journal) log.Add(j.Sequence, j.At, j.Level, j.ControlId ?? "", j.Message);
        return new[] { meta, results, log };
    }

    public static IReadOnlyList<Sheet> DriftSheets(DriftReport d)
    {
        var meta = new Sheet("Drift", new[] { "Field", "Value" })
            .Add("Tenant", d.TenantName).Add("Tenant ID", d.TenantId).Add("Before snapshot", d.BeforeSnapshotId).Add("Before captured", d.BeforeCapturedAt)
            .Add("Before standard", d.BeforeStandardRelease).Add("After snapshot", d.AfterSnapshotId).Add("After captured", d.AfterCapturedAt)
            .Add("After standard", d.AfterStandardRelease).Add("Assessed against", d.AssessedRelease).Add("Generated", d.GeneratedAt)
            .Add("Objects added", d.Added).Add("Objects removed", d.Removed).Add("Objects changed", d.Changed).Add("Toolkit-managed objects affected", d.ManagedAffected)
            .Add("Control regressions", d.Regressions);
        foreach (var n in d.Notes) meta.Add("Note", n);
        var controls = new Sheet("Control changes", new[] { "Control", "Name", "Category", "Severity", "Before", "After", "Regression", "Explanation" });
        foreach (var c in d.ControlChanges) controls.Add(c.ControlId, c.Name, c.Category, c.Severity, StatusLabels.For(c.Before), StatusLabels.For(c.After), c.Regression, c.Explanation);
        var objects = new Sheet("Object changes", new[] { "Collection", "Object", "Object ID", "Change", "Classification", "Toolkit-managed", "Control", "Reason" });
        var diffs = new Sheet("Object differences", new[] { "Collection", "Object", "Object ID", "Setting", "Before", "After" });
        foreach (var i in d.Items)
        {
            objects.Add(i.CollectionLabel, i.Name, i.ObjectId, i.Change.ToString(), i.Classification.ToString(), i.ToolkitManaged, i.ControlId ?? "", i.Reason);
            foreach (var x in i.Differences) diffs.Add(i.CollectionLabel, i.Name, i.ObjectId, x.Setting, x.Standard, x.Current);
        }
        return new[] { meta, controls, objects, diffs };
    }

    /// <summary>Exports a raw configuration capture: one overview row per object plus a detail row per setting.</summary>
    public static IReadOnlyList<Sheet> SnapshotSheets(TenantSnapshot snapshot, StandardCatalogue? standard)
    {
        var names = NameResolver.FromSnapshot(snapshot);
        var capture = new Sheet("Capture", new[] { "Field", "Value" })
            .Add("Tenant ID", snapshot.TenantId).Add("Tenant name", snapshot.TenantName).Add("Primary domain", snapshot.PrimaryDomain).Add("Client label", snapshot.ClientLabel)
            .Add("Identity source", snapshot.IdentitySource).Add("Captured", snapshot.CapturedAt).Add("Captured by", snapshot.CapturedBy).Add("Capture ID", snapshot.Id)
            .Add("Standard release", snapshot.StandardRelease).Add("Collection complete", snapshot.Complete)
            .Add("Interpretation", "Overview has one row per object. Detail tables repeat the object name once per setting; these are not duplicate policies. Collection success is not a build-standard or security assessment.");
        var status = new Sheet("Collection status", new[] { "Collection", "API", "Outcome", "Count", "Details incomplete", "Error" });
        var overview = new Sheet("Policy overview", new[] { "Collection", "Object name", "Object ID", "Type", "State", "Targeting / assignments" });
        var details = new Sheet("Details", new[] { "Collection", "Object name", "Object ID", "Setting", "Value" });
        foreach (var (key, c) in snapshot.Collections)
        {
            var def = standard?.FindCollection(key);
            var label = def?.Label ?? key;
            status.Add(label, c.Api, c.Status == CaptureStatus.Collected ? (c.DetailIncomplete ? "Partially collected" : c.Count == 0 ? "No objects returned" : "Collected") : "Not collected", c.Count, c.DetailIncomplete, c.Error ?? "");
            foreach (var item in c.Items)
            {
                var id = item["id"]?.GetValue<string>() ?? "";
                var name = item[def?.NameProperty ?? "displayName"]?.GetValue<string>() ?? item["displayName"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? item["userPrincipalName"]?.GetValue<string>() ?? id;
                overview.Add(label, name, id, item["@odata.type"]?.GetValue<string>() ?? label, item["state"]?.GetValue<string>() ?? "", names.AssignmentSummary(item));
                foreach (var (path, value) in CanonicalJson.Leaves(item))
                {
                    if (path.StartsWith("_", StringComparison.Ordinal) && path != "_assignments") continue;
                    details.Add(label, name, id, path, names.Render(value));
                }
            }
        }
        return new[] { capture, status, overview, details };
    }
}
