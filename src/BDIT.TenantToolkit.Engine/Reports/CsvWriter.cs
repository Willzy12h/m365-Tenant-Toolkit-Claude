using System.IO.Compression;
using System.Text;

namespace BDIT.TenantToolkit.Engine.Reports;

/// <summary>CSV output that cannot trigger spreadsheet formula execution: cells beginning with = + - @ or control characters are prefixed with an apostrophe.</summary>
public static class CsvWriter
{
    public static string Write(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append((char)0xFEFF);
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Cell)));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (NeedsGuard(text)) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static bool NeedsGuard(string text)
    {
        var trimmed = text.TrimStart(' ');
        if (trimmed.Length == 0) return false;
        var c = trimmed[0];
        return c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';
    }

    /// <summary>Zips one CSV per sheet, in sheet order, with numbered, filesystem-safe names.</summary>
    public static byte[] ZipSheets(IReadOnlyList<Sheet> sheets)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < sheets.Count; i++)
            {
                var safe = new string(sheets[i].Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
                var entry = zip.CreateEntry($"{i + 1:00}_{safe}.csv", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(Write(sheets[i].Rows));
            }
        }
        return stream.ToArray();
    }
}
