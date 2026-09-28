using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Geometria pura de <see cref="ShapeItem"/> para hit-test (seleção): distância ao contorno
/// (traço) e teste de ponto dentro da área preenchida. Independente de renderização.
/// </summary>
public static class ShapeGeometry
{
    /// <summary>True se a forma tem preenchimento visível (Filling != none/vazio).</summary>
    public static bool IsFilled(ShapeItem shape)
        => !string.IsNullOrEmpty(shape.Filling) && shape.Filling != "none";

    /// <summary>É uma região fechada (tem interior) — retângulo, elipse, polígono fechado, triângulo, retângulo arredondado.</summary>
    public static bool IsClosedArea(ShapeItem shape)
        => shape.Kind switch
        {
            ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Triangle or ShapeKind.RoundedRectangle => true,
            ShapeKind.Polygon => shape.Closed,
            _ => false
        };

    /// <summary>
    /// True quando a forma deve ser editada pelas ALÇAS NAS EXTREMIDADES (arrastar move aquele
    /// ponto, esticando o traço) em vez das 8 alças da caixa envolvente. É o comportamento do
    /// editor de PDF para traços: a linha tem alças nos próprios extremos.
    /// Compartilhado entre a renderização das alças (Drawing) e o hit-test (UI) para que o
    /// desenho e o clique nunca divirjam.
    /// </summary>
    public static bool UsesVertexHandles(ShapeItem shape)
        => shape.Kind is ShapeKind.Line or ShapeKind.Polygon or ShapeKind.Curve
           && shape.Points.Count is >= 2 and <= 8;

    /// <summary>
    /// Distância (mm, mundo) do ponto ao contorno/traço da forma. Para formas vazias (sem
    /// área) testa apenas os segmentos do traço. Usado para decidir se o clique está "sobre" a forma.
    /// </summary>
    public static double DistanceToOutline(ShapeItem shape, Point2D p)
    {
        var pts = shape.Points;
        if (pts.Count < 2)
            return double.MaxValue;

        // Hit-test analítico para arco e elipse (muito mais preciso que approximação por segmentos)
        switch (shape.Kind)
        {
            case ShapeKind.Arc:
                return DistanceToArc(shape, p);
            case ShapeKind.Ellipse:
                return DistanceToEllipse(shape, p);
            case ShapeKind.EllipseArc:
                return DistanceToEllipseArc(shape, p);
        }

        var closed = IsClosedArea(shape);

        // Forma com VÁRIOS contornos (texto vetorizado): a distância é a MENOR entre os
        // contornos; ligar um contorno ao outro inventaria proximidade onde não há desenho.
        if (shape.ContourStarts is { Count: > 1 })
        {
            var bestMulti = double.MaxValue;
            foreach (var (start, end) in shape.Contours())
            {
                for (var i = start + 1; i < end; i++)
                    bestMulti = Math.Min(bestMulti, DistanceToSegment(p, shape.Points[i - 1], shape.Points[i]));
                if (end - start >= 3)
                    bestMulti = Math.Min(bestMulti, DistanceToSegment(p, shape.Points[end - 1], shape.Points[start]));
            }
            return bestMulti;
        }

        var outline = OutlineVertices(shape, closed);
        if (outline.Count < 2)
            return double.MaxValue;

        var best = double.MaxValue;
        for (var i = 1; i < outline.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, outline[i - 1], outline[i]));
        if (closed)
            best = Math.Min(best, DistanceToSegment(p, outline[^1], outline[0]));
        return best;
    }

    /// <summary>Normaliza um ângulo em graus para [0, 360).</summary>
    private static double NormalizeAngle(double angle)
    {
        var a = angle % 360.0;
        return a < 0 ? a + 360.0 : a;
    }

    /// <summary>True se o ângulo está dentro do trecho varrido [start, start+sweep] do arco.</summary>
    private static bool AngleWithinArc(double angle, double start, double sweep)
    {
        if (sweep == 0) return false;
        var a = NormalizeAngle(angle);
        var s = NormalizeAngle(start);
        if (sweep > 0)
        {
            var e = s + sweep;
            if (e >= 360.0)
            {
                var wrapped = e % 360.0;
                return a >= s || a <= wrapped;
            }
            return a >= s && a < e;
        }
        else
        {
            var e = s + sweep;
            if (e < 0)
            {
                var wrapped = ((e % 360.0) + 360.0) % 360.0;
                return a <= s || a >= wrapped;
            }
            return a <= s && a > e;
        }
    }

    /// <summary>Distância analítica de um ponto a um arco circular.</summary>
    private static double DistanceToArc(ShapeItem shape, Point2D p)
    {
        if (shape.Points.Count < 2) return double.MaxValue;
        var center = shape.Points[0];
        var radius = center.DistanceTo(shape.Points[1]);
        if (radius <= 0) return double.MaxValue;

        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        var distToCenter = Math.Sqrt(dx * dx + dy * dy);

        // Se o ângulo do ponto está dentro do trecho do arco → distância ao raio
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (AngleWithinArc(angle, shape.StartAngle, shape.SweepAngle))
            return Math.Abs(distToCenter - radius);

        // Fora do arco: distância ao ponto final mais próximo
        var startRad = shape.StartAngle * Math.PI / 180.0;
        var endRad = (shape.StartAngle + shape.SweepAngle) * Math.PI / 180.0;
        var startPt = new Point2D(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        var endPt = new Point2D(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));
        return Math.Min(p.DistanceTo(startPt), p.DistanceTo(endPt));
    }

    /// <summary>
    /// Distância analítica de um ponto ao trecho de uma elipse (arco elíptico).
    /// A elipse é definida pelo centro em Points[0] e as semi-extensões rx=|dx|, ry=|dy|
    /// relativas a Points[1]. O trecho varrido é definido por StartAngle/SweepAngle.
    /// </summary>
    private static double DistanceToEllipseArc(ShapeItem shape, Point2D p)
    {
        if (shape.Points.Count < 2) return double.MaxValue;
        var c = shape.Points[0];
        var q = shape.Points[1];
        var rx = Math.Abs(q.X - c.X);
        var ry = Math.Abs(q.Y - c.Y);
        if (rx <= 0 || ry <= 0) return double.MaxValue;

        var dx = p.X - c.X;
        var dy = p.Y - c.Y;
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        if (AngleWithinArc(angle, shape.StartAngle, shape.SweepAngle))
        {
            // Ponto na elipse no mesmo ângulo do cursor
            var theta = angle * Math.PI / 180.0;
            var cos = Math.Cos(theta);
            var sin = Math.Sin(theta);
            var r = rx * ry / Math.Sqrt(Math.Pow(ry * cos, 2) + Math.Pow(rx * sin, 2));
            return p.DistanceTo(new Point2D(c.X + r * cos, c.Y + r * sin));
        }

        // Fora do trecho: distância ao ponto final mais próximo
        var startRad = shape.StartAngle * Math.PI / 180.0;
        var endRad = (shape.StartAngle + shape.SweepAngle) * Math.PI / 180.0;
        var startPt = new Point2D(c.X + rx * Math.Cos(startRad), c.Y + ry * Math.Sin(startRad));
        var endPt = new Point2D(c.X + rx * Math.Cos(endRad), c.Y + ry * Math.Sin(endRad));
        return Math.Min(p.DistanceTo(startPt), p.DistanceTo(endPt));
    }

    /// <summary>Distância analítica de um ponto à elipse (aproximação iterativa).</summary>
    private static double DistanceToEllipse(ShapeItem shape, Point2D p)
    {
        if (shape.Points.Count < 2) return double.MaxValue;
        var a = shape.Points[0]; var b = shape.Points[1];
        var cx = (a.X + b.X) / 2.0; var cy = (a.Y + b.Y) / 2.0;
        var rx = Math.Abs(b.X - a.X) / 2.0; var ry = Math.Abs(b.Y - a.Y) / 2.0;
        if (rx <= 0 || ry <= 0) return double.MaxValue;

        // Normaliza o ponto para o espaço da elipse
        var dx = p.X - cx; var dy = p.Y - cy;

        // Para elipses, a distância ao contorno pode ser approximada por:
        // d = |sqrt((dx/rx)^2 + (dy/ry)^2) - 1| * min(rx, ry)
        var norm = Math.Sqrt((dx / rx) * (dx / rx) + (dy / ry) * (dy / ry));
        if (norm <= 0) return Math.Min(rx, ry);

        // Distância radial normalizada
        var radialDist = Math.Abs(norm - 1.0);

        // Escala pela média ponderada dos raios na direção do ponto
        var angle = Math.Atan2(dy / ry, dx / rx);
        var localRadius = rx * ry / Math.Sqrt(
            (ry * Math.Cos(angle)) * (ry * Math.Cos(angle)) +
            (rx * Math.Sin(angle)) * (rx * Math.Sin(angle)));

        return radialDist * localRadius;
    }

    /// <summary>
    /// True se o ponto está dentro da região da forma. Para elipse/polígono fechado/triângulo,
    /// testa ponto-em-polígono; para retângulo testa o retângulo. Independente de preenchimento
    /// (chame apenas quando quiser tratar o interior como área).
    /// </summary>
    public static bool PointInRegion(ShapeItem shape, Point2D p)
    {
        if (!IsClosedArea(shape) || shape.Points.Count == 0)
            return false;

        // Vários contornos: regra PAR-ÍMPAR sobre todos eles — é o que preserva os FUROS das
        // letras (um ponto dentro do vazio do "o" é cruzado duas vezes ⇒ fora).
        if (shape.ContourStarts is { Count: > 1 })
        {
            var inside = false;
            foreach (var (start, end) in shape.Contours())
            {
                var contour = new List<Point2D>(end - start);
                for (var i = start; i < end; i++)
                    contour.Add(shape.Points[i]);
                if (PointInPolygon(contour, p))
                    inside = !inside;
            }
            return inside;
        }

        return shape.Kind switch
        {
            ShapeKind.Rectangle or ShapeKind.RoundedRectangle => PointInRect(shape, p),
            ShapeKind.Ellipse => PointInEllipse(shape, p),
            _ => PointInPolygon(OutlineVertices(shape, true), p)
        };
    }

    /// <summary>Vértices do contorno em coordenadas de mundo, seguindo o renderer.</summary>
    private static List<Point2D> OutlineVertices(ShapeItem shape, bool closed)
    {
        var pts = shape.Points;
        switch (shape.Kind)
        {
            case ShapeKind.Line:
                return pts.Count >= 2 ? new List<Point2D> { pts[0], pts[1] } : new List<Point2D>();
            case ShapeKind.Rectangle:
            {
                var a = pts[0]; var b = pts[1];
                var minX = Math.Min(a.X, b.X); var maxX = Math.Max(a.X, b.X);
                var minY = Math.Min(a.Y, b.Y); var maxY = Math.Max(a.Y, b.Y);
                return new List<Point2D>
                {
                    new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)
                };
            }
            case ShapeKind.RoundedRectangle:
            {
                var a = pts[0]; var b = pts[1];
                var minX = Math.Min(a.X, b.X); var maxX = Math.Max(a.X, b.X);
                var minY = Math.Min(a.Y, b.Y); var maxY = Math.Max(a.Y, b.Y);
                return new List<Point2D>
                {
                    new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)
                };
            }
            case ShapeKind.Ellipse:
            {
                var a = pts[0]; var b = pts[1];
                var cx = (a.X + b.X) / 2.0; var cy = (a.Y + b.Y) / 2.0;
                var rx = Math.Abs(b.X - a.X) / 2.0; var ry = Math.Abs(b.Y - a.Y) / 2.0;
                if (rx <= 0 || ry <= 0) return new List<Point2D>();
                const int n = 48;
                var ring = new List<Point2D>();
                for (var i = 0; i < n; i++)
                {
                    var t = i * 2.0 * Math.PI / n;
                    ring.Add(new Point2D(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
                }
                return ring;
            }
            case ShapeKind.Arc:
            {
                if (pts.Count < 1) return new List<Point2D>();
                var center = pts[0];
                var radius = pts.Count >= 2 ? center.DistanceTo(pts[1]) : 5.0;
                var startRad = shape.StartAngle * Math.PI / 180.0;
                var sweepRad = shape.SweepAngle * Math.PI / 180.0;
                const int n = 48;
                var arc = new List<Point2D>();
                for (var i = 0; i <= n; i++)
                {
                    var t = startRad + sweepRad * i / n;
                    arc.Add(new Point2D(center.X + radius * Math.Cos(t), center.Y + radius * Math.Sin(t)));
                }
                return arc;
            }
            case ShapeKind.EllipseArc:
            {
                if (pts.Count < 2) return new List<Point2D>();
                var c = pts[0]; var q = pts[1];
                var rx = Math.Abs(q.X - c.X);
                var ry = Math.Abs(q.Y - c.Y);
                if (rx <= 0 || ry <= 0) return new List<Point2D>();
                var startRad = shape.StartAngle * Math.PI / 180.0;
                var sweepRad = shape.SweepAngle * Math.PI / 180.0;
                const int n = 48;
                var arc = new List<Point2D>();
                for (var i = 0; i <= n; i++)
                {
                    var t = startRad + sweepRad * i / n;
                    arc.Add(new Point2D(c.X + rx * Math.Cos(t), c.Y + ry * Math.Sin(t)));
                }
                return arc;
            }
            case ShapeKind.Triangle:
            {
                if (pts.Count == 3)
                    return pts.ToList();
                if (pts.Count < 2) return new List<Point2D>();
                // Fallback: 2 pontos → triângulo isósceles (compatibilidade)
                var p0 = pts[0]; var p1 = pts[1];
                var midX = (p0.X + p1.X) / 2.0;
                var topY = Math.Max(p0.Y, p1.Y);
                var botY = Math.Min(p0.Y, p1.Y);
                return new List<Point2D>
                {
                    new(p0.X, botY), new(p1.X, botY), new(midX, topY)
                };
            }
            case ShapeKind.Curve:
                if (pts.Count < 2) return pts.ToList();
                return FlattenCurve(pts, shape.Closed);
            default: // Polygon
                return pts.ToList();
        }
    }

    /// <summary>
    /// Polilinha amostrada de uma curva suave de Catmull-Rom que passa pelos pontos âncora.
    /// Usado pela renderização, hit-test e bounds da forma <see cref="ShapeKind.Curve"/>.
    /// </summary>
    public static List<Point2D> FlattenCurve(IReadOnlyList<Point2D> anchors, bool closed, int subdiv = 16)
    {
        var result = new List<Point2D>();
        var n = anchors.Count;
        if (n < 2) return anchors.ToList();
        if (n == 2)
        {
            result.Add(anchors[0]);
            result.Add(anchors[1]);
            return result;
        }

        if (closed)
        {
            for (var i = 0; i < n; i++)
                AddSpan(anchors[(i - 1 + n) % n], anchors[i], anchors[(i + 1) % n], anchors[(i + 2) % n],
                        result, subdiv, skipFirst: i != 0);
        }
        else
        {
            AddSpan(anchors[0], anchors[0], anchors[1], anchors[2], result, subdiv, skipFirst: false);
            for (var i = 1; i < n - 2; i++)
                AddSpan(anchors[i - 1], anchors[i], anchors[i + 1], anchors[i + 2], result, subdiv, skipFirst: true);
            AddSpan(anchors[n - 3], anchors[n - 2], anchors[n - 1], anchors[n - 1], result, subdiv, skipFirst: true);
        }
        return result;
    }

    private static void AddSpan(Point2D p0, Point2D p1, Point2D p2, Point2D p3,
        List<Point2D> result, int subdiv, bool skipFirst)
    {
        for (var s = 0; s <= subdiv; s++)
        {
            if (s == 0 && skipFirst) continue;
            var t = (double)s / subdiv;
            result.Add(CatmullRom(p0, p1, p2, p3, t));
        }
    }

    private static Point2D CatmullRom(Point2D p0, Point2D p1, Point2D p2, Point2D p3, double t)
    {
        var tt = t * t;
        var ttt = tt * t;
        var x = 0.5 * (2 * p1.X + (-p0.X + p2.X) * t
                       + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * tt
                       + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * ttt);
        var y = 0.5 * (2 * p1.Y + (-p0.Y + p2.Y) * t
                       + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * tt
                       + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * ttt);
        return new Point2D(x, y);
    }

    /// <summary>
    /// True quando a forma REALMENTE toca o retângulo (laço de seleção) — testa a GEOMETRIA, não
    /// a caixa envolvente. Sem isto, uma linha horizontal longa era selecionada só porque a caixa
    /// dela cruzava o retângulo (trazendo traços distantes junto na cópia).
    /// </summary>
    public static bool TouchesRect(ShapeItem shape, Rect2D rect)
    {
        if (shape.Points.Count == 0)
            return false;

        // Interior de forma preenchida que cobre o retângulo inteiro (ex.: um fundo).
        if (IsClosedArea(shape) && IsFilled(shape) &&
            PointInRegion(shape, new Point2D(rect.CenterX, rect.CenterY)))
            return true;

        return PolylineTouchesRect(OutlineSegments(shape), rect);
    }

    /// <summary>True quando algum dos segmentos informados toca (cruza ou fica dentro) o retângulo.</summary>
    public static bool PolylineTouchesRect(IReadOnlyList<(Point2D A, Point2D B)> segments, Rect2D rect)
    {
        foreach (var (a, b) in segments)
        {
            if (rect.Contains(a) || rect.Contains(b) || SegmentIntersectsRect(a, b, rect))
                return true;
        }
        return false;
    }

    /// <summary>Recorte do segmento contra o retângulo (Liang-Barsky): true se houver interseção.</summary>
    public static bool SegmentIntersectsRect(Point2D a, Point2D b, Rect2D r)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        double t0 = 0, t1 = 1;

        Span<double> p = stackalloc double[] { -dx, dx, -dy, dy };
        Span<double> q = stackalloc double[] { a.X - r.MinX, r.MaxX - a.X, a.Y - r.MinY, r.MaxY - a.Y };

        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-12)
            {
                if (q[i] < 0)
                    return false; // paralelo a esta borda e fora dela
                continue;
            }

            var t = q[i] / p[i];
            if (p[i] < 0)
            {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            }
            else
            {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
        }

        return true;
    }

    /// <summary>
    /// Segmentos do contorno/traço da forma em coordenadas de mundo (fechados quando a forma
    /// é uma área fechada). Usado para descritores e teste de proximidade.
    /// </summary>
    public static List<(Point2D A, Point2D B)> OutlineSegments(ShapeItem shape)
    {
        // Forma com VÁRIOS contornos: gera segmentos DENTRO de cada contorno, nunca ligando um
        // ao outro (o segmento de ligação criava áreas preenchidas entre letras e aparecia no
        // destaque de seleção).
        if (shape.ContourStarts is { Count: > 1 })
        {
            var multi = new List<(Point2D A, Point2D B)>();
            foreach (var (start, end) in shape.Contours())
            {
                for (var i = start + 1; i < end; i++)
                    multi.Add((shape.Points[i - 1], shape.Points[i]));
                if (end - start >= 3)
                    multi.Add((shape.Points[end - 1], shape.Points[start]));
            }
            return multi;
        }

        var closed = IsClosedArea(shape);
        var outline = OutlineVertices(shape, closed);
        var segments = new List<(Point2D A, Point2D B)>();
        if (outline.Count < 2) return segments;
        for (var i = 1; i < outline.Count; i++)
            segments.Add((outline[i - 1], outline[i]));
        if (closed && outline.Count >= 3)
            segments.Add((outline[^1], outline[0]));
        return segments;
    }

    private static bool PointInRect(ShapeItem shape, Point2D p)
    {
        var a = shape.Points[0]; var b = shape.Points[1];
        var minX = Math.Min(a.X, b.X); var maxX = Math.Max(a.X, b.X);
        var minY = Math.Min(a.Y, b.Y); var maxY = Math.Max(a.Y, b.Y);
        return p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
    }

    private static bool PointInEllipse(ShapeItem shape, Point2D p)
    {
        var a = shape.Points[0]; var b = shape.Points[1];
        var cx = (a.X + b.X) / 2.0; var cy = (a.Y + b.Y) / 2.0;
        var rx = Math.Abs(b.X - a.X) / 2.0; var ry = Math.Abs(b.Y - a.Y) / 2.0;
        if (rx <= 0 || ry <= 0) return false;
        var dx = (p.X - cx) / rx; var dy = (p.Y - cy) / ry;
        return dx * dx + dy * dy <= 1.0;
    }

    private static bool PointInPolygon(List<Point2D> poly, Point2D p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var pi = poly[i]; var pj = poly[j];
            if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToSegment(Point2D p, Point2D a, Point2D b)
    {
        var abx = b.X - a.X; var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        if (len2 <= 0) return p.DistanceTo(a);
        var t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2;
        t = Math.Clamp(t, 0, 1);
        var qx = a.X + t * abx; var qy = a.Y + t * aby;
        return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
    }
}
