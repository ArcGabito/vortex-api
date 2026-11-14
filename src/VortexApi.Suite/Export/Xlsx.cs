using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace VortexApi.Suite.Export;

/// <summary>Cómo se muestra una columna en Excel.</summary>
public enum XlsxColumnType
{
    Text,

    /// <summary>Número con separador de miles y hasta 2 decimales.</summary>
    Number,

    /// <summary>Importe con 2 decimales.</summary>
    Money,

    /// <summary>Un porcentaje ya multiplicado por 100 (50 = 50 %): se muestra con el signo, sin volver a multiplicar.</summary>
    Percent,

    /// <summary>Fecha (<see cref="DateOnly"/>, <see cref="DateTime"/> o <see cref="DateTimeOffset"/>).</summary>
    Date,
}

public sealed record XlsxColumn(string Header, XlsxColumnType Type = XlsxColumnType.Text, double? Width = null);

/// <summary>
/// Una hoja: título opcional (y subtítulo) arriba, encabezados en negrita con filtro y fila fija, las filas y una fila de
/// totales en negrita. Las celdas son texto, números (<c>decimal</c>, <c>int</c>, <c>long</c>, <c>double</c>), fechas,
/// <c>bool</c> o null.
/// </summary>
public sealed record XlsxSheet(string Name, IReadOnlyList<XlsxColumn> Columns, IEnumerable<object?[]> Rows, object?[]? Totals = null,
    string? Title = null, string? Subtitle = null);

/// <summary>
/// Libro de Excel (.xlsx, Office Open XML) sin dependencias: lo arma con <see cref="ZipArchive"/> y texto en línea (sin
/// tabla de cadenas compartidas). Sirve para los reportes de todos los sistemas de la suite y abre en Excel, LibreOffice,
/// Numbers y Google Sheets.
/// </summary>
public static class XlsxWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Estilos (cellXfs): 0 normal, 1 negrita, 2 número, 3 importe, 4 porcentaje, 5 fecha, 6 título, 7–10 = 2–5 en negrita.
    private const int Bold = 1, Num = 2, MoneyStyle = 3, Pct = 4, DateStyle = 5, TitleStyle = 6, BoldOffset = 5;

    public static byte[] Write(params XlsxSheet[] sheets) => Write((IReadOnlyList<XlsxSheet>)sheets);

    public static byte[] Write(IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0)
            throw new ArgumentException("El libro necesita al menos una hoja.", nameof(sheets));
        var names = UniqueNames(sheets.Select(s => s.Name));
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Add(zip, "xl/workbook.xml", Workbook(names));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
            Add(zip, "xl/styles.xml", Styles);
            for (var i = 0; i < sheets.Count; i++)
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i]));
        }
        return memory.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Nombres válidos para Excel: sin <c>[]:*?/\</c>, hasta 31 caracteres y sin repetir.</summary>
    private static List<string> UniqueNames(IEnumerable<string> raw)
    {
        var result = new List<string>();
        foreach (var name in raw)
        {
            var clean = new string((string.IsNullOrWhiteSpace(name) ? "Hoja" : name.Trim()).Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim('\'');
            if (clean.Length == 0)
                clean = "Hoja";
            clean = clean.Length > 31 ? clean[..31] : clean;
            var candidate = clean;
            for (var n = 2; result.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++)
            {
                var suffix = $" ({n})";
                candidate = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
            }
            result.Add(candidate);
        }
        return result;
    }

    private static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= sheets; i++)
            sb.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        return sb.Append("</Types>").ToString();
    }

    private static string Workbook(IReadOnlyList<string> names)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
        for (var i = 0; i < names.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"""<sheet name="{Esc(names[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        return sb.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRels(int sheets)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 1; i <= sheets; i++)
            sb.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        sb.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{sheets + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        return sb.Append("</Relationships>").ToString();
    }

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="3"><numFmt numFmtId="164" formatCode="#,##0.##"/><numFmt numFmtId="165" formatCode="0.0&quot; %&quot;"/><numFmt numFmtId="166" formatCode="yyyy-mm-dd"/></numFmts><fonts count="3"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="14"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFF3F4F6"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="11"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="164" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/><xf numFmtId="4" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/><xf numFmtId="165" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/><xf numFmtId="166" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
        """;

    private static string Sheet(XlsxSheet s)
    {
        var rows = s.Rows.ToList();
        var columns = s.Columns.Count;
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        var headerRow = (s.Title is null ? 0 : 1) + (s.Subtitle is null ? 0 : 1) + (s.Title is null && s.Subtitle is null ? 1 : 2);
        sb.Append(CultureInfo.InvariantCulture,
            $"""<sheetViews><sheetView workbookViewId="0"><pane ySplit="{headerRow}" topLeftCell="A{headerRow + 1}" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("<cols>");
        for (var c = 0; c < columns; c++)
        {
            var col = s.Columns[c];
            var width = col.Width ?? Math.Clamp(Math.Max(col.Header.Length, rows.Take(200).Select(r => c < r.Length ? Display(r[c]).Length : 0).DefaultIfEmpty(0).Max()) + 2,
                col.Type == XlsxColumnType.Text ? 8 : 10, 60);
            sb.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{width:0.#}" customWidth="1"/>""");
        }
        sb.Append("</cols><sheetData>");
        var r = 0;
        if (s.Title is { } title)
            sb.Append(CultureInfo.InvariantCulture, $"""<row r="{++r}">{Text(Ref(0, r), title, TitleStyle)}</row>""");
        if (s.Subtitle is { } subtitle)
            sb.Append(CultureInfo.InvariantCulture, $"""<row r="{++r}">{Text(Ref(0, r), subtitle, 0)}</row>""");
        if (r > 0)
            r++;   // una fila en blanco antes de la tabla
        sb.Append(CultureInfo.InvariantCulture, $"""<row r="{++r}">""");
        for (var c = 0; c < columns; c++)
            sb.Append(Text(Ref(c, r), s.Columns[c].Header, Bold));
        sb.Append("</row>");
        var first = r;
        foreach (var row in rows)
            AppendRow(sb, s.Columns, row, ++r, bold: false);
        var last = r;
        if (s.Totals is { } totals)
            AppendRow(sb, s.Columns, totals, ++r, bold: true);
        sb.Append("</sheetData>");
        if (columns > 0)
            sb.Append(CultureInfo.InvariantCulture, $"""<autoFilter ref="{Ref(0, first)}:{Ref(columns - 1, Math.Max(first, last))}"/>""");
        return sb.Append("</worksheet>").ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<XlsxColumn> columns, object?[] row, int r, bool bold)
    {
        sb.Append(CultureInfo.InvariantCulture, $"""<row r="{r}">""");
        for (var c = 0; c < columns.Count && c < row.Length; c++)
        {
            if (row[c] is null)
                continue;
            var cell = Ref(c, r);
            var style = columns[c].Type switch
            {
                XlsxColumnType.Number => Num,
                XlsxColumnType.Money => MoneyStyle,
                XlsxColumnType.Percent => Pct,
                XlsxColumnType.Date => DateStyle,
                _ => 0,
            };
            if (bold)
                style = style == 0 ? Bold : style + BoldOffset;
            sb.Append(row[c] switch
            {
                decimal d => Number(cell, d.ToString(CultureInfo.InvariantCulture), style),
                int i => Number(cell, i.ToString(CultureInfo.InvariantCulture), style),
                long l => Number(cell, l.ToString(CultureInfo.InvariantCulture), style),
                double x when double.IsFinite(x) => Number(cell, x.ToString("R", CultureInfo.InvariantCulture), style),
                DateOnly d => Number(cell, Serial(d.ToDateTime(TimeOnly.MinValue)), style == 0 || style == Bold ? DateStyle + (bold ? BoldOffset : 0) : style),
                DateTime d => Number(cell, Serial(d), style == 0 || style == Bold ? DateStyle + (bold ? BoldOffset : 0) : style),
                DateTimeOffset d => Number(cell, Serial(d.DateTime), style == 0 || style == Bold ? DateStyle + (bold ? BoldOffset : 0) : style),
                bool b => $"""<c r="{cell}" t="b"{(style > 0 ? $" s=\"{style}\"" : "")}><v>{(b ? 1 : 0)}</v></c>""",
                var other => Text(cell, other!.ToString() ?? "", bold ? Bold : 0),
            });
        }
        sb.Append("</row>");
    }

    /// <summary>Número de serie de Excel (días desde el 30/12/1899).</summary>
    private static string Serial(DateTime d) =>
        (d - new DateTime(1899, 12, 30)).TotalDays.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Number(string cell, string value, int style) =>
        $"""<c r="{cell}"{(style > 0 ? $" s=\"{style}\"" : "")}><v>{value}</v></c>""";

    /// <summary>Texto en línea. Excel no admite caracteres de control salvo tab y saltos de línea: se quitan.</summary>
    private static string Text(string cell, string value, int style)
    {
        var clean = new string(value.Where(ch => ch is '\t' or '\n' or '\r' || !char.IsControl(ch)).ToArray());
        if (clean.Length > 32767)
            clean = clean[..32767];
        return $"""<c r="{cell}" t="inlineStr"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space="preserve">{Esc(clean)}</t></is></c>""";
    }

    private static string Display(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString("#,0.##", CultureInfo.InvariantCulture),
        DateOnly or DateTime or DateTimeOffset => "0000-00-00",
        _ => value.ToString() ?? "",
    };

    /// <summary>Referencia A1 (columna desde 0, fila desde 1).</summary>
    internal static string Ref(int column, int row)
    {
        var name = "";
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
            name = (char)('A' + (c - 1) % 26) + name;
        return name + row.ToString(CultureInfo.InvariantCulture);
    }

    private static string Esc(string value) => SecurityElement.Escape(value) ?? "";
}
