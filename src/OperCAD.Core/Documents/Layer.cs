namespace OperCAD.Core.Documents;

/// <summary>
/// Camada de desenho (layer). Agrupa objetos da folha para controle de visibilidade
/// e bloqueio. Inspirado em layers de PDF (Optional Content Groups) e CAD clássico.
/// </summary>
public sealed class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Nome da camada (ex.: "Moldura", "Simbolos", "Texto").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Visibilidade: quando false, os objetos desta camada não são renderizados.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Bloqueio: quando true, os objetos desta camada não podem ser selecionados/movidos.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Cor de destaque da camada (hex, para UI).</summary>
    public string Color { get; set; } = "#808080";

    /// <summary>Ordem de renderização (menor = mais atrás).</summary>
    public int ZOrder { get; set; }

    public override string ToString() => Name;
}
