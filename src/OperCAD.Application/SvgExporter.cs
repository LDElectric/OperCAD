using System.Text;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>
/// Exporta folhas do projeto para SVG (Scalable Vector Graphics).
/// Gera SVG vetorial a partir dos objetos da folha (elementos, condutores, textos e formas).
/// </summary>
public static class SvgExporter
{
    /// <summary>Exporta uma folha para SVG como string XML.</summary>
    /// <param name="page">Folha a exportar.</param>
    /// <param name="includeFrame">Incluir moldura e legenda.</param>
    /// <param name="transparentBackground">Fundo transparente.</param>
    public static string ExportToSvg(Page page, bool includeFrame = true, bool transparentBackground = false)
    {
        var w = page.Settings.Width;
        var h = page.Settings.Height;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                      $"width=\"{w}mm\" height=\"{h}mm\" viewBox=\"0 0 {w} {h}\">");

        // Fundo
        if (!transparentBackground)
        {
            sb.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"white\" />");
        }

        // Moldura (bordas e cabeçalhos de coluna/linha)
        if (includeFrame && page.Settings.ShowFrame)
        {
            RenderFrame(sb, page);
        }

        // Objetos da folha (elementos, textos, formas)
        foreach (var obj in page.Objects)
        {
            switch (obj)
            {
                case PlacedElement placed:
                    RenderPlacedElement(sb, placed, h);
                    break;
                case TextItem text:
                    RenderTextItem(sb, text);
                    break;
                case ShapeItem shape:
                    RenderShape(sb, shape);
                    break;
                case Conductor cond:
                    RenderConductor(sb, cond, page);
                    break;
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>Salva o SVG em um arquivo.</summary>
    public static void SaveToFile(Page page, string filePath, bool includeFrame = true, bool transparentBackground = false)
    {
        File.WriteAllText(filePath, ExportToSvg(page, includeFrame, transparentBackground));
    }

    private static void RenderFrame(StringBuilder sb, Page page)
    {
        var s = page.Settings;
        var bounds = s.FullBounds;
        var ph = s.Height;

        // Moldura externa — Y invertido (mundo Y-up → SVG Y-down)
        sb.AppendLine($"  <rect x=\"{bounds.MinX}\" y=\"{SvgY(bounds.MaxY, ph)}\" " +
                      $"width=\"{bounds.Width}\" height=\"{bounds.Height}\" " +
                      "fill=\"none\" stroke=\"black\" stroke-width=\"0.5\" />");

        // Área de desenho
        var draw = s.DrawingBounds;
        sb.AppendLine($"  <rect x=\"{draw.MinX}\" y=\"{SvgY(draw.MaxY, ph)}\" " +
                      $"width=\"{draw.Width}\" height=\"{draw.Height}\" " +
                      "fill=\"none\" stroke=\"black\" stroke-width=\"0.3\" />");

        // Cabeçalhos de colunas (faixa superior da folha)
        if (s.ShowColumns)
        {
            var cols = Math.Max(s.ColumnsCount, 1);
            var colW = draw.Width / cols;
            for (var i = 0; i <= cols; i++)
            {
                var x = draw.MinX + i * colW;
                sb.AppendLine($"  <line x1=\"{x}\" y1=\"{SvgY(draw.MaxY, ph)}\" x2=\"{x}\" y2=\"{SvgY(bounds.MaxY, ph)}\" " +
                              "stroke=\"black\" stroke-width=\"0.3\" />");
                if (i < cols)
                {
                    var letter = ElementLabels.ColumnLetter(i);
                    var textX = x + colW / 2;
                    var textY = SvgY(bounds.MaxY - s.ColumnsHeaderHeight / 2, ph);
                    sb.AppendLine($"  <text x=\"{textX}\" y=\"{textY}\" text-anchor=\"middle\" " +
                                  $"font-size=\"3\" font-family=\"Arial\">{letter}</text>");
                }
            }
        }

        // Cabeçalhos de linhas (faixa esquerda da folha)
        if (s.ShowRows)
        {
            var rows = Math.Max(s.RowsCount, 1);
            var rowH = draw.Height / rows;
            for (var i = 0; i <= rows; i++)
            {
                var yWorld = draw.MinY + i * rowH;
                var ySvg = SvgY(yWorld, ph);
                sb.AppendLine($"  <line x1=\"{draw.MinX}\" y1=\"{ySvg}\" x2=\"{bounds.MinX}\" y2=\"{ySvg}\" " +
                              "stroke=\"black\" stroke-width=\"0.3\" />");
                if (i < rows)
                {
                    var num = (rows - i).ToString();
                    var textX = bounds.MinX + s.RowsHeaderWidth / 2;
                    var textY = SvgY(yWorld + rowH / 2, ph);
                    sb.AppendLine($"  <text x=\"{textX}\" y=\"{textY}\" text-anchor=\"middle\" " +
                                  $"font-size=\"3\" font-family=\"Arial\">{num}</text>");
                }
            }
        }
    }

    private static void RenderPlacedElement(StringBuilder sb, PlacedElement element, double pageHeight)
    {
        var def = element.Definition;

        for (var i = 0; i < def.Primitives.Count; i++)
        {
            var prim = def.Primitives[i];
            if (element.TextOverrides.TryGetValue(i, out var ov) &&
                prim is ElementText t && (prim is not ElementDynamicText d || d.TextFrom == "UserText"))
            {
                prim = t with { Text = ov };
            }

            switch (prim)
            {
                case ElementLine line:
                    RenderLine(sb, line, element, pageHeight);
                    break;
                case ElementRect rect:
                    RenderRect(sb, rect, element, pageHeight);
                    break;
                case ElementEllipse ellipse:
                    RenderEllipse(sb, ellipse, element, pageHeight);
                    break;
                case ElementArc arc:
                    RenderArc(sb, arc, element, pageHeight);
                    break;
                case ElementPolygon polygon:
                    RenderPolygon(sb, polygon, element, pageHeight);
                    break;
                case ElementDynamicText dynText:
                    RenderDynamicText(sb, dynText, element, pageHeight);
                    break;
                case ElementText text:
                    RenderElementText(sb, text, element, pageHeight);
                    break;
            }
        }
    }

    private static void RenderLine(StringBuilder sb, ElementLine line, PlacedElement element, double ph)
    {
        var (x1, y1) = LocalToWorld(element, line.X1, line.Y1, ph);
        var (x2, y2) = LocalToWorld(element, line.X2, line.Y2, ph);
        var color = line.Style.Color ?? "black";
        var width = GetLineWidth(line.Style.LineWeight);
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" " +
                      $"stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\" />");
    }

    private static void RenderRect(StringBuilder sb, ElementRect rect, PlacedElement element, double ph)
    {
        var scale = element.Definition.Scale;
        var (x, y) = LocalToWorld(element, rect.X, rect.Y, ph);
        var color = rect.Style.Color ?? "black";
        var fill = rect.Style.Filling == "none" ? "none" : rect.Style.Filling;
        var width = GetLineWidth(rect.Style.LineWeight);
        var w = rect.Width * scale;
        var h = rect.Height * scale;
        if (rect.Rx > 0 || rect.Ry > 0)
            sb.AppendLine($"  <rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" " +
                          $"rx=\"{rect.Rx * scale}\" ry=\"{rect.Ry * scale}\" " +
                          $"stroke=\"{color}\" stroke-width=\"{width}\" fill=\"{fill}\" />");
        else
            sb.AppendLine($"  <rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" " +
                          $"stroke=\"{color}\" stroke-width=\"{width}\" fill=\"{fill}\" />");
    }

    private static void RenderEllipse(StringBuilder sb, ElementEllipse ellipse, PlacedElement element, double ph)
    {
        var scale = element.Definition.Scale;
        var (cx, cy) = LocalToWorld(element, ellipse.X + ellipse.Width / 2, ellipse.Y + ellipse.Height / 2, ph);
        var color = ellipse.Style.Color ?? "black";
        var fill = ellipse.Style.Filling == "none" ? "none" : ellipse.Style.Filling;
        var width = GetLineWidth(ellipse.Style.LineWeight);
        sb.AppendLine($"  <ellipse cx=\"{cx}\" cy=\"{cy}\" rx=\"{ellipse.Width * scale / 2}\" ry=\"{ellipse.Height * scale / 2}\" " +
                      $"stroke=\"{color}\" stroke-width=\"{width}\" fill=\"{fill}\" />");
    }

    private static void RenderArc(StringBuilder sb, ElementArc arc, PlacedElement element, double ph)
    {
        // Converte arco para path SVG
        var scale = element.Definition.Scale;
        var (cx, cy) = LocalToWorld(element, arc.X + arc.Width / 2, arc.Y + arc.Height / 2, ph);
        var color = arc.Style.Color ?? "black";
        var width = GetLineWidth(arc.Style.LineWeight);
        var rx = arc.Width * scale / 2;
        var ry = arc.Height * scale / 2;
        // Angulo negado para SVG Y-down
        var startRad = -arc.Start * Math.PI / 180;
        var angleRad = arc.Angle * Math.PI / 180;
        var x1 = cx + rx * Math.Cos(startRad);
        var y1 = cy + ry * Math.Sin(startRad);
        var endRad = startRad + angleRad;
        var x2 = cx + rx * Math.Cos(endRad);
        var y2 = cy + ry * Math.Sin(endRad);
        var largeArc = arc.Angle > 180 ? 1 : 0;
        sb.AppendLine($"  <path d=\"M {x1} {y1} A {rx} {ry} 0 {largeArc} 1 {x2} {y2}\" " +
                      $"stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\" />");
    }

    private static void RenderPolygon(StringBuilder sb, ElementPolygon polygon, PlacedElement element, double ph)
    {
        if (polygon.Points.Count < 2)
            return;
        var color = polygon.Style.Color ?? "black";
        var fill = polygon.Style.Filling == "none" ? "none" : polygon.Style.Filling;
        var width = GetLineWidth(polygon.Style.LineWeight);
        var d = new StringBuilder();
        var first = polygon.Points[0];
        var (fx, fy) = LocalToWorld(element, first.X, first.Y, ph);
        d.Append($"M {fx} {fy}");
        for (var i = 1; i < polygon.Points.Count; i++)
        {
            var (px, py) = LocalToWorld(element, polygon.Points[i].X, polygon.Points[i].Y, ph);
            d.Append($" L {px} {py}");
        }
        if (polygon.Closed)
            d.Append(" Z");
        sb.AppendLine($"  <path d=\"{d}\" stroke=\"{color}\" stroke-width=\"{width}\" fill=\"{fill}\" />");
    }

    private static void RenderElementText(StringBuilder sb, ElementText text, PlacedElement element, double ph)
    {
        var scale = element.Definition.Scale;
        var (x, y) = LocalToWorld(element, text.X, text.Y, ph);
        var color = text.Color ?? "black";
        var fontSize = ParseFontSize(text.Font) * scale;
        var anchor = text.Halign switch
        {
            ElementTextHAlign.Right => "end",
            ElementTextHAlign.Center => "middle",
            _ => "start"
        };
        // Rotação negada para SVG Y-down
        var rotation = text.Rotation != 0 ? $" transform=\"rotate({-text.Rotation}, {x}, {y})\"" : "";
        sb.AppendLine($"  <text x=\"{x}\" y=\"{y}\" font-size=\"{fontSize}\" font-family=\"Arial\" " +
                      $"fill=\"{color}\" text-anchor=\"{anchor}\"{rotation}>{EscapeXml(text.Text)}</text>");
    }

    private static void RenderDynamicText(StringBuilder sb, ElementDynamicText dynText, PlacedElement element, double ph)
    {
        // Texto dinâmico: exibe o valor da etiqueta ou informação do elemento
        var value = dynText.TextFrom switch
        {
            "ElementInfo" => dynText.InfoName.ToLowerInvariant() switch
            {
                "label" => element.Label,
                "comment" => element.Comment,
                "function" => element.Function,
                "description" => element.Description,
                "manufacturer" => element.Manufacturer,
                "designation" => element.Designation,
                _ => element.Label
            },
            "UserText" => dynText.Text,
            _ => dynText.Text
        };

        if (string.IsNullOrEmpty(value))
            return;

        var scale = element.Definition.Scale;
        var (x, y) = LocalToWorld(element, dynText.X, dynText.Y, ph);
        var color = dynText.Color ?? "black";
        var fontSize = ParseFontSize(dynText.Font) * scale;
        var anchor = dynText.Halign switch
        {
            ElementTextHAlign.Right => "end",
            ElementTextHAlign.Center => "middle",
            _ => "start"
        };
        sb.AppendLine($"  <text x=\"{x}\" y=\"{y}\" font-size=\"{fontSize}\" font-family=\"Arial\" " +
                      $"fill=\"{color}\" text-anchor=\"{anchor}\">{EscapeXml(value)}</text>");
    }

    private static void RenderTextItem(StringBuilder sb, TextItem text)
    {
        var color = text.Color ?? "#000000";
        var anchor = text.TextAlign switch
        {
            "center" => "middle",
            "right" => "end",
            _ => "start"
        };
        var style = "";
        if (text.Bold || text.Italic || text.Underline || text.Strikethrough)
        {
            var parts = new List<string>();
            if (text.Bold) parts.Add("font-weight:bold");
            if (text.Italic) parts.Add("font-style:italic");
            if (text.Underline) parts.Add("text-decoration:underline");
            if (text.Strikethrough) parts.Add("text-decoration:line-through");
            style = $" style=\"{string.Join(";", parts)}\"";
        }
        var rotation = text.Rotation != 0 ? $" transform=\"rotate({text.Rotation}, {text.X}, {text.Y})\"" : "";
        sb.AppendLine($"  <text x=\"{text.X}\" y=\"{text.Y}\" font-size=\"{text.FontSizePt}\" " +
                      $"font-family=\"{EscapeXml(text.FontFamily)}\" font-weight=\"{(text.Bold ? "bold" : "normal")}\" " +
                      $"fill=\"{color}\" text-anchor=\"{anchor}\"{style}{rotation}>{EscapeXml(text.Text)}</text>");
    }

    private static void RenderShape(StringBuilder sb, ShapeItem shape)
    {
        if (shape.Points.Count < 2)
            return;
        var color = shape.Color ?? "black";
        var width = Math.Max(shape.PenWidth, 0.3);
        var dashAttr = GetDashArrayAttr(shape.DashStyle);

        switch (shape.Kind)
        {
            case ShapeKind.Line:
                sb.AppendLine($"  <line x1=\"{shape.Points[0].X}\" y1=\"{shape.Points[0].Y}\" " +
                              $"x2=\"{shape.Points[1].X}\" y2=\"{shape.Points[1].Y}\" " +
                              $"stroke=\"{color}\" stroke-width=\"{width}\"{dashAttr} />");
                break;
            case ShapeKind.Rectangle:
            {
                var x = Math.Min(shape.Points[0].X, shape.Points[1].X);
                var y = Math.Min(shape.Points[0].Y, shape.Points[1].Y);
                var w2 = Math.Abs(shape.Points[1].X - shape.Points[0].X);
                var h2 = Math.Abs(shape.Points[1].Y - shape.Points[0].Y);
                sb.AppendLine($"  <rect x=\"{x}\" y=\"{y}\" width=\"{w2}\" height=\"{h2}\" " +
                              $"stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\"{dashAttr} />");
                break;
            }
            case ShapeKind.Ellipse:
            {
                var cx = (shape.Points[0].X + shape.Points[1].X) / 2;
                var cy = (shape.Points[0].Y + shape.Points[1].Y) / 2;
                var rx = Math.Abs(shape.Points[1].X - shape.Points[0].X) / 2;
                var ry = Math.Abs(shape.Points[1].Y - shape.Points[0].Y) / 2;
                sb.AppendLine($"  <ellipse cx=\"{cx}\" cy=\"{cy}\" rx=\"{rx}\" ry=\"{ry}\" " +
                              $"stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\"{dashAttr} />");
                break;
            }
            case ShapeKind.Polygon:
            {
                var d = new StringBuilder();
                d.Append($"M {shape.Points[0].X} {shape.Points[0].Y}");
                for (var i = 1; i < shape.Points.Count; i++)
                    d.Append($" L {shape.Points[i].X} {shape.Points[i].Y}");
                if (shape.Closed)
                    d.Append(" Z");
                sb.AppendLine($"  <path d=\"{d}\" stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\"{dashAttr} />");
                break;
            }
            case ShapeKind.Curve:
            {
                if (shape.Points.Count < 2) break;
                var dCurve = new StringBuilder();
                dCurve.Append($"M {shape.Points[0].X:F2} {shape.Points[0].Y:F2}");

                if (shape.ControlPoints is { } cp && cp.Count > 0)
                {
                    var segCount = Math.Min(shape.Points.Count - 1, cp.Count);
                    for (var i = 0; i < segCount; i++)
                    {
                        var (c1, c2) = cp[i];
                        var end = shape.Points[i + 1];
                        dCurve.Append($" C {c1.X:F2} {c1.Y:F2} {c2.X:F2} {c2.Y:F2} {end.X:F2} {end.Y:F2}");
                    }
                }
                else
                {
                    var flat = ShapeGeometry.FlattenCurve(shape.Points, shape.Closed);
                    for (var i = 1; i < flat.Count; i++)
                        dCurve.Append($" L {flat[i].X:F2} {flat[i].Y:F2}");
                }

                if (shape.Closed)
                    dCurve.Append(" Z");
                sb.AppendLine($"  <path d=\"{dCurve}\" stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\"{dashAttr} />");
                break;
            }
            case ShapeKind.Arc:
            case ShapeKind.EllipseArc:
            {
                var center = shape.Points[0];
                double rx, ry;
                if (shape.Kind == ShapeKind.Arc)
                {
                    var r = center.DistanceTo(shape.Points[1]);
                    rx = ry = r;
                }
                else
                {
                    var q = shape.Points[1];
                    rx = Math.Abs(q.X - center.X);
                    ry = Math.Abs(q.Y - center.Y);
                }
                if (rx <= 0 || ry <= 0) break;
                var steps = Math.Max(12, (int)(Math.Abs(shape.SweepAngle) * 36 / 360));
                var startRad = shape.StartAngle * Math.PI / 180.0;
                var sweepRad = shape.SweepAngle * Math.PI / 180.0;
                var d = new StringBuilder();
                for (var i = 0; i <= steps; i++)
                {
                    var t = startRad + sweepRad * i / steps;
                    var px = center.X + rx * Math.Cos(t);
                    var py = center.Y + ry * Math.Sin(t);
                    d.Append(i == 0 ? $"M {px:F2} {py:F2}" : $" L {px:F2} {py:F2}");
                }
                sb.AppendLine($"  <path d=\"{d}\" stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\"{dashAttr} />");
                break;
            }
        }
    }

    private static string GetDashArrayAttr(LineDashStyle dashStyle) => dashStyle switch
    {
        LineDashStyle.Dashed => " stroke-dasharray=\"8,4\"",
        LineDashStyle.Dotted => " stroke-dasharray=\"2,4\"",
        LineDashStyle.DashDot => " stroke-dasharray=\"8,4,2,4\"",
        _ => ""
    };

    private static void RenderConductor(StringBuilder sb, Conductor conductor, Page page)
    {
        if (conductor.Path.Count < 2)
            return;
        var ph = page.Settings.Height;
        var color = conductor.Color ?? "black";
        var width = Math.Max(conductor.Width, 0.3);

        var d = new StringBuilder();
        d.Append($"M {conductor.Path[0].X} {SvgY(conductor.Path[0].Y, ph)}");
        for (var i = 1; i < conductor.Path.Count; i++)
            d.Append($" L {conductor.Path[i].X} {SvgY(conductor.Path[i].Y, ph)}");

        sb.AppendLine($"  <path d=\"{d}\" stroke=\"{color}\" stroke-width=\"{width}\" fill=\"none\" " +
                      $"stroke-linejoin=\"round\" />");

        // Texto/número do condutor
        if (!string.IsNullOrEmpty(conductor.Text) && conductor.DisplayText && conductor.Path.Count >= 2)
        {
            var mid = conductor.Path[conductor.Path.Count / 2];
            sb.AppendLine($"  <text x=\"{mid.X}\" y=\"{SvgY(mid.Y - 1, ph)}\" font-size=\"{conductor.NumSize}\" " +
                          $"font-family=\"Arial\" fill=\"{conductor.TextColor ?? "#000000"}\" " +
                          $"text-anchor=\"middle\">{EscapeXml(conductor.Text)}</text>");
        }
    }

    private static (double X, double Y) LocalToWorld(PlacedElement element, double lx, double ly, double pageHeight)
    {
        // Converte coordenadas locais do elemento para SVG
        // Aplica escala, hotspot, posição e rotação, com Y invertido
        var def = element.Definition;
        var scale = def.Scale;
        var hsX = def.HotspotX * scale;
        var hsY = def.HotspotY * scale;

        // Escala + hotspot offset
        var mlx = lx * scale - hsX;
        var mly = ly * scale - hsY;

        // Rotação (angulo negado para SVG Y-down)
        var rad = -element.Rotation * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var wx = element.X + mlx * cos - mly * sin;
        var wy = SvgY(element.Y + mlx * sin + mly * cos, pageHeight);
        return (wx, wy);
    }

    /// <summary>Converte coordenada Y do mundo (Y-up) para SVG (Y-down).</summary>
    private static double SvgY(double worldY, double pageHeight) => pageHeight - worldY;

    private static double GetLineWidth(ElementLineWeight weight) => weight switch
    {
        ElementLineWeight.Thin => 0.2,
        ElementLineWeight.Normal => 0.4,
        ElementLineWeight.Medium => 0.7,
        ElementLineWeight.Thick => 1.0,
        _ => 0.4
    };

    private static double ParseFontSize(string font)
    {
        // Extrai tamanho da string de fonte Qt-style: "Liberation Sans,5,-1,5,50,0,0,0,0,0,Regular"
        var parts = font.Split(',');
        if (parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var size))
            return Math.Abs(size);
        return 3.5; // Tamanho padrão
    }

    private static string EscapeXml(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
