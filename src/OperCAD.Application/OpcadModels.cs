using System.Text.Json.Serialization;

namespace OperCAD.Application;

/// <summary>
/// Modelos de desserialização para arquivos .opcad (formato JSON do modelo OPCAD).
/// Cada arquivo .opcad descreve um elemento da biblioteca com primitivas gráficas,
/// terminais e textos dinâmicos.
/// </summary>
internal sealed class OpcadElement
{
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string Id { get; set; } = "";
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> KindInformations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ElementInformations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Informations { get; set; } = "";
    public bool IsConductor { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double HotspotX { get; set; }
    public double HotspotY { get; set; }
    public string LinkType { get; set; } = "simple";
    public List<OpcadPrimitive> Primitives { get; set; } = new();
    public List<OpcadTerminal> Terminals { get; set; } = new();
    public List<OpcadDynamicText> DynamicTexts { get; set; } = new();
}

// ── Primitivas ─────────────────────────────────────────────────────────────

internal class OpcadPrimitive
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("$type")]
    public string DollarType { get; set; } = "";

    public OpcadStyle Style { get; set; } = new();
    public bool Antialias { get; set; }

    /// <summary>Tipo efetivo: usa $type (prioridade) ou type.</summary>
    [JsonIgnore]
    public string EffectiveType => string.IsNullOrEmpty(DollarType) ? Type : DollarType;
}

internal sealed class OpcadLinePrimitive : OpcadPrimitive
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string End1 { get; set; } = "none";
    public string End2 { get; set; } = "none";
    public double Length1 { get; set; } = 1.5;
    public double Length2 { get; set; } = 1.5;
}

internal sealed class OpcadRectPrimitive : OpcadPrimitive
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Rx { get; set; }
    public double Ry { get; set; }
}

internal sealed class OpcadEllipsePrimitive : OpcadPrimitive
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

internal sealed class OpcadCirclePrimitive : OpcadPrimitive
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Diameter { get; set; }
}

internal sealed class OpcadArcPrimitive : OpcadPrimitive
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Start { get; set; }
    public double Angle { get; set; }
}

internal sealed class OpcadPolygonPrimitive : OpcadPrimitive
{
    public List<OpcadPoint> Points { get; set; } = new();
    public bool Closed { get; set; }
}

internal sealed class OpcadPolylinePrimitive : OpcadPrimitive
{
    // Pontos vêm como [[x, y], ...] (arrays de pares), conforme salvo pelo editor.
    public List<List<double>> Points { get; set; } = new();
}

internal sealed class OpcadTextPrimitive : OpcadPrimitive
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public string Font { get; set; } = "Liberation Sans,5,-1,5,50,0,0,0,0,0,Regular";
    public double Size { get; set; }
    public string Color { get; set; } = "#000000";
    public string Halignment { get; set; } = "AlignLeft";
    public string Valignment { get; set; } = "AlignTop";
}

// ── Modelos auxiliares ─────────────────────────────────────────────────────

internal sealed class OpcadStyle
{
    public string LineStyle { get; set; } = "normal";
    public string LineWeight { get; set; } = "normal";
    public string Filling { get; set; } = "none";
    public string Color { get; set; } = "black";
}

internal sealed class OpcadPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

internal sealed class OpcadTerminal
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public string Orientation { get; set; } = "e";
    public string Type { get; set; } = "Generic";
}

internal sealed class OpcadDynamicText
{
    public double X { get; set; }
    public double Y { get; set; }
    public int Z { get; set; }
    public double TextWidth { get; set; } = -1;
    public string Halignment { get; set; } = "AlignLeft";
    public string Valignment { get; set; } = "AlignTop";
    public bool Frame { get; set; }
    public double Rotation { get; set; }
    public bool KeepVisualRotation { get; set; }
    public string TextFrom { get; set; } = "UserText";
    public string Uuid { get; set; } = "";
    public string Font { get; set; } = "Liberation Sans,5,-1,5,50,0,0,0,0,0,Regular";
    public string Text { get; set; } = "";
    public string InfoName { get; set; } = "";
}
