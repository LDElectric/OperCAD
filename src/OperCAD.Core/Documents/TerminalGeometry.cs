using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Cálculo de posição e orientação no mundo de um terminal de elemento posicionado,
/// considerando rotação do elemento e usando a escala EFETIVA de cada elemento
/// (<see cref="ElementDefinition.Scale"/>, a mesma usada no render).
/// </summary>
public static class TerminalGeometry
{
    /// <summary>Escala (mm por unidade do arquivo) do elemento, incluindo o ajuste global de tamanho
    /// (<see cref="ElementDefinition.GlobalScale"/>) e a escala de inserção desta instância
    /// (<see cref="PlacedElement.Scale"/>, zoom CAD) — deve bater com o <c>ElementRenderer.ElementScale</c>.</summary>
    private static double ScaleOf(PlacedElement el)
        => el.Definition.Scale * el.Scale * ElementDefinition.GlobalScale;

    /// <summary>Posição (mundo) do terminal.</summary>
    public static Point2D WorldPosition(PlacedElement el, ElementTerminal t)
    {
        var s = ScaleOf(el);
        var (rx, ry) = Rotate((t.X - el.Definition.HotspotX) * s, (t.Y - el.Definition.HotspotY) * s, el.Rotation);
        return new Point2D(el.X + rx, el.Y + ry);
    }

    /// <summary>Converte um ponto do mundo para as coordenadas locais do elemento.</summary>
    public static Point2D WorldToLocal(PlacedElement el, Point2D world)
    {
        var s = ScaleOf(el);
        var (rx, ry) = Rotate(world.X - el.X, world.Y - el.Y, -el.Rotation);
        return new Point2D(rx / s + el.Definition.HotspotX, ry / s + el.Definition.HotspotY);
    }

    /// <summary>Converte um ponto local do elemento para o mundo.</summary>
    public static Point2D LocalToWorld(PlacedElement el, Point2D local)
    {
        var s = ScaleOf(el);
        var (rx, ry) = Rotate((local.X - el.Definition.HotspotX) * s, (local.Y - el.Definition.HotspotY) * s, el.Rotation);
        return new Point2D(el.X + rx, el.Y + ry);
    }

    /// <summary>Orientação do terminal no mundo ('n','e','s','w').</summary>
    public static char WorldOrientation(PlacedElement el, ElementTerminal t)
    {
        var (vx, vy) = OrientationVector(t.Orientation);
        var (rx, ry) = Rotate(vx, vy, el.Rotation);
        return VectorToOrientation(rx, ry);
    }

    /// <summary>Vetor local da orientação ('n'→(0,1), 'e'→(1,0), 's'→(0,-1), 'w'→(-1,0)).</summary>
    public static (double X, double Y) OrientationVector(char o) => o switch
    {
        'n' => (0, 1),
        'e' => (1, 0),
        's' => (0, -1),
        'w' => (-1, 0),
        _ => (1, 0)
    };

    private static char VectorToOrientation(double x, double y)
    {
        if (Math.Abs(x) >= Math.Abs(y))
            return x >= 0 ? 'e' : 'w';
        return y >= 0 ? 'n' : 's';
    }

    /// <summary>Rotação local→mundo: R(-rot), igual ao ElementRenderer.</summary>
    private static (double X, double Y) Rotate(double x, double y, double rotDeg)
    {
        var a = -rotDeg * Math.PI / 180.0;
        var ca = Math.Cos(a);
        var sa = Math.Sin(a);
        return (x * ca - y * sa, x * sa + y * ca);
    }

    /// <summary>Localiza o terminal mais próximo de uma posição do mundo.</summary>
    public static (PlacedElement Element, int Index, Point2D Pos)? HitTestTerminal(Page page, Point2D world, double toleranceMm)
    {
        PlacedElement? bestElement = null;
        int bestIndex = -1;
        Point2D bestPos = default;
        var bestDist = toleranceMm;

        foreach (var obj in page.Objects)
        {
            if (obj is not PlacedElement el)
                continue;
            var terminals = el.Definition.Terminals;
            for (var i = 0; i < terminals.Count; i++)
            {
                var pos = WorldPosition(el, terminals[i]);
                var dist = pos.DistanceTo(world);
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    bestElement = el;
                    bestIndex = i;
                    bestPos = pos;
                }
            }
        }

        return bestElement is null ? null : (bestElement, bestIndex, bestPos);
    }
}
