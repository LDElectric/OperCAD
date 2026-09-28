using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Recognition;

/// <summary>
/// Descritor de forma invariante a posição e escala: rasteriza o conjunto de primitivas de um
/// aglomerado em uma grade de "tinta" (N×N), normalizada. Permite comparar um símbolo importado
/// (qualquer escala/pitch) com amostras já marcadas pelo usuário.
/// </summary>
public static class InkDescriptor
{
    /// <summary>Descreve um conjunto de formas em um vetor de densidade N×N (L1 normalizado).</summary>
    public static double[] Describe(IReadOnlyList<ShapeItem> shapes, int grid = 16)
    {
        var cells = new double[grid * grid];
        if (shapes is null || shapes.Count == 0)
            return cells;

        // BBox de todos os segmentos (para normalizar posição/escala).
        var segments = new List<(Point2D A, Point2D B)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in shapes)
        {
            foreach (var seg in ShapeGeometry.OutlineSegments(s))
            {
                segments.Add(seg);
                minX = Math.Min(minX, Math.Min(seg.A.X, seg.B.X));
                maxX = Math.Max(maxX, Math.Max(seg.A.X, seg.B.X));
                minY = Math.Min(minY, Math.Min(seg.A.Y, seg.B.Y));
                maxY = Math.Max(maxY, Math.Max(seg.A.Y, seg.B.Y));
            }
        }
        if (segments.Count == 0)
            return cells;

        var span = Math.Max(maxX - minX, maxY - minY);
        if (span <= 0) span = 1;
        // amostra a cada ~ metade de uma célula para preencher o traço
        var stepLen = span / (grid * 2.0);

        foreach (var seg in segments)
        {
            var len = Math.Sqrt((seg.B.X - seg.A.X) * (seg.B.X - seg.A.X) + (seg.B.Y - seg.A.Y) * (seg.B.Y - seg.A.Y));
            var steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(stepLen, 1e-9)));
            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;
                var x = seg.A.X + (seg.B.X - seg.A.X) * t;
                var y = seg.A.Y + (seg.B.Y - seg.A.Y) * t;
                var nx = (x - minX) / span;
                var ny = (y - minY) / span;
                var cx = Math.Clamp((int)(nx * grid), 0, grid - 1);
                var cy = Math.Clamp((int)(ny * grid), 0, grid - 1);
                cells[cy * grid + cx] += 1;
            }
        }

        // L1 normaliza
        var sum = 0.0;
        foreach (var c in cells) sum += c;
        if (sum > 0)
            for (var i = 0; i < cells.Length; i++) cells[i] /= sum;
        return cells;
    }

    /// <summary>Similaridade por cosseno entre dois descritores L1 (0..1).</summary>
    public static double Cosine(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom <= 0 ? 0 : dot / denom;
    }

    /// <summary>
    /// Gera descritores rotacionados (0°, 90°, 180°, 270°) de um conjunto de formas.
    /// Útil para comparação invariante a rotação.
    /// </summary>
    public static List<double[]> DescribeRotated(IReadOnlyList<ShapeItem> shapes, int grid = 16)
    {
        var result = new List<double[]>(4);
        var baseDescriptor = Describe(shapes, grid);
        result.Add(baseDescriptor);

        // Para cada rotação, aplica transformação angular e recalcula
        foreach (var angle in new[] { 90.0, 180.0, 270.0 })
        {
            var rotated = RotateShapes(shapes, angle);
            result.Add(Describe(rotated, grid));
        }

        return result;
    }

    /// <summary>
    /// Similaridade por cosseno invariante a rotação (4 orientações: 0°, 90°, 180°, 270°).
    /// Retorna a maior similaridade encontrada entre as rotações.
    /// </summary>
    public static double CosineRotational(IReadOnlyList<ShapeItem> a, IReadOnlyList<double> bDescriptor, int grid = 16)
    {
        var aDescriptors = DescribeRotated(a, grid);
        var best = 0.0;
        foreach (var aDesc in aDescriptors)
        {
            var sim = Cosine(aDesc, bDescriptor);
            if (sim > best) best = sim;
        }
        return best;
    }

    /// <summary>Gira um conjunto de formas por um ângulo em graus em torno do centroide.</summary>
    private static List<ShapeItem> RotateShapes(IReadOnlyList<ShapeItem> shapes, double angleDeg)
    {
        // Calcula o centroide global
        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        foreach (var s in shapes)
        {
            foreach (var p in s.Points)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        }
        var cx = (minX + maxX) / 2.0;
        var cy = (minY + maxY) / 2.0;

        var rad = angleDeg * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        var result = new List<ShapeItem>(shapes.Count);
        foreach (var s in shapes)
        {
            var rotatedPoints = s.Points.Select(p =>
            {
                var dx = p.X - cx;
                var dy = p.Y - cy;
                return new Point2D(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
            }).ToList();

            result.Add(new ShapeItem
            {
                Kind = s.Kind,
                Points = rotatedPoints,
                Color = s.Color,
                PenWidth = s.PenWidth,
                Closed = s.Closed
            });
        }

        return result;
    }
}

/// <summary>Amostra marcada pelo usuário: rótulo de elemento (ou negativa = "não é elemento").</summary>
public sealed record ElementSample
{
    public string Label { get; init; } = string.Empty;   // nome do elemento; vazio = negativa
    public bool IsElement => !string.IsNullOrEmpty(Label);
    public int Grid { get; init; } = 16;
    public double[] Descriptor { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Triagem por amostras: o usuário marca alguns aglomerados como um elemento (positivo) ou como
/// "não é elemento" (negativo); aglomerados novos são classificados pelo vizinho mais parecido
/// (descritor invariante a escala/posição).
/// </summary>
public sealed class ElementTriage
{
    private readonly List<ElementSample> _samples = new();

    public IReadOnlyList<ElementSample> Samples => _samples;

    public void Clear() => _samples.Clear();

    public void AddPositive(string label, IReadOnlyList<ShapeItem> shapes, int grid = 16)
        => _samples.Add(new ElementSample { Label = label, Grid = grid, Descriptor = InkDescriptor.Describe(shapes, grid) });

    /// <summary>Adiciona uma amostra positiva com descriptor pré-computado (usado na carga de disco).</summary>
    public void AddPositive(string label, double[] descriptor, int grid = 16)
        => _samples.Add(new ElementSample { Label = label, Grid = grid, Descriptor = descriptor });

    public void AddNegative(IReadOnlyList<ShapeItem> shapes, int grid = 16)
        => _samples.Add(new ElementSample { Label = string.Empty, Grid = grid, Descriptor = InkDescriptor.Describe(shapes, grid) });

    /// <summary>Adiciona uma amostra negativa com descriptor pré-computado (usado na carga de disco).</summary>
    public void AddNegative(double[] descriptor, int grid = 16)
        => _samples.Add(new ElementSample { Label = string.Empty, Grid = grid, Descriptor = descriptor });

    /// <summary>
    /// Classifica um aglomerado: retorna o rótulo positivo mais similar se for suficientemente
    /// parecido (e mais parecido que qualquer negativa); senão null (= "não identificado / não é elemento").
    /// Compara usando 4 rotações (0°, 90°, 180°, 270°) para invariância a orientação.
    /// </summary>
    public (string? Label, double Score)? Classify(IReadOnlyList<ShapeItem> shapes, double threshold = 0.85)
    {
        var grid = _samples.Count > 0 ? _samples[0].Grid : 16;
        string? bestLabel = null;
        double bestScore = 0, bestNegative = 0;
        foreach (var s in _samples)
        {
            // Compara o descriptor do sample com as 4 rotações do shapes
            var sim = InkDescriptor.CosineRotational(shapes, s.Descriptor, grid);
            if (s.IsElement)
            {
                if (sim > bestScore) { bestScore = sim; bestLabel = s.Label; }
            }
            else
            {
                if (sim > bestNegative) bestNegative = sim;
            }
        }
        if (bestLabel is null || bestScore < threshold || bestScore <= bestNegative)
            return null;
        return (bestLabel, bestScore);
    }
}
