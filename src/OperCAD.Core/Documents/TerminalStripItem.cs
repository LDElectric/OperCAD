using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Item de borne colocado na folha (port do item de borne do padrão CAD): exibe uma
/// tabela com os níveis do terminal strip, seus números e pontes.
/// </summary>
public sealed class TerminalStripItem : DocumentObject
{
    /// <summary>Uuid do borne (TerminalStrip) exibido por este item.</summary>
    public Guid StripUuid { get; set; }

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Largura da tabela em mm.</summary>
    public double Width { get; set; } = 44;

    /// <summary>Altura de cada linha (nível) em mm.</summary>
    public double RowHeight { get; set; } = 6;

    public TerminalStripItem()
    {
        Name = "Borne";
    }

    /// <summary>Retângulo ocupado (mundo), dado o número de níveis do borne.</summary>
    public Rect2D Bounds(int levelCount)
        => new(X, Y, Width, RowHeight * (Math.Max(levelCount, 0) + 1));
}
