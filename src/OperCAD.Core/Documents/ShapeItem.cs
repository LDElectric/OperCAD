using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>Tipo de forma livre da folha.</summary>
public enum ShapeKind { Line, Rectangle, Ellipse, Polygon, Triangle, Arc, EllipseArc, Curve, RoundedRectangle }

/// <summary>Estilo de traço (dash) de uma forma ou primitiva.</summary>
public enum LineDashStyle { Solid, Dashed, Dotted, DashDot }
public enum ShapeLineCap { Butt, Round, Square }
public enum ShapeLineJoin { Miter, Round, Bevel }

/// <summary>
/// Forma geométrica livre desenhada na folha (linha, retângulo, elipse ou
/// polilinha) — forma livre colocada no folio.
/// </summary>
public sealed class ShapeItem : DocumentObject
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;

    /// <summary>Pontos de definição: 2 pontos (linha/retângulo/elipse) ou N vértices (polilinha).</summary>
    public List<Point2D> Points { get; set; } = new();

    /// <summary>Cor da caneta (nome HTML ou hex).</summary>
    public string Color { get; set; } = "black";

    /// <summary>Espessura da caneta em mm (0.29 = padrão, mesma espessura do condutor de elementos).</summary>
    public double PenWidth { get; set; } = 0.29;

    /// <summary>Estilo de traço: sólido, tracejado, pontilhado ou traço-ponto.</summary>
    public LineDashStyle DashStyle { get; set; } = LineDashStyle.Solid;
    public List<double> DashPattern { get; set; } = new();
    public double DashPhase { get; set; }
    public ShapeLineCap LineCap { get; set; } = ShapeLineCap.Round;
    public ShapeLineJoin LineJoin { get; set; } = ShapeLineJoin.Round;

    /// <summary>Preenchimento: none | black | white | green | red | blue | yellow | gray.</summary>
    public string Filling { get; set; } = "none";

    /// <summary>Polilinha fechada (polígono) ou aberta.</summary>
    public bool Closed { get; set; }

    /// <summary>Ângulo inicial do arco em graus (0 = direita, sentido anti-horário).</summary>
    public double StartAngle { get; set; }

    /// <summary>Extensão do arco em graus (positivo = anti-horário).</summary>
    public double SweepAngle { get; set; } = 90;

    /// <summary>Raio X dos cantos arredondados (retângulo arredondado).</summary>
    public double Rx { get; set; }

    /// <summary>Raio Y dos cantos arredondados (retângulo arredondado).</summary>
    public double Ry { get; set; }

    /// <summary>
    /// Pontos de controle Bézier para curvas nativas. Cada entrada contém os dois pontos
    /// de controle (C1, C2) de um segmento cúbico. Quando não nulo, a renderização usa
    /// SKPath.CubicTo em vez de polilinha achatada. Null para formas sem curvas Bézier.
    /// </summary>
    public List<(Point2D C1, Point2D C2)>? ControlPoints { get; set; }

    /// <summary>
    /// Índices (em <see cref="Points"/>) onde começa cada CONTORNO, quando a forma tem mais de um.
    /// Um texto vetorizado é UM path com dezenas de subpaths (cada letra e os VAZIOS internos
    /// dela) — mantê-los no mesmo objeto é o que permite preencher preservando os furos.
    /// Contornos são independentes: NÃO se ligam por segmentos. Null/vazio = contorno único.
    /// </summary>
    public List<int>? ContourStarts { get; set; }

    public Guid? ImportGroupId { get; set; }

    /// <summary>
    /// Índice do OBJETO (path) do PDF que originou esta forma. Mesma ideia do editor de PDF:
    /// a identidade do objeto vem do arquivo, não de inferência geométrica. Permite selecionar
    /// de uma vez todos os subpaths de um mesmo path do PDF (um traço com várias partes).
    /// Null para formas nativas do OperCAD (não importadas).
    /// </summary>
    public int? PdfObjectIndex { get; set; }

    public bool IsLocked { get; set; }

    /// <summary>Retângulo envolvente dos pontos (para hit-test e seleção).</summary>
    public Rect2D Bounds
    {
        get
        {
            if (Points.Count == 0)
                return new Rect2D(0, 0, 0, 0);
            var minX = Points.Min(p => p.X);
            var maxX = Points.Max(p => p.X);
            var minY = Points.Min(p => p.Y);
            var maxY = Points.Max(p => p.Y);
            return new Rect2D(minX, minY, maxX - minX, maxY - minY);
        }
    }

    /// <summary>
    /// Desloca todos os pontos (mover). Inclui os pontos de controle Bézier — sem isso,
    /// curvas importadas de PDF deformam ao serem arrastadas (as âncoras andam, os
    /// controles ficavam para trás).
    /// </summary>
    public void Translate(double dx, double dy)
    {
        if (dx == 0 && dy == 0)
            return;
        for (var i = 0; i < Points.Count; i++)
            Points[i] = new Point2D(Points[i].X + dx, Points[i].Y + dy);
        if (ControlPoints is { } cp)
        {
            for (var i = 0; i < cp.Count; i++)
                cp[i] = (
                    new Point2D(cp[i].C1.X + dx, cp[i].C1.Y + dy),
                    new Point2D(cp[i].C2.X + dx, cp[i].C2.Y + dy));
        }
    }

    /// <summary>
    /// Gira âncoras E pontos de controle em torno de <paramref name="center"/>, e acompanha
    /// o ângulo dos arcos. Transformação rígida: não deforma o desenho.
    /// </summary>
    public void RotateAround(Point2D center, double angleDegrees)
    {
        if (angleDegrees == 0)
            return;
        var rad = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        Point2D Rotate(Point2D p)
        {
            var dx = p.X - center.X;
            var dy = p.Y - center.Y;
            return new Point2D(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
        }

        for (var i = 0; i < Points.Count; i++)
            Points[i] = Rotate(Points[i]);
        if (ControlPoints is { } cp)
        {
            for (var i = 0; i < cp.Count; i++)
                cp[i] = (Rotate(cp[i].C1), Rotate(cp[i].C2));
        }
        StartAngle = NormalizeAngle360(StartAngle + angleDegrees);
    }

    /// <summary>
    /// Espelha âncoras E pontos de controle. <paramref name="horizontal"/> = true espelha
    /// em torno de uma linha VERTICAL (inverte X). Arco tem o ângulo e a varredura refletidos.
    /// </summary>
    public void Mirror(double axis, bool horizontal)
    {
        Point2D Flip(Point2D p) => horizontal
            ? new Point2D(2 * axis - p.X, p.Y)
            : new Point2D(p.X, 2 * axis - p.Y);

        for (var i = 0; i < Points.Count; i++)
            Points[i] = Flip(Points[i]);
        if (ControlPoints is { } cp)
        {
            for (var i = 0; i < cp.Count; i++)
                cp[i] = (Flip(cp[i].C1), Flip(cp[i].C2));
        }
        StartAngle = NormalizeAngle360(horizontal ? 180.0 - StartAngle : -StartAngle);
        SweepAngle = -SweepAngle;
    }

    /// <summary>
    /// MUDANÇA DE ESCALA da forma em torno de <paramref name="center"/>, como o SCALE do CAD:
    /// escala âncoras, pontos de controle Bézier, raios dos cantos arredondados, ESPESSURA DA
    /// CANETA e padrão de tracejado. Diferente de arrastar uma ponta (que DEFORMA a peça), aqui
    /// o desenho continua igual — só maior ou menor.
    /// Os ângulos (arco) não mudam: ângulo é adimensional, escalar não os afeta.
    /// </summary>
    /// <returns>true se a geometria foi escalada (fator ≠ 1 e forma com pontos).</returns>
    public bool ScaleAround(Point2D center, double factor)
    {
        if (factor <= 0 || Math.Abs(factor - 1.0) < 1e-9)
            return false;
        if (Points.Count == 0)
            return false;

        Point2D Scale(Point2D p) => new(
            center.X + (p.X - center.X) * factor,
            center.Y + (p.Y - center.Y) * factor);

        for (var i = 0; i < Points.Count; i++)
            Points[i] = Scale(Points[i]);

        // Curvas Bézier: sem escalar os controles, o traço deforma (mesma razão do Translate).
        if (ControlPoints is { } cp)
        {
            for (var i = 0; i < cp.Count; i++)
                cp[i] = (Scale(cp[i].C1), Scale(cp[i].C2));
        }

        // Traçado PROPORCIONAL: a caneta acompanha a geometria. PenWidth = 0 significa
        // "caminho só PREENCHIDO" (glifos de texto vetorizado) e continua sem contorno.
        PenWidth *= factor;
        Rx *= factor;
        Ry *= factor;
        for (var i = 0; i < DashPattern.Count; i++)
            DashPattern[i] *= factor;
        DashPhase *= factor;
        return true;
    }

    private static double NormalizeAngle360(double angle)
        => (angle % 360.0 + 360.0) % 360.0;

    /// <summary>Clona os pontos (para comandos de undo).</summary>
    public List<Point2D> SnapshotPoints() => Points.ToList();

    /// <summary>
    /// Faixas [início, fim) de cada contorno. Sem quebras, devolve o objeto inteiro como um
    /// único contorno.
    /// </summary>
    public IEnumerable<(int Start, int End)> Contours()
    {
        var starts = ContourStarts;
        if (starts is not { Count: > 1 })
        {
            if (Points.Count > 0)
                yield return (0, Points.Count);
            yield break;
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var start = Math.Clamp(starts[i], 0, Points.Count);
            var end = i + 1 < starts.Count ? Math.Clamp(starts[i + 1], 0, Points.Count) : Points.Count;
            if (end > start)
                yield return (start, end);
        }
    }

    public List<int>? SnapshotContourStarts() => ContourStarts?.ToList();

    public void RestoreContourStarts(List<int>? starts)
        => ContourStarts = starts?.ToList();

    public void RestorePoints(IEnumerable<Point2D> points)
    {
        Points.Clear();
        Points.AddRange(points);
    }

    /// <summary>Clona os pontos de controle (para comandos de undo). Null se não houver curvas.</summary>
    public List<(Point2D C1, Point2D C2)>? SnapshotControlPoints()
        => ControlPoints?.ToList();

    public void RestoreControlPoints(List<(Point2D C1, Point2D C2)>? controlPoints)
    {
        ControlPoints = controlPoints?.ToList();
    }
}
