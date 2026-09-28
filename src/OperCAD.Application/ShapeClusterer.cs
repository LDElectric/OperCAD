using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>
/// Agrupa formas livres (ShapeItem) em clusters espaciais por proximidade.
/// Usa Union-Find para agrupar shapes cujos bounding boxes se tocam ou estão
/// dentro de um threshold de distância.
/// </summary>
public static class ShapeClusterer
{
    /// <summary>
    /// Agrupa shapes e textos de uma página em clusters espaciais.
    /// </summary>
    /// <param name="shapes">Formas livres da página.</param>
    /// <param name="texts">Textos livres da página (opcional, associados por proximidade).</param>
    /// <param name="gapThreshold">Distância máxima (mm) entre bounding boxes para considerar proximidade.</param>
    public static List<ShapeCluster> Cluster(
        IReadOnlyList<ShapeItem> shapes,
        IReadOnlyList<TextItem>? texts = null,
        double gapThreshold = 5.0)
    {
        if (shapes.Count == 0)
            return new List<ShapeCluster>();

        var n = shapes.Count;
        var uf = new UnionFind(n);

        // Union-Find: agrupar shapes com bounding boxes próximos
        var bounds = new Rect2D[n];
        for (var i = 0; i < n; i++)
        {
            bounds[i] = shapes[i].Bounds;
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (BboxesClose(bounds[i], bounds[j], gapThreshold))
                    uf.Union(i, j);
            }
        }

        // Agrupar por raiz do Union-Find
        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var root = uf.Find(i);
            if (!groups.ContainsKey(root))
                groups[root] = new List<int>();
            groups[root].Add(i);
        }

        // Construir clusters
        var clusters = new List<ShapeCluster>();
        foreach (var group in groups.Values)
        {
            var clusterShapes = group.Select(i => shapes[i]).ToList();
            var clusterBounds = ComputeBounds(clusterShapes);

            // Associar textos que estejam dentro ou próximos ao bounding box do cluster
            var clusterTexts = new List<TextItem>();
            if (texts is { Count: > 0 })
            {
                var inflatedBounds = clusterBounds.Inflate(gapThreshold);
                foreach (var text in texts)
                {
                    if (inflatedBounds.Contains(new Point2D(text.X, text.Y)))
                        clusterTexts.Add(text);
                }
            }

            clusters.Add(new ShapeCluster
            {
                Shapes = clusterShapes,
                Texts = clusterTexts,
                Bounds = clusterBounds
            });
        }

        return clusters;
    }

    /// <summary>
    /// Verifica se dois bounding boxes estão próximos (distância entre bordas ≤ threshold).
    /// </summary>
    private static bool BboxesClose(Rect2D a, Rect2D b, double threshold)
    {
        // Se se tocam ou sobrepõem, estão próximos
        if (a.MinX <= b.MaxX + threshold && a.MaxX >= b.MinX - threshold &&
            a.MinY <= b.MaxY + threshold && a.MaxY >= b.MinY - threshold)
            return true;

        return false;
    }

    /// <summary>
    /// Calcula o bounding box que contém todas as formas do cluster.
    /// </summary>
    private static Rect2D ComputeBounds(List<ShapeItem> shapes)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var shape in shapes)
        {
            var b = shape.Bounds;
            if (b.Width <= 0 && b.Height <= 0 && shape.Points.Count == 0)
                continue;
            minX = Math.Min(minX, b.MinX);
            minY = Math.Min(minY, b.MinY);
            maxX = Math.Max(maxX, b.MaxX);
            maxY = Math.Max(maxY, b.MaxY);
        }

        if (minX > maxX || minY > maxY)
            return new Rect2D(0, 0, 0, 0);

        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Segmenta uma folha importada de PDF em SÍMBOLOS (1 cluster = 1 símbolo). Diferente de
    /// <see cref="Cluster"/>, corta os FIOS longos que ligariam símbolos vizinhos num bloco
    /// único — essencial em esquemas elétricos, onde tudo está conectado por linhas. Usa
    /// componentes conectadas por proximidade com índice espacial (não O(n²)) e associa cada
    /// texto ao componente que o contém.
    /// </summary>
    /// <param name="shapes">Formas da folha (já recortada/transformada).</param>
    /// <param name="texts">Textos da folha.</param>
    /// <param name="gapThreshold">Distância máxima (mm) entre bounding boxes para unir num símbolo.</param>
    /// <param name="wireMinLengthMm">Comprimento (mm) a partir do qual um traço fino é tratado como FIO.</param>
    /// <param name="wireMaxThicknessMm">Espessura máxima (mm) do bbox de um traço para ser fio.</param>
    /// <param name="maxSymbolWidthMm">Componentes mais largos que isto são descartados (ruído/página).</param>
    /// <param name="maxSymbolHeightMm">Componentes mais altos que isto são descartados (ruído/página).</param>
    /// <param name="minShapesPerSymbol">Mínimo de primitivas para formar um símbolo agrupável.</param>
    public static List<ShapeCluster> SegmentSymbols(
        IReadOnlyList<ShapeItem> shapes,
        IReadOnlyList<TextItem>? texts = null,
        double gapThreshold = 1.5,
        double wireMinLengthMm = 15.0,
        double wireMaxThicknessMm = 0.8,
        double maxSymbolWidthMm = 210.0,
        double maxSymbolHeightMm = 150.0,
        int minShapesPerSymbol = 2,
        double textLikeMaxSizeMm = 3.5,
        double glyphAttachMm = 3.0)
    {
        var result = new List<ShapeCluster>();
        if (shapes.Count == 0)
            return result;

        // Separa em três classes:
        //  - GLIFO (texto vetorizado): forma pequena — não forma "símbolo" sozinha; é
        //    anexada à geometria mais próxima, para a etiqueta viajar junto com o símbolo.
        //  - FIO: traço fino e longo — cortado (senão tudo vira um único componente).
        //  - SÍMBOLO: o restante, base dos componentes.
        var indices = new List<int>(shapes.Count);
        var glyphIndices = new List<int>();
        for (var i = 0; i < shapes.Count; i++)
        {
            var b = shapes[i].Bounds;
            var maxDim = Math.Max(b.Width, b.Height);
            if (maxDim > 0 && maxDim <= textLikeMaxSizeMm)
                glyphIndices.Add(i);
            else if (!IsWireLike(shapes[i], wireMinLengthMm, wireMaxThicknessMm))
                indices.Add(i);
        }

        if (indices.Count < minShapesPerSymbol)
            return result;

        var n = indices.Count;
        var uf = new UnionFind(n);
        var bounds = new Rect2D[n];
        for (var i = 0; i < n; i++)
            bounds[i] = shapes[indices[i]].Bounds;

        // Índice espacial em grade — evita O(n²) em páginas densas (milhares de formas).
        var cell = Math.Max(gapThreshold * 4, 4.0);
        var grid = new Dictionary<(int X, int Y), List<int>>();
        for (var i = 0; i < n; i++)
        {
            var key = CellOf(bounds[i], cell);
            if (!grid.TryGetValue(key, out var cellItems))
            {
                cellItems = new List<int>();
                grid[key] = cellItems;
            }
            cellItems.Add(i);
        }

        for (var i = 0; i < n; i++)
        {
            var (cx, cy) = CellOf(bounds[i], cell);
            for (var ox = -1; ox <= 1; ox++)
            for (var oy = -1; oy <= 1; oy++)
            {
                if (!grid.TryGetValue((cx + ox, cy + oy), out var neighbours))
                    continue;
                foreach (var j in neighbours)
                {
                    if (j <= i)
                        continue;
                    if (BboxesClose(bounds[i], bounds[j], gapThreshold))
                        uf.Union(i, j);
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var root = uf.Find(i);
            if (!groups.TryGetValue(root, out var groupItems))
            {
                groupItems = new List<int>();
                groups[root] = groupItems;
            }
            groupItems.Add(i);
        }

        var attachedGlyphs = new HashSet<int>();
        foreach (var group in groups.Values)
        {
            if (group.Count < minShapesPerSymbol)
                continue;

            var clusterShapes = group.Select(i => shapes[indices[i]]).ToList();
            var clusterBounds = ComputeBounds(clusterShapes);

            // Anexa os glifos (etiquetas de texto vetorizado) que caem em volta do símbolo:
            // assim o rótulo "K7.1" acompanha o símbolo ao copiar/mover/girar.
            var attachArea = clusterBounds.Inflate(glyphAttachMm);
            foreach (var gi in glyphIndices)
            {
                if (attachedGlyphs.Contains(gi))
                    continue;
                var gb = shapes[gi].Bounds;
                if (attachArea.Contains(new Point2D(gb.CenterX, gb.CenterY)))
                {
                    clusterShapes.Add(shapes[gi]);
                    attachedGlyphs.Add(gi);
                }
            }

            clusterBounds = ComputeBounds(clusterShapes);

            // Componente que cobre a folha toda não é símbolo — deixamos sem grupo.
            if (clusterBounds.Width > maxSymbolWidthMm && clusterBounds.Height > maxSymbolHeightMm)
                continue;

            var clusterTexts = new List<TextItem>();
            if (texts is { Count: > 0 })
            {
                var inflated = clusterBounds.Inflate(gapThreshold);
                foreach (var text in texts)
                    if (inflated.Contains(new Point2D(text.X, text.Y)))
                        clusterTexts.Add(text);
            }

            result.Add(new ShapeCluster
            {
                Shapes = clusterShapes,
                Texts = clusterTexts,
                Bounds = clusterBounds
            });
        }

        return result;
    }

    private static (int X, int Y) CellOf(Rect2D bounds, double cell)
        => ((int)Math.Floor(bounds.CenterX / cell), (int)Math.Floor(bounds.CenterY / cell));

    /// <summary>
    /// Traço fino e longo = FIO de ligação (não pertence ao símbolo). Símbolos são compactos;
    /// traços curtos internos (barra de contato, conexão curta) permanecem no símbolo.
    /// </summary>
    private static bool IsWireLike(ShapeItem shape, double minLengthMm, double maxThicknessMm)
    {
        var b = shape.Bounds;
        if (b.Width <= 0 && b.Height <= 0)
            return false;
        if (Math.Min(b.Width, b.Height) > maxThicknessMm)
            return false;
        return Math.Max(b.Width, b.Height) >= minLengthMm;
    }

    /// <summary>
    /// Marca quais formas são TEXTO VETORIZADO (glifos), decidindo por CONTEXTO: uma forma
    /// pequena cercada de MUITAS outras formas pequenas (um texto tem dezenas de traços curtos
    /// num espaço pequeno). Decidir pelo tamanho da forma isolada é errado — depois de dividir o
    /// desenho em segmentos, um traço curto de desenho também é pequeno, e acabava afinado,
    /// deixando a espessura do desenho inconsistente.
    /// </summary>
    public static HashSet<Guid> FindTextLikeShapes(
        IReadOnlyList<ShapeItem> shapes,
        double maxGlyphSizeMm = 3.5,
        double radiusMm = 4.0,
        int minNeighbours = 6)
    {
        var result = new HashSet<Guid>();
        if (shapes.Count == 0)
            return result;

        // Só formas pequenas concorrem a glifo.
        var small = new List<ShapeItem>();
        foreach (var shape in shapes)
        {
            var b = shape.Bounds;
            var maxDim = Math.Max(b.Width, b.Height);
            if (maxDim > 0 && maxDim <= maxGlyphSizeMm)
                small.Add(shape);
        }
        if (small.Count < minNeighbours)
            return result;

        // Grade espacial para contar vizinhos sem O(n²).
        var cell = Math.Max(radiusMm, 1.0);
        var grid = new Dictionary<(int X, int Y), List<int>>();
        var centers = new Point2D[small.Count];
        for (var i = 0; i < small.Count; i++)
        {
            var b = small[i].Bounds;
            centers[i] = new Point2D(b.CenterX, b.CenterY);
            var key = ((int)Math.Floor(b.CenterX / cell), (int)Math.Floor(b.CenterY / cell));
            if (!grid.TryGetValue(key, out var bucket))
            {
                bucket = new List<int>();
                grid[key] = bucket;
            }
            bucket.Add(i);
        }

        var radiusSq = radiusMm * radiusMm;
        for (var i = 0; i < small.Count; i++)
        {
            var (cx, cy) = ((int)Math.Floor(centers[i].X / cell), (int)Math.Floor(centers[i].Y / cell));
            var neighbours = 0;
            for (var ox = -1; ox <= 1; ox++)
            {
                for (var oy = -1; oy <= 1; oy++)
                {
                    if (!grid.TryGetValue((cx + ox, cy + oy), out var bucket))
                        continue;
                    foreach (var j in bucket)
                    {
                        if (j == i)
                            continue;
                        var dx = centers[j].X - centers[i].X;
                        var dy = centers[j].Y - centers[i].Y;
                        if (dx * dx + dy * dy <= radiusSq)
                        {
                            neighbours++;
                            if (neighbours >= minNeighbours)
                                break;
                        }
                    }
                    if (neighbours >= minNeighbours)
                        break;
                }
                if (neighbours >= minNeighbours)
                    break;
            }
            if (neighbours >= minNeighbours)
                result.Add(small[i].Id);
        }

        return result;
    }

    /// <summary>Implementação eficiente de Union-Find (com path compression e union by rank).</summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public UnionFind(int n)
        {
            _parent = new int[n];
            _rank = new int[n];
            for (var i = 0; i < n; i++)
                _parent[i] = i;
        }

        public int Find(int x)
        {
            if (_parent[x] != x)
                _parent[x] = Find(_parent[x]);
            return _parent[x];
        }

        public void Union(int x, int y)
        {
            var rx = Find(x);
            var ry = Find(y);
            if (rx == ry) return;

            if (_rank[rx] < _rank[ry])
            {
                _parent[rx] = ry;
            }
            else
            {
                _parent[ry] = rx;
                if (_rank[rx] == _rank[ry])
                    _rank[rx]++;
            }
        }
    }
}
