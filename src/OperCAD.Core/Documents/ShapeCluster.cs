using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Cluster espacial de formas livres agrupadas por proximidade.
/// Cada cluster representa um possível símbolo/elemento do desenho importado.
/// </summary>
public sealed class ShapeCluster
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Formas geométricas que compõem o cluster.</summary>
    public List<ShapeItem> Shapes { get; init; } = new();

    /// <summary>Textos associados ao cluster (dentro do bounding box).</summary>
    public List<TextItem> Texts { get; init; } = new();

    /// <summary>Retângulo envolvente de todas as formas do cluster.</summary>
    public Rect2D Bounds { get; init; }

    /// <summary>Centro geométrico do cluster.</summary>
    public Point2D Centroid => new(Bounds.CenterX, Bounds.CenterY);

    /// <summary>Rótulo reconhecido do elemento (ex.: "Motor", "Contator"). Null se não reconhecido.</summary>
    public string? RecognizedLabel { get; set; }

    /// <summary>Score de similaridade do reconhecimento (0..1). Null se não reconhecido.</summary>
    public double? RecognitionScore { get; set; }

    /// <summary>ElementDefinition da biblioteca correspondente ao cluster reconhecido.</summary>
    public Elements.ElementDefinition? MatchedDefinition { get; set; }

    /// <summary>Se true, o cluster foi aceito pelo usuário como elemento.</summary>
    public bool IsAccepted { get; set; }

    /// <summary>Se true, o cluster foi marcado como "não é elemento" pelo usuário.</summary>
    public bool IsRejected { get; set; }
}
