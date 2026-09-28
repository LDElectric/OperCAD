using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Conductors;

/// <summary>
/// Helper para transladar waypoints de condutores conectados a um elemento que foi movido.
/// Garante que condutores que conectam elementos movidos mantenham suas conexões com
/// cotovelos ortogonais corretos.
/// </summary>
public static class ConductorHelper
{
    /// <summary>
    /// Translada os waypoints dos condutores conectados a um elemento movido.
    /// Para cada condutor que tem o elemento movido em From ou To, traduz o segmento
    /// do caminho adjacente ao terminal movido, mantendo os pontos do outro terminal fixos.
    /// Se necessário, insere vértices de 90° para manter a conectividade.
    /// </summary>
    public static void TranslateConductorEndpoints(
        Page page,
        PlacedElement movedEl,
        double dx,
        double dy)
    {
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
            return;

        var delta = new Point2D(dx, dy);

        foreach (var conductor in page.Objects.OfType<Conductor>())
        {
            // Condutos preservados são manipulados pelo usuário; não transladar automaticamente
            if (conductor.PreservePath)
                continue;

            bool isFrom = conductor.From.ElementId == movedEl.Id;
            bool isTo = conductor.To.ElementId == movedEl.Id;

            if (!isFrom && !isTo)
                continue;

            if (conductor.Path.Count < 2)
                continue;

            TranslateConductorSide(conductor, isFrom, delta);
        }
    }

    /// <summary>
    /// Para um condutor específico, translada o lado conectado ao terminal movido.
    /// Identifica automaticamente quantos waypoints pertencem ao lado movido
    /// (segmentos contíguos a partir do terminal) e os traduz como unidade,
    /// inserindo vértices de 90° quando a translação quebra a ortogonalidade.
    /// </summary>
    private static void TranslateConductorSide(Conductor conductor, bool isFrom, Point2D delta)
    {
        var path = conductor.Path;
        int startIdx = isFrom ? 0 : path.Count - 1;
        int step = isFrom ? 1 : -1;

        // Encontra o limite do segmento conectado: traduz pontos até encontrar
        // o primeiro ponto que não está no mesmo eixo do terminal (primeira curva)
        var firstTerminal = path[startIdx];
        var firstSegEnd = path[startIdx + step];
        bool sameAxis = Math.Abs(firstTerminal.X - firstSegEnd.X) < 1e-9
                     || Math.Abs(firstTerminal.Y - firstSegEnd.Y) < 1e-9;

        int limitIdx;
        if (sameAxis)
        {
            // Percorre até encontrar onde o eixo muda
            limitIdx = startIdx + step;
            while (limitIdx + step >= 0 && limitIdx + step < path.Count)
            {
                var curr = path[limitIdx];
                var next = path[limitIdx + step];
                bool stillSameAxis = Math.Abs(curr.X - next.X) < 1e-9
                                  || Math.Abs(curr.Y - next.Y) < 1e-9;
                if (!stillSameAxis)
                    break;
                limitIdx += step;
            }
        }
        else
        {
            // Primeiro segmento já é perpendicular ao terminal — traduz só o ponto inicial
            limitIdx = startIdx;
        }

        // Traduz todos os pontos do segmento conectado
        var pts = new List<Point2D>(path);
        for (int i = startIdx; ; i += step)
        {
            if (i < 0 || i >= pts.Count)
                break;
            pts[i] = new Point2D(pts[i].X + delta.X, pts[i].Y + delta.Y);

            if (i == limitIdx)
                break;

            // Se traduzir quebrou a ortogonalidade com o próximo ponto, inserir vértice
            if (i != startIdx)
            {
                var prev = pts[i - step];
                var curr = pts[i];
                bool orthogonal = Math.Abs(prev.X - curr.X) < 1e-9
                               || Math.Abs(prev.Y - curr.Y) < 1e-9;

                if (!orthogonal)
                {
                    // Inserir vértice de 90° entre prev e curr
                    if (Math.Abs(prev.X - curr.X) < 1e-9)
                    {
                        // Mesmo X, divergência em Y → inserir ponto com mesmo Y de prev
                        pts.Insert(i, new Point2D(curr.X, prev.Y));
                        limitIdx += step > 0 ? 1 : 0;
                    }
                    else
                    {
                        // Mesmo Y, divergência em X → inserir ponto com mesmo X de prev
                        pts.Insert(i, new Point2D(prev.X, curr.Y));
                        limitIdx += step > 0 ? 1 : 0;
                    }
                }
            }
        }

        conductor.Path = pts;
    }
}
