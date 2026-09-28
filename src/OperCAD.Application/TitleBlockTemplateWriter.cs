using System.Text;
using System.Xml.Linq;
using OperCAD.Core.Documents;

namespace OperCAD.Application;

/// <summary>
/// Grava templates de legenda (.titleblock) no formato do padrão CAD 0.100
/// (inverso do TitleBlockTemplateImporter): &lt;titleblocktemplate&gt; com
/// &lt;grid cols rows&gt; e células &lt;field&gt; com &lt;value&gt;/&lt;label&gt;
/// traduzidos (pt_BR). Logos são re-emitidos do XML bruto preservado no import.
/// </summary>
public static class TitleBlockTemplateWriter
{
    public static void Save(TitleBlockTemplate template, string filePath)
    {
        var root = new XElement("titleblocktemplate",
            new XAttribute("name", template.Name ?? string.Empty));

        root.Add(new XElement("information", template.Information ?? string.Empty));

        // Logos (preservados do arquivo original)
        if (!string.IsNullOrWhiteSpace(template.RawLogosXml))
        {
            try
            {
                root.Add(XElement.Parse(template.RawLogosXml));
            }
            catch
            {
                // XML de logo inválido: ignora
            }
        }

        var grid = new XElement("grid",
            new XAttribute("cols", ColumnsSpec(template)),
            new XAttribute("rows", string.Join(";", template.RowHeights)));

        foreach (var cell in template.Cells)
        {
            grid.Add(new XElement("field",
                new XAttribute("row", cell.Row),
                new XAttribute("col", cell.Col),
                new XAttribute("rowspan", cell.RowSpan),
                new XAttribute("colspan", cell.ColSpan),
                new XAttribute("name", cell.Name ?? string.Empty),
                new XAttribute("displaylabel", BoolStr(cell.DisplayLabel)),
                new XAttribute("align", cell.Align ?? "center"),
                new XAttribute("valign", cell.Valign ?? "center"),
                new XAttribute("hadjust", BoolStr(cell.Hadjust)),
                new XAttribute("font_size", cell.FontSize),
                new XElement("value",
                    new XElement("translation", new XAttribute("lang", "pt_BR"), cell.Value ?? string.Empty)),
                new XElement("label",
                    new XElement("translation", new XAttribute("lang", "pt_BR"), cell.Label ?? string.Empty))));
        }

        root.Add(grid);

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var writer = new Utf8StringWriter();
        doc.Save(writer, SaveOptions.None);
        System.IO.File.WriteAllText(filePath, writer.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Monta a especificação de colunas (ex.: "t6%;t4%;r100%;50px;").</summary>
    public static string ColumnsSpec(TitleBlockTemplate template)
    {
        var parts = new List<string>();
        foreach (var (type, value) in template.ColumnWidths)
        {
            parts.Add(type switch
            {
                TitleBlockColumnLength.Absolute => $"{value}px",
                TitleBlockColumnLength.RelativeToTotal => $"t{value}%",
                TitleBlockColumnLength.RelativeToRemaining => $"r{value}%",
                _ => $"{value}px"
            });
        }
        return string.Join(";", parts) + ";";
    }

    private static string BoolStr(bool v) => v ? "true" : "false";

    private sealed class Utf8StringWriter : System.IO.StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
