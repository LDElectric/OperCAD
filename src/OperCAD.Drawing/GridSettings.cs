namespace OperCAD.Drawing;

/// <summary>Estilo de renderização do grid.</summary>
public enum GridVisualStyle { Lines, Dots }

/// <summary>
/// Configuração do grid de desenho com suporte a múltiplos níveis (fino/médio/grosso).
/// O grid fino (2.5mm) é a resolução base para snap de elementos .opcad.
/// Os níveis médio e grosso são apenas visuais.
/// </summary>
public sealed class GridSettings
{
    public bool IsVisible { get; set; } = true;

    /// <summary>Espaçamento do grid fino em mm (padrão 2.5mm, resolução base de snap).</summary>
    public double FineSpacing { get; set; } = 2.5;

    /// <summary>Espaçamento do grid médio em mm (padrão 10mm).</summary>
    public double MediumSpacing { get; set; } = 10.0;

    /// <summary>Espaçamento do grid grosso em mm (padrão 50mm).</summary>
    public double CoarseSpacing { get; set; } = 50.0;

    /// <summary>Espaçamento de snap em mm (padrão 2.5mm = resolução do grid fino).</summary>
    public double SnapSpacing { get; set; } = 2.5;

    /// <summary>Estilo visual do grid.</summary>
    public GridVisualStyle Style { get; set; } = GridVisualStyle.Dots;

    /// <summary>Espaçamento legado (compatibilidade com código existente).</summary>
    public double Spacing
    {
        get => FineSpacing;
        set
        {
            FineSpacing = value;
            MediumSpacing = value * 4;
            CoarseSpacing = value * 20;
            SnapSpacing = value;
        }
    }
}
