using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Cálculo puro (sem dependência de renderização/Skia) da geometria de uma moldura de
/// folha dirigida por dados (<see cref="FolhaTemplate"/>). Usado tanto pelo renderer quanto
/// pelo importador (ex.: para escalar o conteúdo até a área útil da folha ABNT).
/// </summary>
public static class FolhaFrameGeometry
{
    /// <summary>Retângulo da moldura (área interna = página menos o recuo).</summary>
    public static Rect2D FrameRect(PageSettings settings)
    {
        var inset = Math.Clamp(settings.FolhaFrameInset, 0, 30);
        var w = settings.Width;
        var h = settings.Height;
        return new Rect2D(inset, inset, w - inset * 2, h - inset * 2);
    }

    /// <summary>
    /// Área de desenho efetiva: desconta as faixas de colunas (topo) e linhas (laterais)
    /// da moldura e a área do bloco de legenda. O conteúdo deve caber nesse retângulo.
    /// </summary>
    public static Rect2D DrawingArea(FolhaTemplate template, PageSettings settings)
    {
        var frame = FrameRect(settings);
        var fw = frame.Width;
        var fh = frame.Height;

        var stripH = Math.Max(0.004, template.ColumnStripHeight) * fh;
        var leftOffset = LeftStripRight(template, fw);
        var rightOffset = template.ShowRightStrip ? Math.Max(0.004, template.RowStripWidth) * fw : 0;
        var titleBlockH = (template.TitleBlockBottom - template.TitleBlockTop) * fh;

        var areaLeft = frame.MinX + leftOffset;
        var areaBottom = frame.MinY + titleBlockH;
        var areaWidth = fw - leftOffset - rightOffset;
        var areaHeight = fh - stripH - titleBlockH;

        return new Rect2D(areaLeft, areaBottom, areaWidth, areaHeight);
    }

    /// <summary>
    /// Borda direita (deslocamento a partir de <c>frame.MinX</c>) da faixa de letras da
    /// ESQUERDA. Quando há margem de encadernação, a faixa de letras fica na última
    /// coluna dela (junto à área de desenho); senão, usa a largura padrão da faixa.
    /// </summary>
    public static double LeftStripRight(FolhaTemplate template, double fw)
    {
        var stripW = Math.Max(0.004, template.RowStripWidth) * fw;
        return template.BindingLines.Count >= 2
            ? template.BindingLines[template.BindingLines.Count - 1] * fw
            : stripW;
    }
}
