using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>
/// Detecta automaticamente a "área útil de desenho" de uma página importada (Estratégia B),
/// descartando a moldura externa do PDF. Algoritmo v10:
/// 1. Encontra bordas externas (linhas longas que encostam nas bordas do papel)
/// 2. Encontra bordas internas (linhas 3-60pt inset das bordas externas)
/// 3. Usa a moldura interna como área de desenho
/// </summary>
public static class PdfAreaDetector
{
    private const double PointsToMm = 25.4 / 72.0;

    /// <summary>
    /// Retorna o retângulo (mm) da área útil, ou null quando não é possível detectar com
    /// confiança (nesse caso o chamador deve recair na seleção manual / importação completa).
    /// </summary>
    public static Rect2D? Detect(IReadOnlyList<ShapeItem> shapes)
        => Detect(shapes, null, null);

    /// <summary>
    /// Retorna o retângulo (mm) da área útil. Quando as dimensões do PAPEL são informadas,
    /// usa a detecção por MOLDURA — robusta a molduras compostas (2-3 retângulos concêntricos),
    /// molduras desenhadas como 4 linhas soltas, carimbo e faixas de guias. Sem as dimensões do
    /// papel, recai na heurística histórica de "maior retângulo".
    /// </summary>
    public static Rect2D? Detect(IReadOnlyList<ShapeItem> shapes, double? paperWidthMm, double? paperHeightMm)
    {
        if (shapes is null || shapes.Count == 0)
            return null;

        if (paperWidthMm is { } pw && paperHeightMm is { } ph && pw > 0 && ph > 0)
        {
            var interior = DetectFrameInterior(shapes, pw, ph);
            if (interior is { } rect)
                return rect;
        }

        return DetectByLargestRectangle(shapes);
    }

    /// <summary>
    /// Detecção v10: encontra moldura EXTERNA (linhas que encostam nas bordas) e
    /// INTERNA (linhas 3-60pt inset). Retorna o interior da moldura interna.
    /// Coordenadas já estão em mm (convertidas pelo PdfImporter).
    /// </summary>
    private static Rect2D? DetectFrameInterior(IReadOnlyList<ShapeItem> shapes, double paperW, double paperH)
    {
        // Coletar todas as linhas longas do desenho (em mm)
        var hLines = new List<(double Y, double MinX, double MaxX, double Length)>();
        var vLines = new List<(double X, double MinY, double MaxY, double Length)>();

        CollectLines(shapes, paperW, paperH, hLines, vLines);

        if (hLines.Count == 0 || vLines.Count == 0)
            return null;

        // Passo 1: Encontrar candidatos à borda EXTERNA
        var topOuter = FindOuterBorderH(hLines, paperH, isTop: true);
        var botOuter = FindOuterBorderH(hLines, paperH, isTop: false);
        var leftOuter = FindOuterBorderV(vLines, paperW, isLeft: true);
        var rightOuter = FindOuterBorderV(vLines, paperW, isLeft: false);

        if (topOuter is null || botOuter is null || leftOuter is null || rightOuter is null)
        {
            // Fallback: tentar com retângulos nativos
            return DetectFrameFromRectangles(shapes, paperW, paperH);
        }

        var outerTopY = topOuter.Value.Y;
        var outerBotY = botOuter.Value.Y;
        var outerLeftX = leftOuter.Value.X;
        var outerRightX = rightOuter.Value.X;

        // Passo 2: Encontrar moldura INTERNA (linhas 3-60pt / ~0.8-16mm inset)
        // Usar passes recursivos para encontrar a MOLDURA MAIS INTERNA.
        const double minInsetMm = 0.8;   // ≈ 3pt
        const double maxInsetMm = 16.0;  // ≈ 60pt

        var curTopY = outerTopY;
        var curBotY = outerBotY;
        var curLeftX = outerLeftX;
        var curRightX = outerRightX;

        for (var pass = 0; pass < 5; pass++)
        {
            var newTopY = FindInnerBorderH(hLines, curTopY, curBotY, paperW, minInsetMm, maxInsetMm, isTop: true);
            var newBotY = FindInnerBorderH(hLines, curTopY, curBotY, paperW, minInsetMm, maxInsetMm, isTop: false);
            var newLeftX = FindInnerBorderV(vLines, curLeftX, curRightX, paperH, minInsetMm, maxInsetMm * 0.83, isLeft: true);
            var newRightX = FindInnerBorderV(vLines, curLeftX, curRightX, paperH, minInsetMm, maxInsetMm * 0.83, isLeft: false);

            if (newTopY is null || newBotY is null || newLeftX is null || newRightX is null)
                break;

            // Validar que a nova moldura é menor que a atual
            if (newRightX.Value - newLeftX.Value < (curRightX - curLeftX) - 1 &&
                newBotY.Value - newTopY.Value < (curBotY - curTopY) - 1)
            {
                curTopY = newTopY.Value;
                curBotY = newBotY.Value;
                curLeftX = newLeftX.Value;
                curRightX = newRightX.Value;
            }
            else
                break;
        }

        // Margem para não incluir a própria linha da moldura (0.2mm)
        const double insetMm = 0.2;
        var x = curLeftX + insetMm;
        var y = curTopY + insetMm;
        var w = (curRightX - curLeftX) - 2 * insetMm;
        var h = (curBotY - curTopY) - 2 * insetMm;

        if (w > 0 && h > 0)
            return new Rect2D(x, y, w, h);

        return null;
    }

    /// <summary>
    /// Coleta linhas horizontais e verticais longas de todas as formas.
    /// </summary>
    private static void CollectLines(
        IReadOnlyList<ShapeItem> shapes,
        double paperW, double paperH,
        List<(double Y, double MinX, double MaxX, double Length)> hLines,
        List<(double X, double MinY, double MaxY, double Length)> vLines)
    {
        var minHL = paperW * 0.30; // 30% da largura = linha horizontal significativa
        var minVL = paperH * 0.30; // 30% da altura = linha vertical significativa

        foreach (var shape in shapes)
        {
            foreach (var (a, b) in ShapeGeometry.OutlineSegments(shape))
            {
                var dx = Math.Abs(b.X - a.X);
                var dy = Math.Abs(b.Y - a.Y);
                var length = Math.Sqrt(dx * dx + dy * dy);

                if (dy <= dx * 0.05 && dx >= minHL) // Horizontal
                {
                    var y = (a.Y + b.Y) / 2.0;
                    var minX = Math.Min(a.X, b.X);
                    var maxX = Math.Max(a.X, b.X);
                    hLines.Add((y, minX, maxX, dx));
                }
                else if (dx <= dy * 0.05 && dy >= minVL) // Vertical
                {
                    var x = (a.X + b.X) / 2.0;
                    var minY = Math.Min(a.Y, b.Y);
                    var maxY = Math.Max(a.Y, b.Y);
                    vLines.Add((x, minY, maxY, dy));
                }
            }

            // Retângulos nativos: extrair 4 lados
            if (shape.Kind == ShapeKind.Rectangle && shape.Points.Count >= 2)
            {
                var b = shape.Bounds;
                if (b.Width > paperW * 0.3 && b.Height > paperH * 0.3)
                {
                    hLines.Add((b.MinY, b.MinX, b.MaxX, b.Width)); // Topo
                    hLines.Add((b.MaxY, b.MinX, b.MaxX, b.Width)); // Base
                    vLines.Add((b.MinX, b.MinY, b.MaxY, b.Height)); // Esquerda
                    vLines.Add((b.MaxX, b.MinY, b.MaxY, b.Height)); // Direita
                }
            }
        }
    }

    /// <summary>
    /// Encontra a borda externa mais próxima de uma borda do papel.
    /// Para linhas horizontais: topo (Y pequeno) ou base (Y grande).
    /// Para linhas verticais: esquerda (X pequeno) ou direita (X grande).
    /// </summary>
    private static (double Y, double MinX, double MaxX, double Length)? FindOuterBorderH(
        List<(double Y, double MinX, double MaxX, double Length)> lines,
        double paperH, bool isTop)
    {
        var lenThreshold = 0.55;

        var candidates = lines.Where(l =>
        {
            var normalizedPos = l.Y / paperH;
            var longEnough = l.Length / l.Length > lenThreshold; // relativo ao próprio comprimento
            return isTop
                ? normalizedPos < 0.18 && longEnough
                : normalizedPos > 0.70 && longEnough;
        }).ToList();

        if (candidates.Count == 0)
            return null;

        return isTop
            ? candidates.OrderBy(l => l.Y).First()
            : candidates.OrderByDescending(l => l.Y).First();
    }

    private static (double X, double MinY, double MaxY, double Length)? FindOuterBorderV(
        List<(double X, double MinY, double MaxY, double Length)> lines,
        double paperW, bool isLeft)
    {
        var lenThreshold = 0.55;

        var candidates = lines.Where(l =>
        {
            var normalizedPos = l.X / paperW;
            var longEnough = l.Length / l.Length > lenThreshold;
            return isLeft
                ? normalizedPos < 0.12 && longEnough
                : normalizedPos > 0.88 && longEnough;
        }).ToList();

        if (candidates.Count == 0)
            return null;

        return isLeft
            ? candidates.OrderBy(l => l.X).First()
            : candidates.OrderByDescending(l => l.X).First();
    }

    /// <summary>
    /// Encontra borda interna HORIZONTAL: linha que está dentro do range de inset da borda externa.
    /// </summary>
    private static double? FindInnerBorderH(
        List<(double Y, double MinX, double MaxX, double Length)> lines,
        double outerTopY, double outerBotY, double paperW,
        double minInset, double maxInset, bool isTop)
    {
        var minLenRatio = 0.50;

        var candidates = lines.Where(l =>
        {
            var inset = isTop ? l.Y - outerTopY : outerBotY - l.Y;
            var longEnough = l.Length / paperW > minLenRatio;
            return inset > minInset && inset < maxInset && longEnough;
        }).ToList();

        if (candidates.Count == 0)
            return null;

        return isTop
            ? candidates.OrderByDescending(l => l.Y).First().Y
            : candidates.OrderBy(l => l.Y).First().Y;
    }

    /// <summary>
    /// Encontra borda interna VERTICAL: linha que está dentro do range de inset da borda externa.
    /// </summary>
    private static double? FindInnerBorderV(
        List<(double X, double MinY, double MaxY, double Length)> lines,
        double outerLeftX, double outerRightX, double paperH,
        double minInset, double maxInset, bool isLeft)
    {
        var minLenRatio = 0.40;

        var candidates = lines.Where(l =>
        {
            var inset = isLeft ? l.X - outerLeftX : outerRightX - l.X;
            var longEnough = l.Length / paperH > minLenRatio;
            return inset > minInset && inset < maxInset && longEnough;
        }).ToList();

        if (candidates.Count == 0)
            return null;

        return isLeft
            ? candidates.OrderByDescending(l => l.X).First().X
            : candidates.OrderBy(l => l.X).First().X;
    }

    /// <summary>
    /// Fallback: tenta encontrar moldura a partir de retângulos nativos.
    /// </summary>
    private static Rect2D? DetectFrameFromRectangles(
        IReadOnlyList<ShapeItem> shapes, double paperW, double paperH)
    {
        var candidates = new List<Rect2D>();

        foreach (var shape in shapes)
        {
            if (shape.Kind != ShapeKind.Rectangle || shape.Points.Count < 2)
                continue;
            var b = shape.Bounds;
            if (b.Width > 0 && b.Height > 0)
                candidates.Add(b);
        }

        if (candidates.Count == 0)
            return null;

        var toleranceW = paperW * 0.15;
        var toleranceH = paperH * 0.15;

        // Moldura = retângulo que encosta nas 4 bordas do papel
        var frames = candidates
            .Where(r => r.MinX <= toleranceW && paperW - r.MaxX <= toleranceW &&
                        r.MinY <= toleranceH && paperH - r.MaxY <= toleranceH)
            .ToList();

        if (frames.Count == 0)
            return null;

        // Escolher o mais interno (folha da árvore de contenção)
        var leaves = new List<Rect2D>();
        for (var i = 0; i < frames.Count; i++)
        {
            var containsAnother = false;
            for (var j = 0; j < frames.Count; j++)
            {
                if (i == j) continue;
                if (Contains(frames[i], frames[j]))
                {
                    containsAnother = true;
                    break;
                }
            }
            if (!containsAnother)
                leaves.Add(frames[i]);
        }

        var chosen = (leaves.Count > 0 ? leaves : frames)
            .OrderByDescending(r => r.Width * r.Height)
            .First();

        const double inset = 0.2;
        var interior = new Rect2D(
            chosen.MinX + inset, chosen.MinY + inset,
            chosen.Width - 2 * inset, chosen.Height - 2 * inset);
        return interior.Width > 0 && interior.Height > 0 ? interior : null;
    }

    private static bool Contains(Rect2D outer, Rect2D inner)
    {
        const double eps = 0.5;
        return outer.MinX <= inner.MinX + eps && outer.MaxX >= inner.MaxX - eps &&
               outer.MinY <= inner.MinY + eps && outer.MaxY >= inner.MaxY - eps;
    }

    /// <summary>
    /// Heurística histórica: a moldura externa é o retângulo de maior área; a área útil é o
    /// bounds de todo o restante. Mantida como fallback quando o tamanho do papel é desconhecido.
    /// </summary>
    private static Rect2D? DetectByLargestRectangle(IReadOnlyList<ShapeItem> shapes)
    {
        var rectangles = shapes.Where(shape => shape.Kind == ShapeKind.Rectangle).ToList();
        if (rectangles.Count == 0)
            return null;

        ShapeItem? outerFrame = null;
        var maxArea = 0.0;
        foreach (var rectangle in rectangles)
        {
            var area = rectangle.Bounds.Width * rectangle.Bounds.Height;
            if (area > maxArea)
            {
                maxArea = area;
                outerFrame = rectangle;
            }
        }

        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        var any = false;
        foreach (var shape in shapes)
        {
            if (ReferenceEquals(shape, outerFrame))
                continue;
            var b = shape.Bounds;
            minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY);
            maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY);
            any = true;
        }

        return any ? new Rect2D(minX, minY, maxX - minX, maxY - minY) : null;
    }

    /// <summary>
    /// Remove retângulos que são "molduras" (cobrem quase toda a área do conteúdo).
    /// </summary>
    public static List<ShapeItem> DiscardOuterFrame(List<ShapeItem> shapes)
    {
        if (shapes.Count == 0)
            return shapes;

        var result = new List<ShapeItem>(shapes);

        for (int iteration = 0; iteration < 5; iteration++)
        {
            if (result.Count == 0)
                break;

            var totalMinX = double.MaxValue; var totalMinY = double.MaxValue;
            var totalMaxX = double.MinValue; var totalMaxY = double.MinValue;
            var any = false;
            foreach (var shape in result)
            {
                var b = shape.Bounds;
                if (b.Width <= 0 && b.Height <= 0) continue;
                totalMinX = Math.Min(totalMinX, b.MinX); totalMinY = Math.Min(totalMinY, b.MinY);
                totalMaxX = Math.Max(totalMaxX, b.MaxX); totalMaxY = Math.Max(totalMaxY, b.MaxY);
                any = true;
            }
            if (!any) break;
            var totalW = totalMaxX - totalMinX;
            var totalH = totalMaxY - totalMinY;
            if (totalW <= 0 || totalH <= 0) break;

            ShapeItem? frame = null;
            var frameArea = 0.0;
            foreach (var shape in result)
            {
                if (shape.Kind != ShapeKind.Rectangle) continue;
                var b = shape.Bounds;
                var area = b.Width * b.Height;
                if (area > frameArea)
                {
                    frameArea = area;
                    frame = shape;
                }
            }

            if (frame is null) break;

            var fb = frame.Bounds;
            var coverageW = fb.Width / totalW;
            var coverageH = fb.Height / totalH;

            if (coverageW >= 0.75 && coverageH >= 0.75)
            {
                Log($"  Moldura (iter {iteration}): {fb.Width:F1}x{fb.Height:F1}mm ({coverageW:P0}x{coverageH:P0})");
                result.RemoveAll(s => ReferenceEquals(s, frame));
            }
            else
                break;
        }

        return result;
    }

    private static void Log(string msg) => System.Diagnostics.Debug.WriteLine($"[WIZARD] {msg}");
}
