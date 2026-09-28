using System.Text.RegularExpressions;
using System.Xml.Linq;
using OperCAD.Core.Documents;

namespace OperCAD.Application;

/// <summary>
/// Importa templates de legenda (.titleblock) do padrão CAD. O formato segue o
/// TitleBlockTemplate do padrão CAD: tag raiz &lt;titleblocktemplate&gt;, &lt;grid cols rows&gt;
/// com células &lt;field&gt;/&lt;logo&gt; (row, col, rowspan, colspan, name, align, valign,
/// displaylabel, hadjust) e textos em &lt;value&gt;/&lt;label&gt; com traduções por idioma.
/// </summary>
public static class TitleBlockTemplateImporter
{
    /// <summary>Traduções priorizadas para escolher o texto de uma célula.</summary>
    private static readonly string[] PreferredLangs = { "pt_BR", "pt", "en", "fr", "es" };

    public static TitleBlockTemplate? Parse(string filePath)
    {
        try
        {
            var doc = XDocument.Load(filePath);
            return Parse(doc.Root, Path.GetFileName(filePath));
        }
        catch
        {
            return null; // arquivo malformado: ignora
        }
    }

    public static TitleBlockTemplate? Parse(XElement? root, string? fileName = null)
    {
        if (root is null || root.Name.LocalName != "titleblocktemplate")
            return null;

        var tpl = new TitleBlockTemplate
        {
            FileName = fileName ?? string.Empty,
            Name = root.Attribute("name")?.Value ?? System.IO.Path.GetFileNameWithoutExtension(fileName ?? string.Empty),
            Information = root.Element("information")?.Value?.Trim() ?? string.Empty
        };

        // Preserva o XML bruto dos logos para a re-escrita (o editor não os edita).
        var logos = root.Element("logos");
        if (logos is not null)
            tpl.RawLogosXml = logos.ToString();

        var grid = root.Element("grid");
        if (grid is null)
            return null;

        ParseRows(grid.Attribute("rows")?.Value, tpl);
        ParseColumns(grid.Attribute("cols")?.Value, tpl);
        if (tpl.RowHeights.Count == 0 || tpl.ColumnWidths.Count == 0)
            return null;

        foreach (var cellEl in grid.Elements())
        {
            if (cellEl.Name.LocalName == "field" || cellEl.Name.LocalName == "logo")
                ParseCell(tpl, cellEl);
        }

        return tpl;
    }

    private static void ParseRows(string? rows, TitleBlockTemplate tpl)
    {
        if (string.IsNullOrWhiteSpace(rows))
            return;
        foreach (var desc in rows.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(desc.Trim(), @"^(\d+)(px)?$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var h))
                tpl.RowHeights.Add(h);
        }
    }

    private static void ParseColumns(string? cols, TitleBlockTemplate tpl)
    {
        if (string.IsNullOrWhiteSpace(cols))
            return;
        foreach (var desc in cols.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = desc.Trim();
            var abs = Regex.Match(v, @"^(\d+)(px)?$", RegexOptions.IgnoreCase);
            if (abs.Success && int.TryParse(abs.Groups[1].Value, out var a))
            {
                tpl.ColumnWidths.Add((TitleBlockColumnLength.Absolute, a));
                continue;
            }
            var rel = Regex.Match(v, @"^([rt])(\d+)%$", RegexOptions.IgnoreCase);
            if (rel.Success && int.TryParse(rel.Groups[2].Value, out var pct))
            {
                var type = rel.Groups[1].Value.Equals("t", StringComparison.OrdinalIgnoreCase)
                    ? TitleBlockColumnLength.RelativeToTotal
                    : TitleBlockColumnLength.RelativeToRemaining;
                tpl.ColumnWidths.Add((type, pct));
            }
        }
    }

    private static void ParseCell(TitleBlockTemplate tpl, XElement cellEl)
    {
        var cell = new TitleBlockCell
        {
            Row = IntAttr(cellEl, "row", -1),
            Col = IntAttr(cellEl, "col", -1),
            RowSpan = IntAttr(cellEl, "rowspan", 0),
            ColSpan = IntAttr(cellEl, "colspan", 0),
            Name = cellEl.Attribute("name")?.Value ?? string.Empty,
            DisplayLabel = BoolAttr(cellEl, "displaylabel", true),
            Align = cellEl.Attribute("align")?.Value ?? "center",
            Valign = cellEl.Attribute("valign")?.Value ?? "center",
            Hadjust = BoolAttr(cellEl, "hadjust", false),
            FontSize = IntAttr(cellEl, "font_size", 0)
        };

        if (cell.Row < 0 || cell.Col < 0)
            return;
        if (cell.Row >= tpl.RowHeights.Count || cell.Col >= tpl.ColumnWidths.Count)
            return;

        cell.Value = BestTranslation(cellEl.Element("value"));
        cell.Label = BestTranslation(cellEl.Element("label"));
        tpl.Cells.Add(cell);
    }

    private static string BestTranslation(XElement? container)
    {
        if (container is null)
            return string.Empty;
        var translations = container.Elements("translation").ToList();
        if (translations.Count == 0)
            return container.Value.Trim();

        foreach (var lang in PreferredLangs)
        {
            var hit = translations.FirstOrDefault(t =>
                (t.Attribute("lang")?.Value ?? string.Empty) == lang &&
                !string.IsNullOrEmpty(t.Value));
            if (hit is not null)
                return hit.Value.Trim();
        }
        var first = translations.FirstOrDefault(t => !string.IsNullOrEmpty(t.Value));
        return first?.Value.Trim() ?? string.Empty;
    }

    private static int IntAttr(XElement el, string name, int def)
        => int.TryParse(el.Attribute(name)?.Value, out var v) ? v : def;

    private static bool BoolAttr(XElement el, string name, bool def)
    {
        var a = el.Attribute(name)?.Value;
        return a is null ? def : a == "true";
    }
}
