using System.IO.Compression;
using System.Text;

namespace BDIT.TenantToolkit.Engine.Reports;

/// <summary>
/// Minimal Office Open XML workbook writer with no third-party dependency. Every cell is an inline string, so
/// externally supplied values can never become formulas. Sheet names are sanitised and de-duplicated.
/// </summary>
public static class XlsxWriter
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const int MaxCellLength = 32000;

    public static byte[] Write(IReadOnlyList<Sheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("At least one sheet is required.", nameof(sheets));
        var names = UniqueNames(sheets.Select(s => s.Name));
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentTypes = new StringBuilder(Declaration)
                .Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
                .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
                .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
                .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (var i = 0; i < sheets.Count; i++)
                contentTypes.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            contentTypes.Append("</Types>");
            AddEntry(zip, "[Content_Types].xml", contentTypes.ToString());

            AddEntry(zip, "_rels/.rels", Declaration +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

            var workbook = new StringBuilder(Declaration)
                .Append($"<workbook xmlns=\"{MainNamespace}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (var i = 0; i < sheets.Count; i++)
                workbook.Append($"<sheet name=\"{Xml(names[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            workbook.Append("</sheets></workbook>");
            AddEntry(zip, "xl/workbook.xml", workbook.ToString());

            var rels = new StringBuilder(Declaration).Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < sheets.Count; i++)
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            rels.Append("</Relationships>");
            AddEntry(zip, "xl/_rels/workbook.xml.rels", rels.ToString());

            for (var i = 0; i < sheets.Count; i++)
            {
                var sb = new StringBuilder(Declaration)
                    .Append($"<worksheet xmlns=\"{MainNamespace}\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" state=\"frozen\"/></sheetView></sheetViews>")
                    .Append("<cols><col min=\"1\" max=\"30\" width=\"28\" customWidth=\"1\"/></cols><sheetData>");
                var rows = sheets[i].Rows;
                for (var r = 0; r < rows.Count; r++)
                {
                    sb.Append($"<row r=\"{r + 1}\">");
                    for (var c = 0; c < rows[r].Length; c++)
                        sb.Append($"<c r=\"{Column(c)}{r + 1}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(rows[r][c])}</t></is></c>");
                    sb.Append("</row>");
                }
                sb.Append("</sheetData></worksheet>");
                AddEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
            }
        }
        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public static string Column(int index)
    {
        var value = "";
        for (index++; index > 0; index = (index - 1) / 26) value = (char)('A' + (index - 1) % 26) + value;
        return value;
    }

    public static string Xml(string? value)
    {
        var text = value ?? "";
        if (text.Length > MaxCellLength) text = text[..(MaxCellLength - 60)] + " [TRUNCATED FOR EXCEL - use JSON or HTML for the full value]";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') break;
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    public static IReadOnlyList<string> UniqueNames(IEnumerable<string> names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var cleaned = new string((raw ?? "Sheet").Select(ch => "\\/?*[]:".Contains(ch, StringComparison.Ordinal) ? '_' : ch).ToArray()).Trim();
            if (cleaned.Length == 0) cleaned = "Sheet";
            if (cleaned.Length > 31) cleaned = cleaned[..31];
            var candidate = cleaned;
            var n = 2;
            while (!seen.Add(candidate))
            {
                var suffix = $" ({n++})";
                candidate = (cleaned.Length + suffix.Length > 31 ? cleaned[..(31 - suffix.Length)] : cleaned) + suffix;
            }
            result.Add(candidate);
        }
        return result;
    }
}
