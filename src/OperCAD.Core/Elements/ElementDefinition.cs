using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Elements;

/// <summary>Peso (espessura) de traço de uma primitiva de desenho.</summary>
public enum ElementLineWeight { Thin, Normal, Medium, Thick }

/// <summary>Alinhamento horizontal de um texto de elemento.</summary>
public enum ElementTextHAlign { Left, Right, Center }

/// <summary>Alinhamento vertical de um texto de elemento.</summary>
public enum ElementTextVAlign { Top, Bottom, Center }

/// <summary>Estilo de traço/preenchimento de uma primitiva (estilo padrão CAD: color, filling, line-weight).</summary>
public sealed record ElementStyle
{
    public string Color { get; init; } = "black";
    public string Filling { get; init; } = "none";
    public ElementLineWeight LineWeight { get; init; } = ElementLineWeight.Normal;
    public LineDashStyle DashStyle { get; init; } = LineDashStyle.Solid;
}

/// <summary>Base para todas as primitivas de desenho de um elemento.</summary>
public abstract record ElementPrimitive
{
    public ElementStyle Style { get; init; } = new();

    /// <summary>Cópia profunda (cada subtipo clona seus próprios dados).</summary>
    public abstract ElementPrimitive DeepCopy();
}

/// <summary>Segmento de reta.</summary>
public sealed record ElementLine : ElementPrimitive
{
    public double X1 { get; init; }
    public double Y1 { get; init; }
    public double X2 { get; init; }
    public double Y2 { get; init; }
    public string End1 { get; init; } = "none";
    public string End2 { get; init; } = "none";
    public double Length1 { get; init; } = 1.5;
    public double Length2 { get; init; } = 1.5;

    public override ElementPrimitive DeepCopy() => this with { Style = Style };
}

/// <summary>Retângulo (com raios de canto opcionais).</summary>
public sealed record ElementRect : ElementPrimitive
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double Rx { get; init; }
    public double Ry { get; init; }

    public override ElementPrimitive DeepCopy() => this with { Style = Style };
}

/// <summary>Elipse (caixa delimitadora).</summary>
public sealed record ElementEllipse : ElementPrimitive
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public override ElementPrimitive DeepCopy() => this with { Style = Style };
}

/// <summary>Arco (caixa delimitadora + ângulos em graus, convenção padrão CAD/Qt).</summary>
public sealed record ElementArc : ElementPrimitive
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double Start { get; init; }
    public double Angle { get; init; }

    public override ElementPrimitive DeepCopy() => this with { Style = Style };
}

/// <summary>Polígono (aberto ou fechado).</summary>
public sealed record ElementPolygon : ElementPrimitive
{
    public List<Point2D> Points { get; init; } = new();
    public bool Closed { get; init; }

    public override ElementPrimitive DeepCopy() => new ElementPolygon
    {
        Style = Style, Points = new List<Point2D>(Points), Closed = Closed
    };
}

/// <summary>Polilinha (sequência de pontos conectados por segmentos, sem fechamento).</summary>
public sealed record ElementPolyline : ElementPrimitive
{
    public List<Point2D> Points { get; init; } = new();

    public override ElementPrimitive DeepCopy() => new ElementPolyline
    {
        Style = Style, Points = new List<Point2D>(Points)
    };
}

/// <summary>Texto fixo.</summary>
public record ElementText : ElementPrimitive
{
    public string Text { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public double Rotation { get; init; }
    public string Font { get; init; } = "Liberation Sans,5,-1,5,50,0,0,0,0,0,Regular";
    public string Color { get; init; } = "#000000";
    public ElementTextHAlign Halign { get; init; } = ElementTextHAlign.Left;
    public ElementTextVAlign Valign { get; init; } = ElementTextVAlign.Top;

    public override ElementPrimitive DeepCopy() => this with { Style = Style };
}

/// <summary>Texto dinâmico (etiqueta do elemento, texto editável pelo usuário...).</summary>
public sealed record ElementDynamicText : ElementText
{
    public int Z { get; init; }
    public bool Frame { get; init; }
    public string TextFrom { get; init; } = "UserText";
    public double TextWidth { get; init; } = -1;
    public bool KeepVisualRotation { get; init; } = true;

    /// <summary>Nome da informação exibida (ex.: "label", "comment") quando TextFrom é ElementInfo.</summary>
    public string InfoName { get; init; } = string.Empty;

    public override ElementPrimitive DeepCopy() => this with { Style = Style };
}

/// <summary>Terminal (ponto de conexão) de um elemento.</summary>
public sealed class ElementTerminal
{
    public Guid Uuid { get; init; }
    public string Name { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public char Orientation { get; init; } = 'e';
    public string Type { get; init; } = "Generic";

    /// <summary>Cópia profunda (novo Uuid para não conflitar no editor).</summary>
    public ElementTerminal DeepCopy() => new()
    {
        Uuid = Guid.NewGuid(),
        Name = Name,
        X = X,
        Y = Y,
        Orientation = Orientation,
        Type = Type
    };
}

/// <summary>Definição de um elemento da biblioteca nativa do OperCAD.</summary>
public sealed record ElementDefinition
{
    /// <summary>
    /// AJUSTE GLOBAL DE TAMANHO DOS ELEMENTOS. A biblioteca .opcad já é nativa em mm
    /// (1 unidade = 1 mm) no tamanho final, então NÃO há sobrescalonamento aqui.
    /// Manter em 1.0 preserva as posições nativas dos terminais (múltiplas de 2,5 mm),
    /// garantindo que os pontos de conexão caiam exatamente sobre o grid de snap.
    /// Centralizado aqui (Core) para que o render (Drawing) e a geometria dos terminais
    /// (Core/Documents/TerminalGeometry) usem a MESMA escala e condutores/terminais
    /// permaneçam alinhados. Mutável para testes/ajuste fino.
    /// </summary>
    public static double GlobalScale { get; set; } = 1.0;

    /// <summary>
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, string> Names { get; init; } = new();

    /// <summary>Nomes do elemento em diferentes idiomas.</summary>
    public Dictionary<string, string> LocalizedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Information { get; init; } = string.Empty;
    public double Width { get; init; }
    public double Height { get; init; }
    public double HotspotX { get; init; }
    public double HotspotY { get; init; }

    /// <summary>
    /// Fator de escala do elemento para milímetros do mundo da folha. Os elementos da
    /// biblioteca nativa usam milímetros e escala 1.0; o fator também admite ajustes por elemento.
    /// Aplicada de forma consistente no render e na geometria dos terminais.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    public string LinkType { get; init; } = "simple";
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Nome do arquivo-fonte ORIGINAL (campo "sourceFile" do .opcad). É a identidade
    /// estável do elemento: permanece o mesmo quando o arquivo é renomeado em dev,
    /// permitindo que buscas por elemento sejam robustas a renomeações.
    /// </summary>
    public string SourceFile { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;
    public List<ElementPrimitive> Primitives { get; init; } = new();
    public List<ElementTerminal> Terminals { get; init; } = new();

    /// <summary>
    /// Quando verdadeiro, este elemento se comporta como um condutor de rota livre.
    /// Ao ser arrastado da barra de elementos, ativa o modo de desenho de condutor
    /// em vez de inserir um PlacedElement estático. O elemento mantém sua aparência
    /// visual mas gera um objeto Conductor com routing ortogonal, jumps e T-connections.
    /// </summary>
    public bool IsConductor { get; init; }

    /// <summary>Campos de texto dinâmico que podem ser editados pelo usuário.</summary>
    public List<DynamicTextField> DynamicTextFields { get; } = new();

    public bool HasNameFor(string lang) => Names.ContainsKey(lang);

    /// <summary>Obtém o nome no idioma especificado, ou o nome padrão como fallback.</summary>
    public string GetLocalizedName(string language = "pt_BR") =>
        LocalizedNames.TryGetValue(language, out var name) ? name : Name;

    /// <summary>Seleciona o melhor nome: pt → en → es → fr → o primeiro disponível.</summary>
    public static string PickBestName(Dictionary<string, string> names)
    {
        if (names.Count == 0)
            return string.Empty;
        foreach (var lang in new[] { "pt", "pt_BR", "en", "es", "fr", "de", "it" })
            if (names.TryGetValue(lang, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;
        return names.Values.First(v => !string.IsNullOrWhiteSpace(v));
    }

    /// <summary>Cópia profunda (primitivas e terminais clonados) para o undo/redo do editor.</summary>
    public ElementDefinition DeepCopy()
    {
        var copy = new ElementDefinition
        {
            Id = Id,
            Name = Name,
            Names = new Dictionary<string, string>(Names),
            Information = Information,
            Width = Width,
            Height = Height,
            HotspotX = HotspotX,
            HotspotY = HotspotY,
            Scale = Scale,
            LinkType = LinkType,
            SourcePath = SourcePath,
            Category = Category,
            IsConductor = IsConductor,
            Primitives = Primitives.Select(p => p.DeepCopy()).ToList(),
            Terminals = Terminals.Select(t => t.DeepCopy()).ToList()
        };
        foreach (var kvp in LocalizedNames)
            copy.LocalizedNames[kvp.Key] = kvp.Value;
        foreach (var field in DynamicTextFields)
            copy.DynamicTextFields.Add(field.DeepCopy());
        return copy;
    }

    /// <summary>
    /// Indica se o elemento exibe uma etiqueta (texto dinâmico com informações
    /// do elemento, ex.: "label") — usado para a numeração automática como no padrão CAD.
    /// </summary>
    public bool HasLabelField =>
        Primitives.OfType<ElementDynamicText>()
            .Any(d => d.TextFrom == "ElementInfo" && (d.InfoName is "label" or ""));
}
