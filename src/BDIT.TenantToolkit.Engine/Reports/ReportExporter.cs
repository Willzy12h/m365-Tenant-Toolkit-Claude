using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Configuration;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.Engine.Reports;

public enum ExportFormat { Html, Markdown, Json, Csv, Xlsx, ClientHtml }

/// <summary>Writes reports to the reports folder with deterministic, filesystem-safe names.</summary>
public sealed class ReportExporter
{
    private readonly ToolkitPaths _paths;
    private readonly string _companyName;

    public ReportExporter(ToolkitPaths paths, string companyName)
    {
        _paths = paths;
        _companyName = string.IsNullOrWhiteSpace(companyName) ? "Blue Diamond IT" : companyName;
    }

    public static string SafeName(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "tenant" : s.Length > 60 ? s[..60] : s;
    }

    private static string Stamp(string iso) => Timestamps.TryParse(iso, out var t) ? t.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) : "undated";

    private string Target(string kind, string tenantLabel, string iso, string extension)
    {
        Directory.CreateDirectory(_paths.ReportsDirectory);
        return Path.Combine(_paths.ReportsDirectory, $"{kind}-{SafeName(tenantLabel)}-{Stamp(iso)}.{extension}");
    }

    public string ExportAssessment(AssessmentResult result, ExportFormat format)
    {
        var label = string.IsNullOrWhiteSpace(result.PrimaryDomain) ? result.TenantId : result.PrimaryDomain;
        switch (format)
        {
            case ExportFormat.Html: return WriteText(Target("assessment", label, result.AssessedAt, "html"), HtmlReports.Engineer(result));
            case ExportFormat.Markdown: return WriteText(Target("assessment", label, result.AssessedAt, "md"), MarkdownReports.Engineer(result));
            case ExportFormat.Json: return WriteText(Target("assessment", label, result.AssessedAt, "json"), ToolkitJson.Serialize(result));
            case ExportFormat.Csv: return WriteBytes(Target("assessment", label, result.AssessedAt, "csv.zip"), CsvWriter.ZipSheets(TabularReports.AssessmentSheets(result)));
            case ExportFormat.Xlsx: return WriteBytes(Target("assessment", label, result.AssessedAt, "xlsx"), XlsxWriter.Write(TabularReports.AssessmentSheets(result)));
            case ExportFormat.ClientHtml: return WriteText(Target("client-summary", label, result.AssessedAt, "html"), HtmlReports.ClientSummary(result, _companyName));
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public string ExportRun(DeploymentRun run, IReadOnlyList<JournalEntry> journal, ExportFormat format)
    {
        var label = string.IsNullOrWhiteSpace(run.PrimaryDomain) ? run.TenantId : run.PrimaryDomain;
        return format switch
        {
            ExportFormat.Html => WriteText(Target("deployment-run", label, run.StartedAt, "html"), HtmlReports.Run(run, journal)),
            ExportFormat.Markdown => WriteText(Target("deployment-run", label, run.StartedAt, "md"), MarkdownReports.Run(run, journal)),
            ExportFormat.Json => WriteText(Target("deployment-run", label, run.StartedAt, "json"), ToolkitJson.Serialize(new { run, journal })),
            ExportFormat.Csv => WriteBytes(Target("deployment-run", label, run.StartedAt, "csv.zip"), CsvWriter.ZipSheets(TabularReports.RunSheets(run, journal))),
            ExportFormat.Xlsx => WriteBytes(Target("deployment-run", label, run.StartedAt, "xlsx"), XlsxWriter.Write(TabularReports.RunSheets(run, journal))),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    public string ExportDrift(DriftReport drift, ExportFormat format)
    {
        var label = string.IsNullOrWhiteSpace(drift.TenantName) ? drift.TenantId : drift.TenantName;
        return format switch
        {
            ExportFormat.Html => WriteText(Target("drift", label, drift.GeneratedAt, "html"), HtmlReports.Drift(drift)),
            ExportFormat.Markdown => WriteText(Target("drift", label, drift.GeneratedAt, "md"), MarkdownReports.Drift(drift)),
            ExportFormat.Json => WriteText(Target("drift", label, drift.GeneratedAt, "json"), ToolkitJson.Serialize(drift)),
            ExportFormat.Csv => WriteBytes(Target("drift", label, drift.GeneratedAt, "csv.zip"), CsvWriter.ZipSheets(TabularReports.DriftSheets(drift))),
            ExportFormat.Xlsx => WriteBytes(Target("drift", label, drift.GeneratedAt, "xlsx"), XlsxWriter.Write(TabularReports.DriftSheets(drift))),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    public string ExportSnapshot(TenantSnapshot snapshot, StandardCatalogue? standard, ExportFormat format)
    {
        var label = string.IsNullOrWhiteSpace(snapshot.PrimaryDomain) ? snapshot.TenantId : snapshot.PrimaryDomain;
        return format switch
        {
            ExportFormat.Json => WriteText(Target("configuration", label, snapshot.CapturedAt, "json"), ToolkitJson.Serialize(snapshot)),
            ExportFormat.Csv => WriteBytes(Target("configuration", label, snapshot.CapturedAt, "csv.zip"), CsvWriter.ZipSheets(TabularReports.SnapshotSheets(snapshot, standard))),
            ExportFormat.Xlsx => WriteBytes(Target("configuration", label, snapshot.CapturedAt, "xlsx"), XlsxWriter.Write(TabularReports.SnapshotSheets(snapshot, standard))),
            _ => throw new ArgumentOutOfRangeException(nameof(format), "Configuration captures export as JSON, CSV or XLSX.")
        };
    }

    private static string WriteText(string file, string content)
    {
        File.WriteAllText(file, content, new System.Text.UTF8Encoding(false));
        return file;
    }

    private static string WriteBytes(string file, byte[] content)
    {
        File.WriteAllBytes(file, content);
        return file;
    }
}
