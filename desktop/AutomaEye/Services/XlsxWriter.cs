using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AutomaEye.Services;

/// <summary>
/// Minimal single-sheet .xlsx writer (string/number cells only) - a C#
/// equivalent of the reference's lib/xlsxlite.js, which hand-rolled the same
/// minimal OOXML part set via AdmZip rather than pulling in a full Excel
/// library for one report. System.IO.Compression.ZipArchive covers the same
/// need here with no extra NuGet package.
/// </summary>
public static class XlsxWriter
{
    public static void Write(string path, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(archive, "_rels/.rels", RelsXml);
        WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(headers, rows));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildSheetXml(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        AppendRow(sb, 1, headers.Cast<object?>().ToList());
        for (int r = 0; r < rows.Count; r++)
            AppendRow(sb, r + 2, rows[r]);

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, int rowIndex, IReadOnlyList<object?> cells)
    {
        sb.Append($"<row r=\"{rowIndex}\">");
        for (int c = 0; c < cells.Count; c++)
        {
            var cellRef = ColumnLetter(c) + rowIndex;
            var value = cells[c];
            if (value is double or float or int or long)
            {
                var num = System.Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                sb.Append($"<c r=\"{cellRef}\"><v>{num}</v></c>");
            }
            else
            {
                var text = System.Security.SecurityElement.Escape(value?.ToString() ?? "");
                sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{text}</t></is></c>");
            }
        }
        sb.Append("</row>");
    }

    private static string ColumnLetter(int index)
    {
        var letter = "";
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            letter = (char)('A' + rem) + letter;
            index = (index - 1) / 26;
        }
        return letter;
    }

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "</Types>";

    private const string RelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Deteksi\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private const string WorkbookRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "</Relationships>";
}
