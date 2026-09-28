using System.Globalization;
using System.Text.RegularExpressions;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>Importa elementos de arquivos DXF para o editor de elementos.</summary>
public static class DxfImporter
{
    /// <summary>Importa primitivas de um arquivo DXF para um ElementDefinition.</summary>
    public static ElementDefinition ImportFromDxf(string filePath)
    {
        var definition = new ElementDefinition
        {
            Name = Path.GetFileNameWithoutExtension(filePath)
        };

        if (!File.Exists(filePath))
            return definition;

        var lines = File.ReadAllLines(filePath);
        var entities = ParseEntities(lines);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var entity in entities)
        {
            foreach (var primitive in ConvertEntity(entity))
            {
                definition.Primitives.Add(primitive);
                UpdateBounds(primitive, ref minX, ref minY, ref maxX, ref maxY);
            }
        }

        // Definir dimensões e hotspot com base no bounding box
        if (minX < maxX && minY < maxY)
        {
            var width = maxX - minX;
            var height = maxY - minY;
            var hotspotX = minX + width / 2.0;
            var hotspotY = minY + height / 2.0;

            // Normalizar primitivas para coordenadas relativas ao hotspot
            var offsetX = -hotspotX;
            var offsetY = -hotspotY;
            var normalizedPrimitives = definition.Primitives
                .Select(p => OffsetPrimitive(p, offsetX, offsetY))
                .ToList();

            definition = definition with
            {
                Width = width,
                Height = height,
                HotspotX = hotspotX,
                HotspotY = hotspotY,
                Primitives = normalizedPrimitives
            };
        }

        return definition;
    }

    /// <summary>Entidade DXF bruta extraída do arquivo.</summary>
    private sealed class DxfEntity
    {
        public string Type { get; set; } = string.Empty;
        public Dictionary<int, string> Codes { get; } = new();
        public List<List<double>> Points { get; set; } = new();
    }

    private static List<DxfEntity> ParseEntities(string[] lines)
    {
        var entities = new List<DxfEntity>();
        DxfEntity? current = null;
        bool inEntities = false;
        int i = 0;

        while (i < lines.Length - 1)
        {
            var code = lines[i].Trim();
            var value = lines[i + 1].Trim();

            if (code == "0" && value == "ENTITIES")
            {
                inEntities = true;
                i += 2;
                continue;
            }

            if (code == "0" && value == "ENDSEC" && inEntities)
            {
                inEntities = false;
                i += 2;
                continue;
            }

            if (inEntities && code == "0")
            {
                if (current != null)
                    entities.Add(current);

                current = new DxfEntity { Type = value };
                i += 2;
                continue;
            }

            if (inEntities && current != null && int.TryParse(code, out var groupCode))
            {
                current.Codes[groupCode] = value;

                // Coordenadas de ponto: 10=inicio X, 20=inicio Y, 30=inicio Z
                //                        11=fim X, 21=fim Y, 31=fim Z
                if (groupCode is 10 or 11 or 20 or 21)
                {
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        // Adicionar ponto será processado depois
                    }
                }
            }

            i += 2;
        }

        if (current != null)
            entities.Add(current);

        return entities;
    }

    private static List<ElementPrimitive> ConvertEntity(DxfEntity entity)
    {
        var primitives = new List<ElementPrimitive>();

        switch (entity.Type)
        {
            case "LINE":
                ConvertLine(entity, primitives);
                break;
            case "CIRCLE":
                ConvertCircle(entity, primitives);
                break;
            case "ARC":
                ConvertArc(entity, primitives);
                break;
            case "LWPOLYLINE":
                ConvertLwPolyline(entity, primitives);
                break;
            case "POLYLINE":
                ConvertPolyline(entity, primitives);
                break;
            case "TEXT":
            case "MTEXT":
                ConvertText(entity, primitives);
                break;
            case "ELLIPSE":
                ConvertEllipse(entity, primitives);
                break;
        }

        return primitives;
    }

    private static void ConvertLine(DxfEntity entity, List<ElementPrimitive> primitives)
    {
        if (!TryGetPoint(entity, 10, out var x1, out var y1) ||
            !TryGetPoint(entity, 11, out var x2, out var y2))
            return;

        primitives.Add(new ElementLine
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Style = ParseStyle(entity)
        });
    }

    private static void ConvertCircle(DxfEntity entity, List<ElementPrimitive> primitives)
    {
        if (!TryGetPoint(entity, 10, out var cx, out var cy))
            return;

        if (!TryGetDouble(entity, 40, out var radius))
            return;

        primitives.Add(new ElementEllipse
        {
            X = cx - radius,
            Y = cy - radius,
            Width = radius * 2,
            Height = radius * 2,
            Style = ParseStyle(entity)
        });
    }

    private static void ConvertArc(DxfEntity entity, List<ElementPrimitive> primitives)
    {
        if (!TryGetPoint(entity, 10, out var cx, out var cy))
            return;

        if (!TryGetDouble(entity, 40, out var radius))
            return;

        if (!TryGetDouble(entity, 50, out var startAngle))
            return;

        if (!TryGetDouble(entity, 51, out var endAngle))
            return;

        // Converter ângulos DXF (graus, sentido anti-horário) para formato ElementArc
        // ElementArc usa caixa delimitadora + ângulo de varredura
        primitives.Add(new ElementArc
        {
            X = cx - radius,
            Y = cy - radius,
            Width = radius * 2,
            Height = radius * 2,
            Start = startAngle,
            Angle = endAngle - startAngle,
            Style = ParseStyle(entity)
        });
    }

    private static void ConvertLwPolyline(DxfEntity entity, List<ElementPrimitive> primitives)
    {
        var points = ExtractPolylinePoints(entity);
        if (points.Count < 2)
            return;

        bool closed = IsClosed(entity);

        // Converter segmentos em linhas
        for (int i = 0; i < points.Count - 1; i++)
        {
            primitives.Add(new ElementLine
            {
                X1 = points[i].X, Y1 = points[i].Y,
                X2 = points[i + 1].X, Y2 = points[i + 1].Y,
                Style = ParseStyle(entity)
            });
        }

        // Fechar polilinha
        if (closed && points.Count > 2)
        {
            primitives.Add(new ElementLine
            {
                X1 = points[^1].X, Y1 = points[^1].Y,
                X2 = points[0].X, Y2 = points[0].Y,
                Style = ParseStyle(entity)
            });
        }
    }

    private static void ConvertPolyline(DxfEntity entity, List<ElementPrimitive> primitives)
    {
        // POLYLINE é similar a LWPOLYLINE mas com vértices em sub-entidades VERTEX
        // Por simplicidade, tratar como polilinha aberta
        var points = ExtractPolylinePoints(entity);
        if (points.Count < 2)
            return;

        for (int i = 0; i < points.Count - 1; i++)
        {
            primitives.Add(new ElementLine
            {
                X1 = points[i].X, Y1 = points[i].Y,
                X2 = points[i + 1].X, Y2 = points[i + 1].Y,
                Style = ParseStyle(entity)
            });
        }
    }

    private static void ConvertText(DxfEntity entity, List<ElementPrimitive> primitives)
    {
        if (!TryGetPoint(entity, 10, out var x, out var y))
            return;

        var text = entity.Codes.TryGetValue(1, out var t) ? t : string.Empty;
        var height = 2.5;
        if (TryGetDouble(entity, 40, out var h))
            height = h;

        var rotation = 0.0;
        if (TryGetDouble(entity, 50, out var r))
            rotation = r;

        primitives.Add(new ElementText
        {
            Text = text,
            X = x, Y = y,
            Rotation = rotation,
            Font = $"Arial,{height.ToString(CultureInfo.InvariantCulture)},-1,5,50,0,0,0,0,0,Regular",
            Color = "#000000",
            Style = ParseStyle(entity)
        });
    }

    private static void ConvertEllipse(DxfEntity entity, List<ElementPrimitive> primitives)
    {
        if (!TryGetPoint(entity, 10, out var cx, out var cy))
            return;

        if (!TryGetDouble(entity, 40, out var ratio))
            return;

        // Major axis endpoint
        double majorX = 1, majorY = 0;
        if (TryGetPoint(entity, 11, out var mx, out var my))
        {
            majorX = mx;
            majorY = my;
        }

        var majorLen = Math.Sqrt(majorX * majorX + majorY * majorY);
        var minorLen = majorLen * ratio;

        primitives.Add(new ElementEllipse
        {
            X = cx - majorLen,
            Y = cy - minorLen,
            Width = majorLen * 2,
            Height = minorLen * 2,
            Style = ParseStyle(entity)
        });
    }

    private static List<Point2D> ExtractPolylinePoints(DxfEntity entity)
    {
        var points = new List<Point2D>();
        var xCoords = new List<double>();
        var yCoords = new List<double>();

        foreach (var kvp in entity.Codes)
        {
            if (double.TryParse(kvp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            {
                if (kvp.Key == 10) xCoords.Add(val);
                if (kvp.Key == 20) yCoords.Add(val);
            }
        }

        var count = Math.Min(xCoords.Count, yCoords.Count);
        for (int i = 0; i < count; i++)
            points.Add(new Point2D(xCoords[i], yCoords[i]));

        return points;
    }

    private static bool IsClosed(DxfEntity entity)
    {
        // Grupo 70: flag de polilinha, bit 1 = closed
        if (entity.Codes.TryGetValue(70, out var flagStr) &&
            int.TryParse(flagStr, out var flag))
        {
            return (flag & 1) == 1;
        }
        return false;
    }

    private static bool TryGetPoint(DxfEntity entity, int code, out double x, out double y)
    {
        x = y = 0;
        var yCode = code + 10; // Grupo Y = grupo X + 10
        if (entity.Codes.TryGetValue(code, out var xStr) &&
            entity.Codes.TryGetValue(yCode, out var yStr) &&
            double.TryParse(xStr, NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
            double.TryParse(yStr, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
        {
            return true;
        }
        return false;
    }

    private static bool TryGetDouble(DxfEntity entity, int code, out double value)
    {
        value = 0;
        if (entity.Codes.TryGetValue(code, out var str) &&
            double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        return false;
    }

    private static ElementStyle ParseStyle(DxfEntity entity)
    {
        // Converter cor DXF (grupo 62) para estilo
        var color = "black";
        if (entity.Codes.TryGetValue(62, out var colorStr) &&
            int.TryParse(colorStr, out var colorIndex))
        {
            color = DxfColorToHex(colorIndex);
        }

        var lineWeight = ElementLineWeight.Normal;
        if (entity.Codes.TryGetValue(370, out var lwStr) &&
            int.TryParse(lwStr, out var lw))
        {
            lineWeight = lw switch
            {
                < 15 => ElementLineWeight.Thin,
                < 25 => ElementLineWeight.Normal,
                < 40 => ElementLineWeight.Medium,
                _ => ElementLineWeight.Thick
            };
        }

        return new ElementStyle { Color = color, LineWeight = lineWeight };
    }

    private static string DxfColorToHex(int index)
    {
        // Cores padrão DXF (ACI - AutoCAD Color Index)
        return index switch
        {
            1 => "#FF0000",   // Vermelho
            2 => "#FFFF00",   // Amarelo
            3 => "#00FF00",   // Verde
            4 => "#00FFFF",   // Ciano
            5 => "#0000FF",   // Azul
            6 => "#FF00FF",   // Magenta
            7 => "#000000",   // Branco/preto
            8 => "#808080",   // Cinza escuro
            9 => "#C0C0C0",   // Cinza claro
            _ => "#000000"
        };
    }

    private static void UpdateBounds(ElementPrimitive primitive, ref double minX, ref double minY, ref double maxX, ref double maxY)
    {
        switch (primitive)
        {
            case ElementLine line:
                minX = Math.Min(minX, Math.Min(line.X1, line.X2));
                minY = Math.Min(minY, Math.Min(line.Y1, line.Y2));
                maxX = Math.Max(maxX, Math.Max(line.X1, line.X2));
                maxY = Math.Max(maxY, Math.Max(line.Y1, line.Y2));
                break;
            case ElementRect rect:
                minX = Math.Min(minX, rect.X);
                minY = Math.Min(minY, rect.Y);
                maxX = Math.Max(maxX, rect.X + rect.Width);
                maxY = Math.Max(maxY, rect.Y + rect.Height);
                break;
            case ElementEllipse ellipse:
                minX = Math.Min(minX, ellipse.X);
                minY = Math.Min(minY, ellipse.Y);
                maxX = Math.Max(maxX, ellipse.X + ellipse.Width);
                maxY = Math.Max(maxY, ellipse.Y + ellipse.Height);
                break;
            case ElementArc arc:
                minX = Math.Min(minX, arc.X);
                minY = Math.Min(minY, arc.Y);
                maxX = Math.Max(maxX, arc.X + arc.Width);
                maxY = Math.Max(maxY, arc.Y + arc.Height);
                break;
            case ElementPolygon polygon:
                foreach (var pt in polygon.Points)
                {
                    minX = Math.Min(minX, pt.X);
                    minY = Math.Min(minY, pt.Y);
                    maxX = Math.Max(maxX, pt.X);
                    maxY = Math.Max(maxY, pt.Y);
                }
                break;
            case ElementDynamicText dynText:
                minX = Math.Min(minX, dynText.X);
                minY = Math.Min(minY, dynText.Y);
                maxX = Math.Max(maxX, dynText.X + 50); // Estimativa
                maxY = Math.Max(maxY, dynText.Y + 10);
                break;
            case ElementText text:
                minX = Math.Min(minX, text.X);
                minY = Math.Min(minY, text.Y);
                maxX = Math.Max(maxX, text.X + 50); // Estimativa
                maxY = Math.Max(maxY, text.Y + 10);
                break;
        }
    }

    private static ElementPrimitive OffsetPrimitive(ElementPrimitive primitive, double dx, double dy)
    {
        return primitive switch
        {
            ElementLine line => line with { X1 = line.X1 + dx, Y1 = line.Y1 + dy, X2 = line.X2 + dx, Y2 = line.Y2 + dy },
            ElementRect rect => rect with { X = rect.X + dx, Y = rect.Y + dy },
            ElementEllipse ellipse => ellipse with { X = ellipse.X + dx, Y = ellipse.Y + dy },
            ElementArc arc => arc with { X = arc.X + dx, Y = arc.Y + dy },
            ElementPolygon polygon => polygon with { Points = polygon.Points.Select(p => new Point2D(p.X + dx, p.Y + dy)).ToList() },
            ElementDynamicText dynText => dynText with { X = dynText.X + dx, Y = dynText.Y + dy },
            ElementText text => text with { X = text.X + dx, Y = text.Y + dy },
            _ => primitive
        };
    }
}
