using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Separa uma forma de VÁRIOS contornos em partes coerentes, agrupando contornos PRÓXIMOS.
/// <para>
/// Motivo (medido no PDF real): um único path do PDF pode conter TODOS os rótulos de uma linha do
/// desenho — um objeto de 109 mm de largura com as etiquetas de vários contatos. Sem separar,
/// selecionar o rótulo de um contato traz junto o rótulo de outro a 98 mm de distância, e não há
/// como editar/excluir um sem o outro.
/// </para>
/// <para>
/// Contornos ANINHADOS (o furo de uma letra como o "o") ficam SEMPRE no mesmo grupo: separá-los
/// faria o furo ser preenchido como área sólida.
/// </para>
/// </summary>
public static class ShapeContourSplitter
{
    /// <summary>
    /// Divide <paramref name="shape"/> em uma forma por agrupamento de contornos vizinhos
    /// (distância entre caixas ≤ <paramref name="gapMm"/>). Se não houver o que separar, devolve a
    /// PRÓPRIA forma (mesma instância), para o chamador não precisar tratar caso especial.
    /// </summary>
    public static IReadOnlyList<ShapeItem> SplitByProximity(ShapeItem shape, double gapMm)
    {
        // Só faz sentido com contornos múltiplos e sem curvas de Bézier (o alinhamento dos pontos
        // de controle por contorno não é representável, e separar deformaria a curva).
        if (shape.ContourStarts is not { Count: > 1 } || shape.ControlPoints is { Count: > 0 })
            return new[] { shape };

        var ranges = shape.Contours().ToList();
        if (ranges.Count < 2)
            return new[] { shape };

        var gap = Math.Max(0.05, gapMm);
        var bounds = ranges.Select(r => BoundsOf(shape.Points, r.Start, r.End)).ToList();

        // Union-find sobre os contornos.
        var parent = new int[ranges.Count];
        for (var i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
                parent[rb] = ra;
        }

        // Grade espacial: compara só contornos em células vizinhas (um path pode ter 200 contornos).
        var cell = Math.Max(gap, 0.5);
        var grid = new Dictionary<(int X, int Y), List<int>>();
        for (var i = 0; i < bounds.Count; i++)
        {
            var b = bounds[i];
            var x0 = (int)Math.Floor((b.MinX - gap) / cell);
            var x1 = (int)Math.Floor((b.MaxX + gap) / cell);
            var y0 = (int)Math.Floor((b.MinY - gap) / cell);
            var y1 = (int)Math.Floor((b.MaxY + gap) / cell);
            for (var gx = x0; gx <= x1; gx++)
            {
                for (var gy = y0; gy <= y1; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var bucket))
                    {
                        bucket = new List<int>();
                        grid[(gx, gy)] = bucket;
                    }
                    bucket.Add(i);
                }
            }
        }

        foreach (var bucket in grid.Values)
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    var a = bucket[i];
                    var b = bucket[j];
                    if (Find(a) == Find(b))
                        continue;
                    if (GapBetween(bounds[a], bounds[b]) <= gap)
                        Union(a, b);
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        if (groups.Count < 2)
            return new[] { shape };

        var parts = new List<ShapeItem>(groups.Count);
        foreach (var group in groups.Values.OrderBy(g => g.Min(i => ranges[i].Start)))
        {
            var points = new List<Point2D>();
            var starts = new List<int>();
            foreach (var index in group.OrderBy(i => ranges[i].Start))
            {
                var (start, end) = ranges[index];
                starts.Add(points.Count);
                for (var i = start; i < end; i++)
                    points.Add(shape.Points[i]);
            }

            var part = new ShapeItem
            {
                Kind = shape.Kind,
                Points = points,
                ContourStarts = starts.Count > 1 ? starts : null,
                ControlPoints = null,
                Closed = shape.Closed,
                Color = shape.Color,
                PenWidth = shape.PenWidth,
                DashStyle = shape.DashStyle,
                DashPattern = new List<double>(shape.DashPattern),
                DashPhase = shape.DashPhase,
                LineCap = shape.LineCap,
                LineJoin = shape.LineJoin,
                Filling = shape.Filling,
                StartAngle = shape.StartAngle,
                SweepAngle = shape.SweepAngle,
                Rx = shape.Rx,
                Ry = shape.Ry,
                PdfObjectIndex = shape.PdfObjectIndex,
                ImportGroupId = shape.ImportGroupId,
                LayerId = shape.LayerId,
                IsLocked = shape.IsLocked
            };
            parts.Add(part);
        }

        return parts;
    }

    private static Rect2D BoundsOf(List<Point2D> points, int start, int end)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (var i = start; i < end; i++)
        {
            var p = points[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Distância entre duas caixas (0 quando se tocam/sobrepõem — caso dos contornos aninhados).</summary>
    private static double GapBetween(Rect2D a, Rect2D b)
    {
        var dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        var dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        return Math.Max(dx, dy);
    }
}
