using System.IO.Compression;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;
using BDIT.TenantToolkit.Engine.Evidence;
using BDIT.TenantToolkit.Engine.Reports;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class EvidenceAndReportTests
{
    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abcdefghijk.lmnopqrstuv", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("{\"access_token\":\"secret-value\"}", "secret-value")]
    [InlineData("{\"refresh_token\":\"0.AAAA-secret\"}", "0.AAAA-secret")]
    [InlineData("{\"client_secret\":\"shhh\"}", "shhh")]
    [InlineData("{\"password\":\"hunter2\"}", "hunter2")]
    [InlineData("https://login/callback?code=AQAB1234", "AQAB1234")]
    public void Scrubber_removes_secrets(string input, string secret)
    {
        var scrubbed = SensitiveDataScrubber.Scrub(input);
        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains("redacted", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Connected to test.example as a@b", SensitiveDataScrubber.Scrub("Connected to test.example as a@b"));
    }

    [Fact]
    public void Logger_scrubs_flushes_and_survives_dispose()
    {
        using var root = new TempRoot();
        var logger = new ToolkitLogger(root.Paths.LogsDirectory, LogLevel.Debug);
        logger.Info("Test", "token Bearer eyJhbGciOiJIUzI1NiJ9.abcdefghijk.lmnopqrstuv here");
        logger.Dispose();
        var text = File.ReadAllText(logger.FilePath!);
        Assert.Contains("redacted", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lmnopqrstuv", text, StringComparison.Ordinal);
        logger.Info("Test", "after dispose");
        Assert.Single(logger.Recent());
    }

    [Fact]
    public void Csv_guards_formula_injection_and_quotes()
    {
        var csv = CsvWriter.Write(new[] { new[] { "Name", "Value" }, new[] { "=cmd()|'/C calc'!A0", "say \"hi\"" }, new[] { "  +1", "@x" }, new[] { "-", "normal" } });
        Assert.Contains("\"'=cmd()", csv, StringComparison.Ordinal);
        Assert.Contains("\"say \"\"hi\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'  +1\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'@x\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'-\"", csv, StringComparison.Ordinal);
        Assert.StartsWith("﻿", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Xlsx_stores_values_as_inline_strings_and_escapes_xml()
    {
        var sheet = new Sheet("Results", new[] { "Name", "Status" });
        sheet.Add("=WEBSERVICE(\"bad\")", "Pass");
        sheet.Add("<client & name>", "Unknown");
        var bytes = XlsxWriter.Write(new[] { sheet, new Sheet("Results", new[] { "x" }) });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("t=\"inlineStr\"", xml, StringComparison.Ordinal);
        Assert.Contains("&lt;client &amp; name&gt;", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        using var wb = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Contains("Results (2)", wb.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_integrity_digest_detects_modification()
    {
        using var root = new TempRoot();
        var store = new EvidenceStore(root.Paths, NullLog.Instance);
        var snapshot = TestData.Snapshot(TestData.Standard());
        var file = store.SaveSnapshot(snapshot);
        Assert.True(store.SnapshotIntegrityIntact(store.LoadSnapshot(TestData.TenantA, snapshot.Id)!));

        var text = File.ReadAllText(file).Replace("\"complete\": true", "\"complete\": false", StringComparison.Ordinal);
        File.WriteAllText(file, text);
        var tampered = store.LoadSnapshot(TestData.TenantA, snapshot.Id)!;
        Assert.False(store.SnapshotIntegrityIntact(tampered));
    }

    [Fact]
    public void Reports_are_deterministic_escape_html_and_keep_raw_json_out_of_client_summary()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.TenantName = "<script>alert(1)</script> Ltd";
        snapshot.Collections["conditionalAccess"].Items.Add(TestData.ConditionalAccessPolicy("p1", "BDIT - CA-001 - Require MFA", "disabled", new[] { TestData.Emergency }));
        var engine = new AssessmentEngine(new FixedClock(), "test");
        var result = engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "engineer");

        var html1 = HtmlReports.Engineer(result);
        var html2 = HtmlReports.Engineer(result);
        Assert.Equal(html1, html2);
        Assert.DoesNotContain("<script>alert(1)</script>", html1, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html1, StringComparison.Ordinal);
        Assert.Contains("conditions.users.excludeUsers", html1, StringComparison.Ordinal);

        var client = HtmlReports.ClientSummary(result, "Blue Diamond IT");
        Assert.Contains("Blue Diamond IT", client, StringComparison.Ordinal);
        Assert.DoesNotContain("\"@odata", client, StringComparison.Ordinal);
        Assert.DoesNotContain("grantControls", client, StringComparison.Ordinal);
        Assert.Contains("Require MFA", client, StringComparison.Ordinal);

        var md = MarkdownReports.Engineer(result);
        Assert.Contains("# Tenant assessment", md, StringComparison.Ordinal);
        Assert.Contains("| `conditions.users.excludeUsers`", md, StringComparison.Ordinal);

        var sheets = TabularReports.AssessmentSheets(result);
        // The recovered exporter includes equivalence evidence and caveats; retain both alongside the original tables.
        Assert.Equal(new[] { "Summary", "Findings", "Differences", "Equivalent configuration", "Equivalence caveats", "Collection status", "Deviations", "Limitations" },
            sheets.Select(s => s.Name));
        Assert.Contains(sheets[1].Rows.Skip(1), r => r[0] == "CA-001" && r[4].Contains("not enforced", StringComparison.Ordinal));
    }

    [Fact]
    public void Tabular_reports_preserve_unmet_equivalence_signals_and_caveats()
    {
        var result = new AssessmentResult
        {
            Findings = new List<ControlFinding>
            {
                new()
                {
                    ControlId = "CA-001",
                    Equivalence = new List<EquivalenceObservation>
                    {
                        new()
                        {
                            Name = "Existing MFA policy", ObjectId = "synthetic-policy", Collection = "conditionalAccess",
                            Covered = false, Enforcement = EnforcementState.Disabled,
                            Signals = new List<SignalResult>
                            {
                                new() { Label = "All users", Required = true, Expected = "All", Observed = "Pilot group", Matched = false }
                            },
                            Caveats = new List<string> { "Disabled policy does not enforce protection." }
                        }
                    }
                }
            }
        };
        var sheets = TabularReports.AssessmentSheets(result);
        var evidence = Assert.Single(sheets.Single(s => s.Name == "Equivalent configuration").Rows.Skip(1));
        Assert.Equal(new[] { "CA-001", "Existing MFA policy", "synthetic-policy", "conditionalAccess", "Disabled", "No", "All users", "Required", "All", "Pilot group", "Not met" }, evidence);
        var caveat = Assert.Single(sheets.Single(s => s.Name == "Equivalence caveats").Rows.Skip(1));
        Assert.Equal(new[] { "CA-001", "Existing MFA policy", "Disabled policy does not enforce protection." }, caveat);
    }

    [Fact]
    public void Exporter_writes_every_format_with_safe_names()
    {
        using var root = new TempRoot();
        var standard = TestData.Standard();
        var result = new AssessmentEngine(new FixedClock(), "test").Assess(TestData.Snapshot(standard), standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "engineer");
        var exporter = new ReportExporter(root.Paths, "Blue Diamond IT");
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var file = exporter.ExportAssessment(result, format);
            Assert.True(File.Exists(file));
            var name = Path.GetFileName(file);
            Assert.True(name.StartsWith("assessment-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("client-summary-", StringComparison.OrdinalIgnoreCase), name);
        }
        Assert.Equal("test.example", ReportExporter.SafeName("test.example"));
        Assert.Equal("a_b", ReportExporter.SafeName("a/b"));
    }
}
