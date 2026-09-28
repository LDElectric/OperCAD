using OperCAD.Core.Geometry;

namespace OperCAD.Core.Conductors;

/// <summary>
/// Roteamento ortogonal de condutores, portado de <c>Conductor::generateConductorPath</c>
/// do padrão CAD. Os pontos são em mm no mundo do OperCAD (y para cima); o algoritmo
/// roda internamente no espaço padrão CAD (y para baixo) e converte o resultado de volta.
/// Orientação dos terminais: 'n', 'e', 's', 'w'.
/// </summary>
public static class ConductorRouter
{
    /// <summary>Espaçamento padrão do grid (mm) — usado quando nenhum valor é informado.</summary>
    public const double DefaultGrid = 10.0;

    /// <summary>
    /// Gera o caminho (lista de pontos em mm, y para cima) entre dois terminais.
    /// </summary>
    /// <param name="p1">Posição do terminal inicial.</param>
    /// <param name="o1">Orientação do terminal inicial ('n'/'e'/'s'/'w').</param>
    /// <param name="p2">Posição do terminal final.</param>
    /// <param name="o2">Orientação do terminal final ('n'/'e'/'s'/'w').</param>
    /// <param name="gridSpacing">Espaçamento do grid em mm (padrão: 10).</param>
    public static List<Point2D> Generate(Point2D p1, char o1, Point2D p2, char o2,
        double gridSpacing = DefaultGrid)
    {
        var grid = Math.Max(gridSpacing, 0.1);

        // converte para o espaço padrão CAD (y para baixo)
        var sp1 = new Point2D(p1.X, -p1.Y);
        var sp2 = new Point2D(p2.X, -p2.Y);

        var newp1 = ExtendTerminal(sp1, o1, grid);
        var newp2 = ExtendTerminal(sp2, o2, grid);

        Point2D depart, arrivee, depart0, arrivee0;
        char oriD, oriA;
        if (newp1.X <= newp2.X)
        {
            depart = newp1; arrivee = newp2; depart0 = sp1; arrivee0 = sp2; oriD = o1; oriA = o2;
        }
        else
        {
            depart = newp2; arrivee = newp1; depart0 = sp2; arrivee0 = sp1; oriD = o2; oriA = o1;
        }

        var points = new List<Point2D> { depart0, depart };

        bool N(char o) => o == 'n';
        bool S(char o) => o == 's';
        bool E(char o) => o == 'e';
        bool W(char o) => o == 'w';

        if (depart.Y < arrivee.Y)
        {
            // trajeto descendente (no espaço padrão CAD)
            if ((N(oriD) && (S(oriA) || W(oriA))) || (E(oriD) && W(oriA)))
            {
                var lx = SnapDown((depart.X + arrivee.X) / 2, grid);
                points.Add(new Point2D(lx, depart.Y));
                points.Add(new Point2D(lx, arrivee.Y));
            }
            else if ((S(oriD) && (N(oriA) || E(oriA))) || (W(oriD) && E(oriA)))
            {
                var ly = SnapDown((depart.Y + arrivee.Y) / 2, grid);
                points.Add(new Point2D(depart.X, ly));
                points.Add(new Point2D(arrivee.X, ly));
            }
            else if ((N(oriD) || E(oriD)) && (N(oriA) || E(oriA)))
            {
                points.Add(new Point2D(arrivee.X, depart.Y)); // caso 2
            }
            else
            {
                points.Add(new Point2D(depart.X, arrivee.Y)); // caso 1
            }
        }
        else
        {
            // trajeto ascendente
            if ((W(oriD) && (E(oriA) || S(oriA))) || (N(oriD) && S(oriA)))
            {
                var ly = SnapDown((depart.Y + arrivee.Y) / 2, grid);
                points.Add(new Point2D(depart.X, ly));
                points.Add(new Point2D(arrivee.X, ly));
            }
            else if ((E(oriD) && (W(oriA) || N(oriA))) || (S(oriD) && N(oriA)))
            {
                var lx = SnapDown((depart.X + arrivee.X) / 2, grid);
                points.Add(new Point2D(lx, depart.Y));
                points.Add(new Point2D(lx, arrivee.Y));
            }
            else if ((W(oriD) || N(oriD)) && (W(oriA) || N(oriA)))
            {
                points.Add(new Point2D(depart.X, arrivee.Y)); // caso 2
            }
            else
            {
                points.Add(new Point2D(arrivee.X, depart.Y)); // caso 1
            }
        }

        points.Add(arrivee);
        points.Add(arrivee0);

        if (newp1.X > newp2.X)
            points.Reverse();

        // converte de volta para o mundo (y para cima)
        var result = new List<Point2D>(points.Count);
        foreach (var p in points)
            result.Add(new Point2D(p.X, -p.Y));
        return result;
    }

    /// <summary>Prolonga o terminal em <paramref name="grid"/> mm, arredondado ao grid.</summary>
    private static Point2D ExtendTerminal(Point2D t, char o, double grid)
    {
        return o switch
        {
            'n' => new Point2D(t.X, RoundGrid(t.Y - grid, grid)),
            'e' => new Point2D(RoundGrid(t.X + grid, grid), t.Y),
            's' => new Point2D(t.X, RoundGrid(t.Y + grid, grid)),
            'w' => new Point2D(RoundGrid(t.X - grid, grid), t.Y),
            _ => t
        };
    }

    /// <summary>
    /// Gera o caminho (em mm, y para cima) de um condutor desenhado à mão (rota livre
    /// multi-segmentos): parte do ponto inicial, passa pelos pontos intermediários
    /// clicados pelo usuário e termina no ponto final. Cada clique do usuário é um
    /// vértice exato — sem extensões automáticas de terminal e sem cotovelos intermediários.
    /// </summary>
    public static List<Point2D> GenerateFreeRoute(
        Point2D start, char? startOrient,
        IReadOnlyList<Point2D> intermediates,
        Point2D end, char? endOrient,
        double gridSpacing = DefaultGrid)
    {
        var pts = new List<Point2D>();
        AppendPoint(pts, start);

        foreach (var ip in intermediates)
            AppendPoint(pts, ip);

        AppendPoint(pts, end);

        return Dedupe(pts);
    }

    /// <summary>Ponto <paramref name="grid"/> mm a partir de um terminal na direção da orientação (mundo, y para cima).</summary>
    public static Point2D ExtendWorld(Point2D t, char o, double grid = DefaultGrid) => o switch
    {
        'n' => new Point2D(t.X, t.Y + grid),
        'e' => new Point2D(t.X + grid, t.Y),
        's' => new Point2D(t.X, t.Y - grid),
        'w' => new Point2D(t.X - grid, t.Y),
        _ => t
    };

    /// <summary>Acrescenta um cotovelo (L) ortogonal para ir de <paramref name="a"/> até <paramref name="b"/>.</summary>
    private static void AppendOrthogonal(List<Point2D> pts, Point2D a, Point2D b)
    {
        if (a == b)
            return;
        if (a.X == b.X || a.Y == b.Y)
        {
            AppendPoint(pts, b);
            return;
        }
        AppendPoint(pts, new Point2D(b.X, a.Y)); // horizontal primeiro, depois vertical
        AppendPoint(pts, b);
    }

    private static void AppendPoint(List<Point2D> pts, Point2D p)
    {
        if (pts.Count == 0 || pts[^1] != p)
            pts.Add(p);
    }

    private static List<Point2D> Dedupe(List<Point2D> pts)
    {
        var result = new List<Point2D>(pts.Count);
        foreach (var p in pts)
            AppendPoint(result, p);
        return result;
    }

    private static double RoundGrid(double v, double grid) => Math.Round(v / grid) * grid;

    private static double SnapDown(double v, double grid) => Math.Floor(v / grid) * grid;
}
