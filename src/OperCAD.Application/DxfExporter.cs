using System.Globalization;
using System.Text;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>Exporta elementos do esquema para formato DXF.</summary>
public static class DxfExporter
{
    /// <summary>Exporta uma folha para DXF (formato AC1009 / R12).</summary>
    public static string ExportToDxf(Page page)
    {
        var sb = new StringBuilder();
        var format = CultureInfo.InvariantCulture;

        // Cabeçalho DXF
        sb.AppendLine("0");
        sb.AppendLine("SECTION");
        sb.AppendLine("2");
        sb.AppendLine("HEADER");
        sb.AppendLine("9");
        sb.AppendLine("$ACADVER");
        sb.AppendLine("1");
        sb.AppendLine("AC1009");
        sb.AppendLine("9");
        sb.AppendLine("$INSBASE");
        sb.AppendLine("10");
        sb.AppendLine("0.0");
        sb.AppendLine("20");
        sb.AppendLine("0.0");
        sb.AppendLine("30");
        sb.AppendLine("0.0");
        sb.AppendLine("0");
        sb.AppendLine("ENDSEC");

        // Tabelas
        sb.AppendLine("0");
        sb.AppendLine("SECTION");
        sb.AppendLine("2");
        sb.AppendLine("TABLES");
        sb.AppendLine("0");
        sb.AppendLine("TABLE");
        sb.AppendLine("2");
        sb.AppendLine("LAYER");
        sb.AppendLine("70");
        sb.AppendLine("2");
        // Camada padrão
        sb.AppendLine("0");
        sb.AppendLine("LAYER");
        sb.AppendLine("2");
        sb.AppendLine("0");
        sb.AppendLine("70");
        sb.AppendLine("0");
        sb.AppendLine("62");
        sb.AppendLine("7");
        sb.AppendLine("6");
        sb.AppendLine("CONTINUOUS");
        // Camada de elementos
        sb.AppendLine("0");
        sb.AppendLine("LAYER");
        sb.AppendLine("2");
        sb.AppendLine("ELEMENTOS");
        sb.AppendLine("70");
        sb.AppendLine("0");
        sb.AppendLine("62");
        sb.AppendLine("1");
        sb.AppendLine("6");
        sb.AppendLine("CONTINUOUS");
        sb.AppendLine("0");
        sb.AppendLine("ENDTAB");
        sb.AppendLine("0");
        sb.AppendLine("ENDSEC");

        // Blocos
        sb.AppendLine("0");
        sb.AppendLine("SECTION");
        sb.AppendLine("2");
        sb.AppendLine("BLOCKS");
        sb.AppendLine("0");
        sb.AppendLine("ENDSEC");

        // Entidades
        sb.AppendLine("0");
        sb.AppendLine("SECTION");
        sb.AppendLine("2");
        sb.AppendLine("ENTITIES");

        // Exportar elementos posicionados
        foreach (var obj in page.Objects)
        {
            switch (obj)
            {
                case PlacedElement element:
                    ExportPlacedElement(sb, element, format);
                    break;
                case Conductor conductor:
                    ExportConductor(sb, conductor, format);
                    break;
            }
        }

        sb.AppendLine("0");
        sb.AppendLine("ENDSEC");

        sb.AppendLine("0");
        sb.AppendLine("EOF");

        return sb.ToString();
    }

    /// <summary>Salva a folha em formato DXF.</summary>
    public static void SaveToFile(Page page, string filePath)
    {
        File.WriteAllText(filePath, ExportToDxf(page));
    }

    private static void ExportPlacedElement(StringBuilder sb, PlacedElement element, CultureInfo format)
    {
        var def = element.Definition;
        var scale = def.Scale;

        // Hotspot offset: subtrai o hotspot e aplica escala para converter para mm do mundo
        var hsX = def.HotspotX * scale;
        var hsY = def.HotspotY * scale;

        // Aplicar transformação: posição + rotação
        var cos = Math.Cos(element.Rotation * Math.PI / 180.0);
        var sin = Math.Sin(element.Rotation * Math.PI / 180.0);

        for (var i = 0; i < def.Primitives.Count; i++)
        {
            var primitive = def.Primitives[i];
            if (element.TextOverrides.TryGetValue(i, out var ov) &&
                primitive is ElementText t && (primitive is not ElementDynamicText d || d.TextFrom == "UserText"))
            {
                primitive = t with { Text = ov };
            }

            switch (primitive)
            {
                case ElementLine line:
                    ExportLine(sb, line, element.X, element.Y, hsX, hsY, scale, cos, sin, format);
                    break;
                case ElementRect rect:
                    ExportRect(sb, rect, element.X, element.Y, hsX, hsY, scale, cos, sin, format);
                    break;
                case ElementEllipse ellipse:
                    ExportEllipse(sb, ellipse, element.X, element.Y, hsX, hsY, scale, cos, sin, format);
                    break;
                case ElementArc arc:
                    ExportArc(sb, arc, element.X, element.Y, hsX, hsY, scale, cos, sin, format);
                    break;
                case ElementPolygon polygon:
                    ExportPolygon(sb, polygon, element.X, element.Y, hsX, hsY, scale, cos, sin, format);
                    break;
                case ElementText text:
                    ExportText(sb, text, element.X, element.Y, hsX, hsY, scale, cos, sin, format);
                    break;
            }
        }

        // Exportar terminais como pontos
        foreach (var terminal in def.Terminals)
        {
            var lx = (terminal.X * scale) - hsX;
            var ly = (terminal.Y * scale) - hsY;
            var tx = element.X + lx * cos - ly * sin;
            var ty = element.Y + lx * sin + ly * cos;
            ExportPoint(sb, tx, ty, $"TERMINAL_{terminal.Name}", format);
        }
    }

    private static void ExportConductor(StringBuilder sb, Conductor conductor, CultureInfo format)
    {
        if (conductor.Path.Count < 2)
            return;

        // Exportar caminho do condutor como polilinha
        sb.AppendLine("0");
        sb.AppendLine("LWPOLYLINE");
        sb.AppendLine("8");
        sb.AppendLine("CONdutores");
        sb.AppendLine("70");
        sb.AppendLine("0"); // Aberto
        sb.AppendLine("43");
        sb.AppendLine(conductor.Width.ToString(format));

        foreach (var point in conductor.Path)
        {
            sb.AppendLine("10");
            sb.AppendLine(point.X.ToString(format));
            sb.AppendLine("20");
            sb.AppendLine(point.Y.ToString(format));
        }

        // Texto do condutor (se houver)
        if (!string.IsNullOrEmpty(conductor.Text) && conductor.Path.Count > 0)
        {
            var midIndex = conductor.Path.Count / 2;
            var midPoint = conductor.Path[midIndex];
            ExportText(sb, new ElementText
            {
                Text = conductor.Text,
                X = midPoint.X,
                Y = midPoint.Y,
                Font = $"Arial,{conductor.NumSize},-1,5,50,0,0,0,0,0,Regular",
                Color = conductor.TextColor
            }, 0, 0, 0, 0, 1, 1, 0, format);
        }
    }

    private static void ExportLine(StringBuilder sb, ElementLine line,
        double offsetX, double offsetY, double hsX, double hsY, double scale, double cos, double sin, CultureInfo format)
    {
        var lx1 = line.X1 * scale - hsX;
        var ly1 = line.Y1 * scale - hsY;
        var lx2 = line.X2 * scale - hsX;
        var ly2 = line.Y2 * scale - hsY;
        var x1 = offsetX + lx1 * cos - ly1 * sin;
        var y1 = offsetY + lx1 * sin + ly1 * cos;
        var x2 = offsetX + lx2 * cos - ly2 * sin;
        var y2 = offsetY + lx2 * sin + ly2 * cos;

        sb.AppendLine("0");
        sb.AppendLine("LINE");
        sb.AppendLine("8");
        sb.AppendLine("0");
        sb.AppendLine("10");
        sb.AppendLine(x1.ToString(format));
        sb.AppendLine("20");
        sb.AppendLine(y1.ToString(format));
        sb.AppendLine("11");
        sb.AppendLine(x2.ToString(format));
        sb.AppendLine("21");
        sb.AppendLine(y2.ToString(format));
    }

    private static void ExportRect(StringBuilder sb, ElementRect rect,
        double offsetX, double offsetY, double hsX, double hsY, double scale, double cos, double sin, CultureInfo format)
    {
        // Converter retângulo em 4 linhas
        var corners = new[]
        {
            new Point2D(rect.X * scale - hsX, rect.Y * scale - hsY),
            new Point2D((rect.X + rect.Width) * scale - hsX, rect.Y * scale - hsY),
            new Point2D((rect.X + rect.Width) * scale - hsX, (rect.Y + rect.Height) * scale - hsY),
            new Point2D(rect.X * scale - hsX, (rect.Y + rect.Height) * scale - hsY)
        };

        for (int i = 0; i < 4; i++)
        {
            var p1 = corners[i];
            var p2 = corners[(i + 1) % 4];
            var x1 = offsetX + p1.X * cos - p1.Y * sin;
            var y1 = offsetY + p1.X * sin + p1.Y * cos;
            var x2 = offsetX + p2.X * cos - p2.Y * sin;
            var y2 = offsetY + p2.X * sin + p2.Y * cos;

            sb.AppendLine("0");
            sb.AppendLine("LINE");
            sb.AppendLine("8");
            sb.AppendLine("0");
            sb.AppendLine("10");
            sb.AppendLine(x1.ToString(format));
            sb.AppendLine("20");
            sb.AppendLine(y1.ToString(format));
            sb.AppendLine("11");
            sb.AppendLine(x2.ToString(format));
            sb.AppendLine("21");
            sb.AppendLine(y2.ToString(format));
        }
    }

    private static void ExportEllipse(StringBuilder sb, ElementEllipse ellipse,
        double offsetX, double offsetY, double hsX, double hsY, double scale, double cos, double sin, CultureInfo format)
    {
        // Exportar como círculo se for circular
        if (Math.Abs(ellipse.Width - ellipse.Height) < 0.01)
        {
            var lx = (ellipse.X + ellipse.Width / 2) * scale - hsX;
            var ly = (ellipse.Y + ellipse.Height / 2) * scale - hsY;
            var cx = offsetX + lx * cos - ly * sin;
            var cy = offsetY + lx * sin + ly * cos;
            var radius = ellipse.Width * scale / 2;

            sb.AppendLine("0");
            sb.AppendLine("CIRCLE");
            sb.AppendLine("8");
            sb.AppendLine("0");
            sb.AppendLine("10");
            sb.AppendLine(cx.ToString(format));
            sb.AppendLine("20");
            sb.AppendLine(cy.ToString(format));
            sb.AppendLine("40");
            sb.AppendLine(radius.ToString(format));
        }
        else
        {
            // Exportar como elipse DXF
            var lx = (ellipse.X + ellipse.Width / 2) * scale - hsX;
            var ly = (ellipse.Y + ellipse.Height / 2) * scale - hsY;
            var cx = offsetX + lx * cos - ly * sin;
            var cy = offsetY + lx * sin + ly * cos;
            var majorRadius = Math.Max(ellipse.Width, ellipse.Height) * scale / 2;
            var minorRadius = Math.Min(ellipse.Width, ellipse.Height) * scale / 2;
            var ratio = minorRadius / majorRadius;

            sb.AppendLine("0");
            sb.AppendLine("ELLIPSE");
            sb.AppendLine("8");
            sb.AppendLine("0");
            sb.AppendLine("10");
            sb.AppendLine(cx.ToString(format));
            sb.AppendLine("20");
            sb.AppendLine(cy.ToString(format));
            // Endpoint do eixo maior
            sb.AppendLine("11");
            sb.AppendLine(majorRadius.ToString(format));
            sb.AppendLine("21");
            sb.AppendLine("0.0");
            sb.AppendLine("40");
            sb.AppendLine(ratio.ToString(format));
            sb.AppendLine("41");
            sb.AppendLine("0.0");
            sb.AppendLine("42");
            sb.AppendLine((2 * Math.PI).ToString(format));
        }
    }

    private static void ExportArc(StringBuilder sb, ElementArc arc,
        double offsetX, double offsetY, double hsX, double hsY, double scale, double cos, double sin, CultureInfo format)
    {
        var lx = (arc.X + arc.Width / 2) * scale - hsX;
        var ly = (arc.Y + arc.Height / 2) * scale - hsY;
        var cx = offsetX + lx * cos - ly * sin;
        var cy = offsetY + lx * sin + ly * cos;
        var radius = arc.Width * scale / 2;

        sb.AppendLine("0");
        sb.AppendLine("ARC");
        sb.AppendLine("8");
        sb.AppendLine("0");
        sb.AppendLine("10");
        sb.AppendLine(cx.ToString(format));
        sb.AppendLine("20");
        sb.AppendLine(cy.ToString(format));
        sb.AppendLine("40");
        sb.AppendLine(radius.ToString(format));
        sb.AppendLine("50");
        sb.AppendLine(arc.Start.ToString(format));
        sb.AppendLine("51");
        sb.AppendLine((arc.Start + arc.Angle).ToString(format));
    }

    private static void ExportPolygon(StringBuilder sb, ElementPolygon polygon,
        double offsetX, double offsetY, double hsX, double hsY, double scale, double cos, double sin, CultureInfo format)
    {
        if (polygon.Points.Count < 2)
            return;

        sb.AppendLine("0");
        sb.AppendLine("LWPOLYLINE");
        sb.AppendLine("8");
        sb.AppendLine("0");
        sb.AppendLine("70");
        sb.AppendLine(polygon.Closed ? "1" : "0");

        foreach (var point in polygon.Points)
        {
            var lx = point.X * scale - hsX;
            var ly = point.Y * scale - hsY;
            var x = offsetX + lx * cos - ly * sin;
            var y = offsetY + lx * sin + ly * cos;
            sb.AppendLine("10");
            sb.AppendLine(x.ToString(format));
            sb.AppendLine("20");
            sb.AppendLine(y.ToString(format));
        }
    }

    private static void ExportText(StringBuilder sb, ElementText text,
        double offsetX, double offsetY, double hsX, double hsY, double scale, double cos, double sin, CultureInfo format)
    {
        var lx = text.X * scale - hsX;
        var ly = text.Y * scale - hsY;
        var x = offsetX + lx * cos - ly * sin;
        var y = offsetY + lx * sin + ly * cos;

        // Extrair tamanho da fonte da string Font
        var fontSize = 2.5;
        var fontParts = text.Font.Split(',');
        if (fontParts.Length > 1 && double.TryParse(fontParts[1], NumberStyles.Float, format, out var fs))
            fontSize = fs;
        fontSize *= scale;

        sb.AppendLine("0");
        sb.AppendLine("TEXT");
        sb.AppendLine("8");
        sb.AppendLine("0");
        sb.AppendLine("10");
        sb.AppendLine(x.ToString(format));
        sb.AppendLine("20");
        sb.AppendLine(y.ToString(format));
        sb.AppendLine("40");
        sb.AppendLine(fontSize.ToString(format));
        sb.AppendLine("1");
        sb.AppendLine(text.Text);
        sb.AppendLine("50");
        sb.AppendLine(text.Rotation.ToString(format));
    }

    private static void ExportPoint(StringBuilder sb, double x, double y, string name, CultureInfo format)
    {
        sb.AppendLine("0");
        sb.AppendLine("POINT");
        sb.AppendLine("8");
        sb.AppendLine("TERMINAIS");
        sb.AppendLine("10");
        sb.AppendLine(x.ToString(format));
        sb.AppendLine("20");
        sb.AppendLine(y.ToString(format));
        sb.AppendLine("0");
        sb.AppendLine("TEXT");
        sb.AppendLine("8");
        sb.AppendLine("TERMINAIS");
        sb.AppendLine("10");
        sb.AppendLine(x.ToString(format));
        sb.AppendLine("20");
        sb.AppendLine((y + 1.0).ToString(format));
        sb.AppendLine("40");
        sb.AppendLine("1.5");
        sb.AppendLine("1");
        sb.AppendLine(name);
    }
}
