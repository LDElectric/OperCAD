using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Drawing;

/// <summary>
/// Limita o posicionamento do HOTSPOT de objetos à ÁREA DE DESENHO da folha — o espaço
/// INTERNO da moldura, descontadas as faixas de letras/números e a legenda. Apenas o
/// hotspot (âncora) é limitado, não o corpo do elemento. O corpo pode se estender além
/// das bordas da moldura.
/// </summary>
public static class PageClamping
{
    /// <summary>
    /// Área de desenho efetiva da folha (espaço interno da moldura). Quando a folha usa um
    /// template de moldura, usa a área do template; senão usa as margens clássicas.
    /// </summary>
    public static Rect2D DrawingArea(PageSettings settings)
        => settings.FolhaTemplate is { } tpl
            ? FolhaTemplateRenderer.DrawingArea(tpl, settings)
            : settings.DrawingBounds;

    /// <summary>Limita um ponto (ancoragem/hotspot) à área de desenho da folha.</summary>
    public static Point2D ClampPoint(Rect2D area, Point2D p)
        => new(Math.Clamp(p.X, area.MinX, area.MaxX), Math.Clamp(p.Y, area.MinY, area.MaxY));

    /// <summary>
    /// Limita o HOTSPOT de um elemento à área de desenho (o corpo pode se estender além
    /// da área — apenas o hotspot é restrito).
    /// </summary>
    public static (double X, double Y) ClampHotspot(Rect2D area, double x, double y)
        => (Math.Clamp(x, area.MinX, area.MaxX), Math.Clamp(y, area.MinY, area.MaxY));

    /// <summary>
    /// Escala do modelo para o mundo usada por <see cref="ElementRenderer.ComputeBounds"/> e
    /// por <see cref="TerminalGeometry"/> (devem ser sempre iguais).
    /// </summary>
    private static double ModelScale(ElementDefinition def)
        => ElementRenderer.ElementScale(def);

    /// <summary>
    /// [DEPRECATED] Ajusta a posição (hotspot) para que o corpo INTEIRO fique dentro da
    /// área de desenho. Mantido para referência — o novo comportamento usa ClampHotspot.
    /// </summary>
    public static (double X, double Y) ClampElement(ElementDefinition def, Rect2D area, double x, double y)
    {
        var scale = ModelScale(def);
        var b = ElementRenderer.ComputeBounds(def);
        double minX = x + b.MinX - def.HotspotX * scale;
        double maxX = x + b.MaxX - def.HotspotX * scale;
        double minY = y + b.MinY - def.HotspotY * scale;
        double maxY = y + b.MaxY - def.HotspotY * scale;
        if (minX < area.MinX) x += area.MinX - minX;
        if (maxX > area.MaxX) x -= maxX - area.MaxX;
        if (minY < area.MinY) y += area.MinY - minY;
        if (maxY > area.MaxY) y -= maxY - area.MaxY;
        return (x, y);
    }

    /// <summary>
    /// Deslocamento (dx, dy) necessário para que um elemento JÁ colocado (considerando a
    /// rotação) fique inteiramente dentro da área de desenho. Aplicar com
    /// <c>el.X += dx; el.Y += dy;</c>.
    /// </summary>
    public static (double Dx, double Dy) ClampPlaced(PlacedElement el, Rect2D area)
    {
        var b = ElementWorldBounds(el);
        double dx = 0, dy = 0;
        if (b.MinX < area.MinX) dx += area.MinX - b.MinX;
        if (b.MaxX > area.MaxX) dx -= b.MaxX - area.MaxX;
        if (b.MinY < area.MinY) dy += area.MinY - b.MinY;
        if (b.MaxY > area.MaxY) dy -= b.MaxY - area.MaxY;
        return (dx, dy);
    }

    /// <summary>
    /// Caixa (AABB) do elemento no MUNDO, considerando a rotação.
    /// <see cref="ElementRenderer.ComputeBounds"/> devolve a caixa do modelo já ESCALADA
    /// (× <see cref="ModelScale"/>); <see cref="TerminalGeometry.LocalToWorld"/> espera
    /// coordenadas do modelo CRUAS — por isso converte de volta antes de transformar.
    /// </summary>
    public static Rect2D ElementWorldBounds(PlacedElement el)
    {
        var scale = ElementRenderer.ElementScale(el);
        var b = ElementRenderer.ComputeBounds(el, el.Label);
        var corners = new[]
        {
            new Point2D(b.MinX / scale, b.MinY / scale),
            new Point2D(b.MaxX / scale, b.MinY / scale),
            new Point2D(b.MinX / scale, b.MaxY / scale),
            new Point2D(b.MaxX / scale, b.MaxY / scale)
        }.Select(p => TerminalGeometry.LocalToWorld(el, p)).ToArray();
        var minX = corners.Min(p => p.X);
        var maxX = corners.Max(p => p.X);
        var minY = corners.Min(p => p.Y);
        var maxY = corners.Max(p => p.Y);
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }
}
