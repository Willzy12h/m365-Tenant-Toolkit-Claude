using System.Text;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Engine.Reports;

/// <summary>Markdown equivalents of the engineer, run and drift reports for ticketing systems and wikis.</summary>
public static class MarkdownReports
{
    private static string E(string? s) => (s ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    public static string Engineer(AssessmentResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Tenant assessment: {E(r.TenantName)}");
        sb.AppendLine();
        sb.AppendLine($"- Primary domain: {E(r.PrimaryDomain)}");
        sb.AppendLine($"- Tenant ID: `{E(r.TenantId)}`");
        sb.AppendLine($"- Standard release: {E(r.Release)} (digest `{E(r.StandardDigest)}`)");
        sb.AppendLine($"- Snapshot: `{E(r.SnapshotId)}` captured {E(r.CapturedAt)} ({(r.SnapshotComplete ? "complete" : "incomplete")})");
        sb.AppendLine($"- Assessed: {E(r.AssessedAt)} by {E(r.AssessedBy)} with toolkit {E(r.ToolkitVersion)}");
        sb.AppendLine();
        var s = r.Summary;
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Status | Count |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| Compliant | {s.Compliant} |");
        sb.AppendLine($"| Compliant with approved deviation | {s.CompliantWithDeviation} |");
        sb.AppendLine($"| Settings match, not enforced | {s.SettingsMatchNotEnforced} |");
        sb.AppendLine($"| Partial match / potential overlap | {s.PartialMatch} |");
        sb.AppendLine($"| Missing | {s.Missing} |");
        sb.AppendLine($"| Requires manual review | {s.RequiresManualReview} |");
        sb.AppendLine($"| Unable to assess | {s.UnableToAssess} |");
        sb.AppendLine($"| Licence unavailable | {s.LicenceUnavailable} |");
        sb.AppendLine($"| Not applicable | {s.NotApplicable} |");
        sb.AppendLine();
        sb.AppendLine($"Actionable: {s.CriticalActionable} critical, {s.HighActionable} high, {s.MediumActionable} medium, {s.LowActionable} low.");
        sb.AppendLine();
        if (r.Limitations.Count > 0)
        {
            sb.AppendLine("## Limitations");
            sb.AppendLine();
            foreach (var l in r.Limitations) sb.AppendLine($"- {E(l)}");
            sb.AppendLine();
        }
        sb.AppendLine("## Findings");
        sb.AppendLine();
        foreach (var f in r.Findings.OrderBy(f => f.IsActionable ? 0 : 1).ThenBy(f => HtmlReports.SeverityRank(f.Severity)).ThenBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### {E(f.ControlId)} {E(f.Name)}");
            sb.AppendLine();
            sb.AppendLine($"- Status: **{E(StatusLabels.For(f.Status))}** · Severity: {E(f.Severity)} · Category: {E(f.Category)}");
            sb.AppendLine($"- Finding: {E(f.Reason)}");
            sb.AppendLine($"- Desired state: {E(f.DesiredState)}");
            if (!string.IsNullOrEmpty(f.ExpectedProductionState)) sb.AppendLine($"- Expected production state: {E(f.ExpectedProductionState)} {E(f.ExpectedProductionAssignment)}");
            if (f.IsActionable && !string.IsNullOrEmpty(f.EngineerAction)) sb.AppendLine($"- Engineer action: {E(f.EngineerAction)}");
            if (f.Deviation is not null) sb.AppendLine($"- Approved deviation: {E(f.Deviation.Reason)} (approved by {E(f.Deviation.ApprovedBy)}, review by {E(f.Deviation.ReviewBy)})");
            foreach (var n in f.Notes) sb.AppendLine($"- Note: {E(n)}");
            foreach (var c in f.Candidates)
            {
                sb.AppendLine();
                sb.AppendLine($"**{(c.SettingsMatch ? "Matching" : c.NameMatch ? "Same-named" : "Overlapping")} object:** {E(c.Name)} `{E(c.ObjectId)}` · {E(StatusLabels.For(c.Enforcement))} · {E(c.AssignmentSummary)} · {c.Matched}/{c.Total} settings match");
                if (c.Differences.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| Setting | Current | BDIT standard | Result |");
                    sb.AppendLine("|---|---|---|---|");
                    foreach (var d in c.Differences) sb.AppendLine($"| `{E(d.Setting)}` | {E(d.Current)} | {E(d.Standard)} | {(d.Match ? "Match" : "Different")} |");
                }
            }
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("_Assessment performed read-only. No tenant configuration was modified. Unknown data is reported as unknown, never as absent._");
        return sb.ToString();
    }

    public static string Drift(DriftReport d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Configuration drift: {E(d.TenantName)}");
        sb.AppendLine();
        sb.AppendLine($"- Before: `{E(d.BeforeSnapshotId)}` ({E(d.BeforeCapturedAt)}, standard {E(d.BeforeStandardRelease)})");
        sb.AppendLine($"- After: `{E(d.AfterSnapshotId)}` ({E(d.AfterCapturedAt)}, standard {E(d.AfterStandardRelease)})");
        sb.AppendLine($"- Assessed against: {E(d.AssessedRelease)} · Generated {E(d.GeneratedAt)}");
        sb.AppendLine($"- Regressions: {d.Regressions} · Objects added {d.Added}, removed {d.Removed}, changed {d.Changed} · Toolkit-managed objects affected: {d.ManagedAffected}");
        foreach (var n in d.Notes) sb.AppendLine($"- {E(n)}");
        sb.AppendLine();
        sb.AppendLine("## Control status changes");
        sb.AppendLine();
        sb.AppendLine("| Control | Name | Before | After | Regression | Explanation |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var c in d.ControlChanges) sb.AppendLine($"| {E(c.ControlId)} | {E(c.Name)} | {E(StatusLabels.For(c.Before))} | {E(StatusLabels.For(c.After))} | {(c.Regression ? "Yes" : "No")} | {E(c.Explanation)} |");
        sb.AppendLine();
        sb.AppendLine("## Object changes");
        sb.AppendLine();
        foreach (var i in d.Items.Where(i => i.Change != DriftChange.Unchanged))
        {
            sb.AppendLine($"### {E(i.CollectionLabel)}: {E(i.Name)} `{E(i.ObjectId)}`");
            sb.AppendLine();
            sb.AppendLine($"- {i.Change} · {i.Classification}{(i.ToolkitManaged ? " · toolkit-managed" : "")}{(i.ControlId is null ? "" : " · " + E(i.ControlId))}");
            sb.AppendLine($"- {E(i.Reason)}");
            if (i.Differences.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Setting | Before | After |");
                sb.AppendLine("|---|---|---|");
                foreach (var x in i.Differences) sb.AppendLine($"| `{E(x.Setting)}` | {E(x.Standard)} | {E(x.Current)} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string Run(DeploymentRun run, IReadOnlyList<JournalEntry> journal)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Deployment run: {E(run.TenantName)}");
        sb.AppendLine();
        foreach (var row in TabularReports.RunSheets(run, journal)[0].Rows.Skip(1)) sb.AppendLine($"- {E(row[0])}: {E(row[1])}");
        sb.AppendLine();
        sb.AppendLine("| Control | Planned | Outcome | Object | Readback | Reason |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var x in run.Results) sb.AppendLine($"| {E(x.ControlId)} | {E(x.PlannedAction)} | {E(x.Status)} | `{E(x.ObjectId)}` | {E(x.Configuration)} | {E(x.Reason)} |");
        sb.AppendLine();
        sb.AppendLine("## Journal");
        sb.AppendLine();
        foreach (var j in journal) sb.AppendLine($"- {E(j.At)} [{E(j.Level)}] {E(j.Message)}");
        return sb.ToString();
    }
}
