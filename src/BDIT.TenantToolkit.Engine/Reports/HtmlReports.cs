using System.Net;
using System.Text;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Engine.Reports;

/// <summary>Self-contained HTML reports. Rendering is deterministic for a given input; no clock is consulted here.</summary>
public static class HtmlReports
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        <style>
        :root{--fg:#1a2028;--muted:#5a6675;--line:#d9dee5;--bg:#f7f9fc;--accent:#1769aa;--critical:#b3261e;--high:#c2530a;--medium:#b06000;--low:#5a6675;--good:#1e8e3e}
        *{box-sizing:border-box}body{font-family:"Segoe UI",system-ui,sans-serif;color:var(--fg);max-width:1180px;margin:0 auto;padding:32px 24px;line-height:1.5;font-size:14px}
        h1{font-size:26px;margin:0 0 6px}h2{font-size:19px;margin:32px 0 10px;border-bottom:1px solid var(--line);padding-bottom:6px}h3{font-size:15px;margin:18px 0 6px}
        .brand{font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted)}
        dl.meta{display:grid;grid-template-columns:200px 1fr;gap:4px 16px;background:var(--bg);padding:14px;border-radius:6px;font-size:13px}dl.meta dt{color:var(--muted)}dl.meta dd{margin:0;overflow-wrap:anywhere}
        table{border-collapse:collapse;width:100%;font-size:13px;margin:8px 0 16px}th{text-align:left;padding:7px 9px;border-bottom:1px solid var(--line);background:var(--bg);font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}td{padding:7px 9px;border-bottom:1px solid var(--line);vertical-align:top;overflow-wrap:anywhere}
        .tiles{display:flex;flex-wrap:wrap;gap:12px;margin:14px 0}.tile{background:var(--bg);border-radius:6px;padding:12px 16px;min-width:150px}.tile b{display:block;font-size:26px;line-height:1.1}.tile span{font-size:12px;color:var(--muted)}
        .finding{border:1px solid var(--line);border-left-width:6px;border-radius:6px;padding:14px 16px;margin-bottom:14px}.sev-critical{border-left-color:var(--critical)}.sev-high{border-left-color:var(--high)}.sev-medium{border-left-color:var(--medium)}.sev-low,.sev-informational{border-left-color:var(--low)}
        .badge{display:inline-block;font-size:11px;font-weight:600;padding:2px 8px;border-radius:10px;background:#eef1f5;color:var(--muted);text-transform:uppercase;letter-spacing:.04em;margin-right:6px}
        .match{color:var(--good)}.diff{color:var(--critical)}.note{background:#f3f8fd;border-left:3px solid var(--accent);padding:10px 12px;border-radius:4px;margin:10px 0;font-size:13px}.warn{background:#fff5de;border-left:3px solid #946100}
        code{font-family:Consolas,monospace;font-size:12px;background:var(--bg);padding:1px 4px;border-radius:3px}footer{margin-top:40px;padding-top:16px;border-top:1px solid var(--line);font-size:12px;color:var(--muted)}
        @media print{body{max-width:none;padding:0}.finding,table{break-inside:avoid}}
        </style>
        """;

    private static void Head(StringBuilder sb, string title)
    {
        sb.Append("<!doctype html><html lang=\"en-GB\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
          .Append(H(title)).Append("</title>").Append(Css).Append("</head><body>");
    }

    private static void Table(StringBuilder sb, Sheet sheet, int skipHeaderRows = 0)
    {
        sb.Append("<table><thead><tr>");
        foreach (var h in sheet.Rows[0]) sb.Append("<th>").Append(H(h)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in sheet.Rows.Skip(1 + skipHeaderRows))
        {
            sb.Append("<tr>");
            foreach (var cell in row) sb.Append("<td>").Append(H(cell)).Append("</td>");
            sb.Append("</tr>");
        }
        if (sheet.Rows.Count <= 1) sb.Append("<tr><td colspan=\"").Append(sheet.Rows[0].Length).Append("\">None</td></tr>");
        sb.Append("</tbody></table>");
    }

    // ---- engineer assessment report ------------------------------------------------------------------------------

    public static string Engineer(AssessmentResult r)
    {
        var sb = new StringBuilder();
        Head(sb, $"BDIT Tenant Assessment - {r.TenantName}");
        sb.Append("<div class=\"brand\">BDIT Microsoft 365 Tenant Toolkit · Engineer report</div>");
        sb.Append("<h1>Tenant assessment: ").Append(H(r.TenantName)).Append("</h1>");
        sb.Append("<dl class=\"meta\">");
        Meta(sb, "Primary domain", r.PrimaryDomain); Meta(sb, "Tenant ID", r.TenantId); Meta(sb, "Client label", r.ClientLabel);
        Meta(sb, "Standard release", r.Release); Meta(sb, "Standard digest", r.StandardDigest); Meta(sb, "Snapshot", r.SnapshotId);
        Meta(sb, "Captured", r.CapturedAt); Meta(sb, "Assessed", r.AssessedAt); Meta(sb, "Assessed by", r.AssessedBy); Meta(sb, "Toolkit version", r.ToolkitVersion);
        sb.Append("</dl>");

        var s = r.Summary;
        sb.Append("<h2>Summary</h2><div class=\"tiles\">");
        Tile(sb, s.Compliant + s.CompliantWithDeviation, "Compliant");
        Tile(sb, s.SettingsMatchNotEnforced, "Match, not enforced");
        Tile(sb, s.PartialMatch, "Partial / overlap");
        Tile(sb, s.Missing, "Missing");
        Tile(sb, s.RequiresManualReview, "Manual review");
        Tile(sb, s.UnableToAssess, "Unable to assess");
        Tile(sb, s.LicenceUnavailable + s.NotApplicable, "Licence / N/A");
        sb.Append("</div>");
        sb.Append("<p>Actionable by severity: <b>").Append(s.CriticalActionable).Append(" critical</b>, <b>").Append(s.HighActionable).Append(" high</b>, ")
          .Append(s.MediumActionable).Append(" medium, ").Append(s.LowActionable).Append(" low.</p>");
        if (!r.SnapshotComplete) sb.Append("<div class=\"note warn\"><b>The capture is incomplete.</b> Controls that depend on missing data are reported as unable to assess; they are not counted as missing.</div>");
        if (r.Limitations.Count > 0)
        {
            sb.Append("<h3>Limitations</h3><ul>");
            foreach (var l in r.Limitations) sb.Append("<li>").Append(H(l)).Append("</li>");
            sb.Append("</ul>");
        }

        var actionable = r.Findings.Where(f => f.IsActionable).OrderBy(f => SeverityRank(f.Severity)).ThenBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).ToList();
        sb.Append("<h2>Findings requiring action (").Append(actionable.Count).Append(")</h2>");
        if (actionable.Count == 0) sb.Append("<p>No actionable findings.</p>");
        foreach (var f in actionable) Finding(sb, f, true);

        var review = r.Findings.Where(f => f.Status is FindingStatus.RequiresManualReview or FindingStatus.UnableToAssess or FindingStatus.LicenceUnavailable).OrderBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).ToList();
        sb.Append("<h2>Manual review, unknown and licence-limited (").Append(review.Count).Append(")</h2>");
        foreach (var f in review) Finding(sb, f, false);

        var inPlace = r.Findings.Where(f => f.Status is FindingStatus.Compliant or FindingStatus.CompliantWithDeviation or FindingStatus.NotApplicable).OrderBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).ToList();
        sb.Append("<h2>In place, deviations and not applicable (").Append(inPlace.Count).Append(")</h2><table><thead><tr><th>Control</th><th>Name</th><th>Status</th><th>Detail</th></tr></thead><tbody>");
        foreach (var f in inPlace)
            sb.Append("<tr><td><code>").Append(H(f.ControlId)).Append("</code></td><td>").Append(H(f.Name)).Append("</td><td>").Append(H(StatusLabels.For(f.Status))).Append("</td><td>").Append(H(f.Reason)).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h2>Collection status</h2>");
        Table(sb, TabularReports.AssessmentSheets(r)[3]);

        sb.Append("<footer><p><b>Assessment performed read-only.</b> No tenant configuration was modified. Matches compare captured settings with the BDIT Build Standard; they are not a security certification and do not prove effective user or device behaviour.</p>")
          .Append("<p>BDIT Build Standard ").Append(H(r.Release)).Append(" · Toolkit ").Append(H(r.ToolkitVersion)).Append(" · Assessment ").Append(H(r.Id)).Append("</p></footer></body></html>");
        return sb.ToString();
    }

    private static void Finding(StringBuilder sb, ControlFinding f, bool showDifferences)
    {
        sb.Append("<article class=\"finding sev-").Append(H(f.Severity.ToLowerInvariant())).Append("\">");
        sb.Append("<div><span class=\"badge\">").Append(H(f.Severity)).Append("</span><span class=\"badge\">").Append(H(StatusLabels.For(f.Status))).Append("</span><code>").Append(H(f.ControlId)).Append("</code> <b>").Append(H(f.Name)).Append("</b> · ").Append(H(f.Category)).Append("</div>");
        sb.Append("<p>").Append(H(f.Reason)).Append("</p>");
        sb.Append("<dl class=\"meta\">");
        Meta(sb, "Desired state", f.DesiredState);
        if (!string.IsNullOrEmpty(f.ExpectedProductionState)) Meta(sb, "Expected production state", f.ExpectedProductionState + (string.IsNullOrEmpty(f.ExpectedProductionAssignment) ? "" : " · " + f.ExpectedProductionAssignment));
        if (!string.IsNullOrEmpty(f.BusinessImpact)) Meta(sb, "Business impact", f.BusinessImpact);
        if (!string.IsNullOrEmpty(f.EngineerAction) && f.IsActionable) Meta(sb, "Engineer action", f.EngineerAction);
        if (!string.IsNullOrEmpty(f.ManualInstructions) && f.Status == FindingStatus.RequiresManualReview) Meta(sb, "How to review", f.ManualInstructions);
        if (f.Owned) Meta(sb, "Toolkit-managed object", f.OwnedObjectId ?? "");
        sb.Append("</dl>");
        if (f.Deviation is not null)
            sb.Append("<div class=\"note\"><b>Approved deviation.</b> ").Append(H(f.Deviation.Reason)).Append(" — approved by ").Append(H(f.Deviation.ApprovedBy)).Append(string.IsNullOrEmpty(f.Deviation.ReviewBy) ? "" : ", review by " + H(f.Deviation.ReviewBy)).Append("</div>");
        foreach (var n in f.Notes) sb.Append("<div class=\"note\">").Append(H(n)).Append("</div>");
        if (f.Equivalence.Count > 0)
        {
            sb.Append("<h3>Equivalent configuration test</h3>");
            sb.Append("<p>Client policies rarely match the Build Standard name or shape, so each object below is measured against the conditions this control actually requires. Every observed value is shown so the judgement can be checked rather than trusted.</p>");
            foreach (var e in f.Equivalence)
            {
                sb.Append("<h4>").Append(H(e.Name)).Append(e.Covered ? " — satisfies every required condition" : " — partial").Append("</h4>");
                sb.Append("<table><tr><th>Condition</th><th>Required</th><th>Expected</th><th>Observed</th><th>Result</th></tr>");
                foreach (var s in e.Signals)
                    sb.Append("<tr><td>").Append(H(s.Label)).Append("</td><td>").Append(s.Required ? "Yes" : "No")
                      .Append("</td><td>").Append(H(s.Expected)).Append("</td><td>").Append(H(s.Observed))
                      .Append("</td><td>").Append(s.Matched ? "Met" : "Not met").Append("</td></tr>");
                sb.Append("</table>");
                foreach (var caveat in e.Caveats) sb.Append("<div class=\"note\">").Append(H(caveat)).Append("</div>");
            }
        }
        if (f.ObservedObjects.Count > 0)
        {
            sb.Append("<h3>Observed objects in the related collection</h3><ul>");
            foreach (var o in f.ObservedObjects) sb.Append("<li>").Append(H(o)).Append("</li>");
            sb.Append("</ul>");
        }
        if (showDifferences && f.Candidates.Count > 0)
        {
            foreach (var c in f.Candidates)
            {
                sb.Append("<h3>").Append(c.SettingsMatch ? "Matching object: " : c.NameMatch ? "Same-named object: " : "Overlapping object: ").Append(H(c.Name)).Append(" <code>").Append(H(c.ObjectId)).Append("</code></h3>");
                sb.Append("<p>State: ").Append(H(StatusLabels.For(c.Enforcement))).Append(" · Targeting: ").Append(H(c.AssignmentSummary)).Append(c.ToolkitManaged ? " · created by the toolkit" : "").Append(" · ").Append(c.Matched).Append('/').Append(c.Total).Append(" settings match</p>");
                if (c.Differences.Count > 0)
                {
                    sb.Append("<table><thead><tr><th>Setting</th><th>Current</th><th>BDIT standard</th><th>Result</th></tr></thead><tbody>");
                    foreach (var d in c.Differences)
                        sb.Append("<tr><td><code>").Append(H(d.Setting)).Append("</code></td><td>").Append(H(d.Current)).Append("</td><td>").Append(H(d.Standard)).Append("</td><td class=\"").Append(d.Match ? "match\">Match" : "diff\">Different").Append("</td></tr>");
                    sb.Append("</tbody></table>");
                }
            }
        }
        sb.Append("</article>");
    }

    // ---- client-facing summary ------------------------------------------------------------------------------------

    public static string ClientSummary(AssessmentResult r, string companyName)
    {
        var sb = new StringBuilder();
        Head(sb, $"Microsoft 365 Security Review - {r.TenantName}");
        sb.Append("<div class=\"brand\">").Append(H(companyName)).Append("</div><h1>Microsoft 365 configuration review</h1>");
        sb.Append("<dl class=\"meta\">");
        Meta(sb, "Prepared for", r.TenantName); Meta(sb, "Prepared by", companyName); Meta(sb, "Review date", r.AssessedAt.Length >= 10 ? r.AssessedAt[..10] : r.AssessedAt); Meta(sb, "Reference", r.Id);
        sb.Append("</dl>");
        var s = r.Summary;
        var assessed = s.Total - s.NotApplicable;
        var inPlace = s.Compliant + s.CompliantWithDeviation;
        var needsAttention = s.Missing + s.PartialMatch + s.SettingsMatchNotEnforced;
        sb.Append("<h2>Overall position</h2>");
        sb.Append("<p>This review compared your Microsoft 365 configuration with the ").Append(H(companyName)).Append(" build standard (release ").Append(H(r.Release)).Append("). ")
          .Append("It looked at ").Append(assessed).Append(" controls. ").Append(inPlace).Append(" are in place");
        if (s.CompliantWithDeviation > 0) sb.Append(" (").Append(s.CompliantWithDeviation).Append(" through an agreed exception)");
        sb.Append(", ").Append(needsAttention).Append(" need attention, ").Append(s.RequiresManualReview).Append(" require an engineer's manual review");
        if (s.UnableToAssess > 0) sb.Append(", and ").Append(s.UnableToAssess).Append(" could not be checked during this review");
        sb.Append(".</p>");
        sb.Append("<div class=\"tiles\">");
        Tile(sb, inPlace, "In place"); Tile(sb, needsAttention, "Need attention"); Tile(sb, s.CriticalActionable + s.HighActionable, "Priority items"); Tile(sb, s.RequiresManualReview + s.UnableToAssess, "Engineer follow-up");
        sb.Append("</div>");

        var priority = r.Findings.Where(f => f.IsActionable).OrderBy(f => SeverityRank(f.Severity)).ThenBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).ToList();
        sb.Append("<h2>Recommendations</h2>");
        if (priority.Count == 0) sb.Append("<p>No configuration gaps were identified against the standard.</p>");
        foreach (var f in priority)
        {
            sb.Append("<article class=\"finding sev-").Append(H(f.Severity.ToLowerInvariant())).Append("\"><div><span class=\"badge\">").Append(H(PriorityWord(f.Severity))).Append("</span><b>").Append(H(f.Name)).Append("</b></div>");
            sb.Append("<p><b>Current position:</b> ").Append(H(ClientStatus(f.Status))).Append("</p>");
            if (!string.IsNullOrEmpty(f.BusinessImpact)) sb.Append("<p><b>Why it matters:</b> ").Append(H(f.BusinessImpact)).Append("</p>");
            sb.Append("<p><b>Recommended action:</b> ").Append(H(string.IsNullOrEmpty(f.EngineerAction) ? f.DesiredState : f.EngineerAction)).Append("</p></article>");
        }

        var deviations = r.Findings.Where(f => f.Deviation is not null).ToList();
        if (deviations.Count > 0)
        {
            sb.Append("<h2>Agreed exceptions</h2><table><thead><tr><th>Control</th><th>Reason</th><th>Review by</th></tr></thead><tbody>");
            foreach (var f in deviations) sb.Append("<tr><td>").Append(H(f.Name)).Append("</td><td>").Append(H(f.Deviation!.Reason)).Append("</td><td>").Append(H(f.Deviation.ReviewBy)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        var incomplete = r.Findings.Where(f => f.Status is FindingStatus.UnableToAssess or FindingStatus.LicenceUnavailable).ToList();
        if (incomplete.Count > 0 || r.Limitations.Count > 0)
        {
            sb.Append("<h2>Checks not completed</h2><ul>");
            foreach (var f in incomplete) sb.Append("<li>").Append(H(f.Name)).Append(": ").Append(H(f.Status == FindingStatus.LicenceUnavailable ? "requires a licence that is not present in your subscription." : "could not be read during this review and will be checked at the next visit.")).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("<h2>Next steps</h2><p>").Append(H(companyName)).Append(" recommends agreeing the priority items first. Changes are introduced in stages, tested with a pilot group, and only enforced after validation, so day-to-day work is not disrupted.</p>");
        sb.Append("<footer><p>This review was performed read-only; no changes were made to your Microsoft 365 environment. It reflects configuration at ").Append(H(r.CapturedAt)).Append(" and is not a security certification.</p></footer></body></html>");
        return sb.ToString();
    }

    private static string ClientStatus(FindingStatus status) => status switch
    {
        FindingStatus.Missing => "Not currently configured.",
        FindingStatus.PartialMatch => "Partly configured; an existing policy differs from the recommended settings.",
        FindingStatus.SettingsMatchNotEnforced => "Configured but not yet switched on for users or devices.",
        _ => StatusLabels.For(status)
    };

    private static string PriorityWord(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => "Priority", "high" => "Important", "medium" => "Recommended", "low" => "Advisory", _ => "Information"
    };

    // ---- deployment run report --------------------------------------------------------------------------------------

    public static string Run(DeploymentRun run, IReadOnlyList<JournalEntry> journal)
    {
        var sb = new StringBuilder();
        Head(sb, $"BDIT Deployment Run - {run.TenantName}");
        sb.Append("<div class=\"brand\">BDIT Microsoft 365 Tenant Toolkit · Deployment run</div><h1>Deployment run: ").Append(H(run.TenantName)).Append("</h1>");
        var sheets = TabularReports.RunSheets(run, journal);
        sb.Append("<dl class=\"meta\">");
        foreach (var row in sheets[0].Rows.Skip(1)) Meta(sb, row[0], row[1]);
        sb.Append("</dl>");
        sb.Append("<div class=\"note warn\">Every object created by this run is disabled (Conditional Access) or unassigned (Intune). Assignment, activation and functional testing are separate engineering steps.</div>");
        sb.Append("<h2>Results</h2>"); Table(sb, sheets[1]);
        sb.Append("<h2>Journal</h2>"); Table(sb, sheets[2]);
        sb.Append("<footer><p>Configuration readback confirms that Microsoft Graph accepted the requested settings; it does not prove effective device or sign-in behaviour.</p></footer></body></html>");
        return sb.ToString();
    }

    // ---- drift report --------------------------------------------------------------------------------------------------

    public static string Drift(DriftReport d)
    {
        var sb = new StringBuilder();
        Head(sb, $"BDIT Tenant Drift - {d.TenantName}");
        sb.Append("<div class=\"brand\">BDIT Microsoft 365 Tenant Toolkit · Drift</div><h1>Configuration drift: ").Append(H(d.TenantName)).Append("</h1>");
        var sheets = TabularReports.DriftSheets(d);
        sb.Append("<dl class=\"meta\">");
        foreach (var row in sheets[0].Rows.Skip(1)) Meta(sb, row[0], row[1]);
        sb.Append("</dl>");
        sb.Append("<div class=\"tiles\">");
        Tile(sb, d.Regressions, "Control regressions"); Tile(sb, d.Changed, "Objects changed"); Tile(sb, d.Added, "Objects added"); Tile(sb, d.Removed, "Objects removed"); Tile(sb, d.ManagedAffected, "Toolkit objects affected");
        sb.Append("</div>");
        sb.Append("<h2>Control status changes</h2>"); Table(sb, sheets[1]);
        sb.Append("<h2>Object changes</h2>");
        var changed = d.Items.Where(i => i.Change != DriftChange.Unchanged).ToList();
        if (changed.Count == 0) sb.Append("<p>No object changes.</p>");
        foreach (var i in changed)
        {
            sb.Append("<article class=\"finding ").Append(i.ToolkitManaged && i.Classification != DriftClassification.None ? "sev-critical" : "sev-medium").Append("\"><div><span class=\"badge\">").Append(H(i.Change.ToString())).Append("</span><span class=\"badge\">").Append(H(i.Classification.ToString())).Append("</span><b>").Append(H(i.Name)).Append("</b> · ").Append(H(i.CollectionLabel)).Append(" <code>").Append(H(i.ObjectId)).Append("</code></div><p>").Append(H(i.Reason)).Append("</p>");
            if (i.Differences.Count > 0)
            {
                sb.Append("<table><thead><tr><th>Setting</th><th>Before</th><th>After</th></tr></thead><tbody>");
                foreach (var x in i.Differences) sb.Append("<tr><td><code>").Append(H(x.Setting)).Append("</code></td><td>").Append(H(x.Standard)).Append("</td><td>").Append(H(x.Current)).Append("</td></tr>");
                sb.Append("</tbody></table>");
            }
            sb.Append("</article>");
        }
        sb.Append("<footer><p>Drift compares two stored captures of the same tenant; no tenant configuration was changed.</p></footer></body></html>");
        return sb.ToString();
    }

    private static void Meta(StringBuilder sb, string key, string? value) => sb.Append("<dt>").Append(H(key)).Append("</dt><dd>").Append(H(value)).Append("</dd>");
    private static void Tile(StringBuilder sb, int value, string label) => sb.Append("<div class=\"tile\"><b>").Append(value).Append("</b><span>").Append(H(label)).Append("</span></div>");

    public static int SeverityRank(string severity) => severity.ToLowerInvariant() switch { "critical" => 0, "high" => 1, "medium" => 2, "low" => 3, _ => 4 };
}
