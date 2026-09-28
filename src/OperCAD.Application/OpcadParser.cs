using System.Globalization;
using System.Text.Json;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>
/// Lê um arquivo .opcad (formato JSON do modelo OPCAD) e produz um
/// <see cref="ElementDefinition"/>. Tolerante a arquivos malformados (retorna null).
/// </summary>
public static class OpcadParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new OpcadPrimitiveConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ElementDefinition? Parse(string filePath, string category)
    {
        OpcadElement? opcad;
        try
        {
            var json = File.ReadAllText(filePath);
            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json.Substring(1); // remove BOM UTF-8, se presente (defensivo)

            opcad = JsonSerializer.Deserialize<OpcadElement>(json, JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (opcad is null)
            return null;

        var id = Guid.TryParse(opcad.Id, out var g) ? g : Guid.NewGuid();
        var names = opcad.Names ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var primitives = ConvertPrimitives(opcad.Primitives);
        var terminals = ConvertTerminals(opcad.Terminals);
        var dynamicTexts = ConvertDynamicTexts(opcad.DynamicTexts);

        // Adiciona textos dinâmicos também como primitivas (para renderização)
        foreach (var dt in dynamicTexts)
            primitives.Add(dt);

        var definition = new ElementDefinition
        {
            Id = id,
            SourcePath = filePath,
            SourceFile = opcad.SourceFile ?? string.Empty,
            Category = category,
            LinkType = opcad.LinkType ?? "simple",
            Width = opcad.Width,
            Height = opcad.Height,
            HotspotX = opcad.HotspotX,
            HotspotY = -opcad.HotspotY,
            // Biblioteca nativa .opcad: 1 unidade = 1 mm (escala 1.0).
            Scale = 1.0,
            Information = opcad.Informations ?? string.Empty,
            Names = names,
            Name = PickBestTranslatedName(names),
            IsConductor = opcad.IsConductor,
            Primitives = primitives,
            Terminals = terminals
        };

        foreach (var dt in dynamicTexts)
        {
            definition.DynamicTextFields.Add(new DynamicTextField
            {
                Name = dt.InfoName,
                Value = dt.Text,
                X = dt.X,
                Y = dt.Y,
                FontFamily = ParseFontFamily(dt.Font),
                FontSize = ParseFontSize(dt.Font)
            });
        }

        return definition;
    }

    // ── Conversão de primitivas ────────────────────────────────────────────

    private static List<ElementPrimitive> ConvertPrimitives(List<OpcadPrimitive> primitives)
    {
        var result = new List<ElementPrimitive>();
        foreach (var p in primitives)
        {
            if (p is null) continue;
            var converted = p switch
            {
                OpcadLinePrimitive line => ConvertLine(line),
                OpcadRectPrimitive rect => ConvertRect(rect),
                OpcadEllipsePrimitive ell => ConvertEllipse(ell),
                OpcadCirclePrimitive cir => ConvertCircle(cir),
                OpcadArcPrimitive arc => ConvertArc(arc),
                OpcadPolygonPrimitive poly => ConvertPolygon(poly),
                OpcadPolylinePrimitive polyline => ConvertPolyline(polyline),
                OpcadTextPrimitive txt => ConvertText(txt),
                _ => TryConvertByType(p)
            };
            if (converted is not null)
                result.Add(converted);
        }
        return result;
    }

    /// <summary>
    /// Fallback: quando System.Text.Json desserializa como OpcadPrimitive genérico,
    /// tenta converter pelo campo EffectiveType lendo propriedades dinamicamente.
    /// </summary>
    private static ElementPrimitive? TryConvertByType(OpcadPrimitive p)
    {
        // Se o tipo não é "line", "rect", etc., ignora
        return p.EffectiveType switch
        {
            "line" => ConvertLine(As<OpcadLinePrimitive>(p)),
            "rect" => ConvertRect(As<OpcadRectPrimitive>(p)),
            "ellipse" => ConvertEllipse(As<OpcadEllipsePrimitive>(p)),
            "circle" => ConvertCircle(As<OpcadCirclePrimitive>(p)),
            "arc" => ConvertArc(As<OpcadArcPrimitive>(p)),
            "polygon" => ConvertPolygon(As<OpcadPolygonPrimitive>(p)),
            "polyline" => ConvertPolyline(As<OpcadPolylinePrimitive>(p)),
            "text" => ConvertText(As<OpcadTextPrimitive>(p)),
            _ => null
        };
    }

    /// <summary>Cria uma instância do subtipo especificado com estilo herdado.</summary>
    private static T As<T>(OpcadPrimitive source) where T : OpcadPrimitive, new()
    {
        return new T
        {
            Style = source.Style,
            Antialias = source.Antialias
        };
    }

    private static ElementLine ConvertLine(OpcadLinePrimitive p) => new()
    {
        X1 = p.X1, Y1 = -p.Y1, X2 = p.X2, Y2 = -p.Y2,
        End1 = p.End1, End2 = p.End2,
        Length1 = p.Length1, Length2 = p.Length2,
        Style = ConvertStyle(p.Style)
    };

    private static ElementRect ConvertRect(OpcadRectPrimitive p) => new()
    {
        X = p.X, Y = -(p.Y + p.Height), Width = p.Width, Height = p.Height,
        Rx = p.Rx, Ry = p.Ry,
        Style = ConvertStyle(p.Style)
    };

    private static ElementEllipse ConvertEllipse(OpcadEllipsePrimitive p) => new()
    {
        X = p.X, Y = -(p.Y + p.Height), Width = p.Width, Height = p.Height,
        Style = ConvertStyle(p.Style)
    };

    private static ElementEllipse ConvertCircle(OpcadCirclePrimitive p) => new()
    {
        X = p.X - p.Diameter / 2, Y = -(p.Y + p.Diameter / 2),
        Width = p.Diameter, Height = p.Diameter,
        Style = ConvertStyle(p.Style)
    };

    private static ElementArc ConvertArc(OpcadArcPrimitive p) => new()
    {
        X = p.X, Y = -(p.Y + p.Height), Width = p.Width, Height = p.Height,
        Start = -p.Start, Angle = -p.Angle,
        Style = ConvertStyle(p.Style)
    };

    private static ElementPolygon ConvertPolygon(OpcadPolygonPrimitive p) => new()
    {
        Points = p.Points.Select(pt => new Point2D(pt.X, -pt.Y)).ToList(),
        Closed = p.Closed,
        Style = ConvertStyle(p.Style)
    };

    private static ElementPolyline ConvertPolyline(OpcadPolylinePrimitive p) => new()
    {
        // Pontos vêm como [[x, y], ...]; inverte Y (referencial do editor → mundo).
        Points = p.Points
            .Where(pts => pts.Count >= 2)
            .Select(pts => new Point2D(pts[0], -pts[1]))
            .ToList(),
        Style = ConvertStyle(p.Style)
    };

    private static ElementText ConvertText(OpcadTextPrimitive p) => new()
    {
        Text = p.Text,
        X = p.X, Y = -p.Y,
        Rotation = -p.Rotation,
        Font = p.Font,
        Color = p.Color,
        Halign = p.Halignment switch
        {
            "AlignRight" => ElementTextHAlign.Right,
            "AlignCenter" => ElementTextHAlign.Center,
            _ => ElementTextHAlign.Left
        },
        Valign = p.Valignment switch
        {
            "AlignBottom" => ElementTextVAlign.Bottom,
            "AlignCenter" => ElementTextVAlign.Center,
            _ => ElementTextVAlign.Top
        },
        Style = ConvertStyle(p.Style)
    };

    // ── Terminais ──────────────────────────────────────────────────────────

    private static List<ElementTerminal> ConvertTerminals(List<OpcadTerminal> terminals)
    {
        return terminals.Select(t => new ElementTerminal
        {
            Uuid = Guid.TryParse(t.Uuid, out var g) ? g : Guid.NewGuid(),
            Name = t.Name,
            X = t.X,
            Y = -t.Y,
            Orientation = t.Orientation.Length > 0 ? t.Orientation[0] : 'e',
            Type = t.Type
        }).ToList();
    }

    // ── Textos dinâmicos ──────────────────────────────────────────────────

    private static List<ElementDynamicText> ConvertDynamicTexts(List<OpcadDynamicText> texts)
    {
        return texts.Select(t => new ElementDynamicText
        {
            Text = t.Text,
            X = t.X,
            Y = -t.Y,
            Z = t.Z,
            Rotation = -t.Rotation,
            Font = t.Font,
            Color = "#000000",
            TextWidth = t.TextWidth,
            Frame = t.Frame,
            TextFrom = t.TextFrom,
            KeepVisualRotation = t.KeepVisualRotation,
            InfoName = t.InfoName,
            Halign = t.Halignment switch
            {
                "AlignRight" => ElementTextHAlign.Right,
                "AlignCenter" => ElementTextHAlign.Center,
                _ => ElementTextHAlign.Left
            },
            Valign = t.Valignment switch
            {
                "AlignBottom" => ElementTextVAlign.Bottom,
                "AlignCenter" => ElementTextVAlign.Center,
                _ => ElementTextVAlign.Top
            }
        }).ToList();
    }

    // ── Helpers de nome ──────────────────────────────────────────────────

    /// <summary>
    /// Seleciona o melhor nome do dicionário multilíngue: pt → pt_BR → en (traduzido)
    /// → es (traduzido) → fr (traduzido). Quando o idioma preferido não existe,
    /// traduz o fallback pelo glossário técnico pt-BR.
    /// </summary>
    private static string PickBestTranslatedName(Dictionary<string, string> names)
    {
        if (names.Count == 0)
            return string.Empty;

        // 1) Tenta PT direto (já traduzido)
        foreach (var lang in new[] { "pt", "pt_BR" })
            if (names.TryGetValue(lang, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;

        // 2) Tenta outros idiomas traduzindo pelo glossário
        foreach (var lang in new[] { "en", "es", "fr", "de", "it" })
            if (names.TryGetValue(lang, out var n) && !string.IsNullOrWhiteSpace(n))
                return ElementNameTranslator.Translate(n);

        // 3) Primeiro valor não-vazio, traduzido
        var fallback = names.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return fallback is not null ? ElementNameTranslator.Translate(fallback) : string.Empty;
    }

    // ── Helpers de fonte ──────────────────────────────────────────────────

    /// <summary>
    /// Extrai o nome da família da fonte do formato Qt.
    /// Ex.: "Liberation Sans,5,-1,5,50,0,0,0,0,0,Regular" → "Liberation Sans"
    /// </summary>
    private static string ParseFontFamily(string font)
    {
        if (string.IsNullOrEmpty(font))
            return "Arial";
        var comma = font.IndexOf(',');
        return comma > 0 ? font[..comma].Trim() : font.Trim();
    }

    /// <summary>
    /// Extrai o tamanho da fonte do formato Qt.
    /// Ex.: "Liberation Sans,9,-1,5,50,0,0,0,0,0,Regular" → 9
    /// </summary>
    private static double ParseFontSize(string font)
    {
        if (string.IsNullOrEmpty(font))
            return 3.5;
        var parts = font.Split(',');
        if (parts.Length >= 2 && double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var size))
            return size;
        return 3.5;
    }

    // ── Estilo ─────────────────────────────────────────────────────────────

    private static ElementStyle ConvertStyle(OpcadStyle? s)
    {
        if (s is null)
            return new ElementStyle();
        return new ElementStyle
        {
            Color = s.Color ?? "black",
            Filling = s.Filling ?? "none",
            LineWeight = s.LineWeight?.ToLowerInvariant() switch
            {
                "thin" => ElementLineWeight.Thin,
                "medium" => ElementLineWeight.Medium,
                "thick" or "hight" or "eleve" => ElementLineWeight.Thick,
                _ => ElementLineWeight.Normal
            },
            DashStyle = s.LineStyle?.ToLowerInvariant() switch
            {
                "dashed" or "dash" or "tracejado" => LineDashStyle.Dashed,
                "dotted" or "dot" or "pontilhado" => LineDashStyle.Dotted,
                "dashdot" or "dash_dot" or "traceponto" => LineDashStyle.DashDot,
                _ => LineDashStyle.Solid
            }
        };
    }
}
