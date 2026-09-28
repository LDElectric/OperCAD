using OperCAD.Core.Conductors;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>
/// Área de transferência interna de objetos da folha (análoga à área de
/// transferência de esquema do padrão CAD): copia elementos selecionados (e condutores
/// entre eles) e cola criando novas instâncias com novos Ids.
/// </summary>
public sealed class DiagramClipboard
{
    private sealed class ElementCopy
    {
        public required ElementDefinition Definition { get; init; }
        public required string Label { get; init; }
        public required string Comment { get; init; }
        public required string Function { get; init; }
        public required string Description { get; init; }
        public required string Manufacturer { get; init; }
        public required string Designation { get; init; }
        public required double X { get; init; }
        public required double Y { get; init; }
        public required double Rotation { get; init; }
        public required Dictionary<int, string> TextOverrides { get; init; }
    }

    private sealed class ConductorCopy
    {
        public required int FromIndex { get; init; }
        public required int FromTerminal { get; init; }
        public required int ToIndex { get; init; }
        public required int ToTerminal { get; init; }
        public required string Color { get; init; }
        public required double Width { get; init; }
    }

    private sealed class ShapeCopy
    {
        public required ShapeKind Kind { get; init; }
        public required List<Point2D> Points { get; init; }
        public required List<(Point2D C1, Point2D C2)>? ControlPoints { get; init; }
        public required List<int>? ContourStarts { get; init; }
        public required string Color { get; init; }
        public required double PenWidth { get; init; }
        public required LineDashStyle DashStyle { get; init; }
        public required List<double> DashPattern { get; init; }
        public required double DashPhase { get; init; }
        public required ShapeLineCap LineCap { get; init; }
        public required ShapeLineJoin LineJoin { get; init; }
        public required string Filling { get; init; }
        public required bool Closed { get; init; }
        public required Guid? ImportGroupId { get; init; }
    }

    private sealed class TextCopy
    {
        public required string Text { get; init; }
        public required double X { get; init; }
        public required double Y { get; init; }
        public required double Rotation { get; init; }
        public required double FontSizePt { get; init; }
        public required string? FontFamily { get; init; }
        public required bool Bold { get; init; }
        public required bool Italic { get; init; }
        public required bool Underline { get; init; }
        public required bool Strikethrough { get; init; }
        public required string TextAlign { get; init; }
        public required string Color { get; init; }
        public required Guid? ImportGroupId { get; init; }
    }

    private readonly List<ElementCopy> _elements = new();
    private readonly List<ConductorCopy> _conductors = new();
    private readonly List<ShapeCopy> _shapes = new();
    private readonly List<TextCopy> _texts = new();

    /// <summary>
    /// Centroide do conjunto copiado — referência da colagem: o conjunto é deslocado por
    /// <c>target - Centroid</c> (translação pura), preservando a posição relativa entre os objetos.
    /// </summary>
    public Point2D Centroid { get; private set; }

    public bool HasContent => _elements.Count + _shapes.Count + _texts.Count > 0;

    /// <summary>Copia os elementos selecionados (e condutores entre eles) para a área de transferência.</summary>
    public void Copy(Page page)
    {
        _elements.Clear();
        _conductors.Clear();
        _shapes.Clear();
        _texts.Clear();

        var selected = page.Objects.OfType<PlacedElement>().Where(e => e.IsSelected).ToList();
        var selectedShapes = page.Objects.OfType<ShapeItem>().Where(s => s.IsSelected).ToList();
        var selectedTexts = page.Objects.OfType<TextItem>().Where(t => t.IsSelected).ToList();

        if (selected.Count == 0 && selectedShapes.Count == 0 && selectedTexts.Count == 0)
            return;

        // Centroid based on all selected objects
        var allPoints = new List<Point2D>();
        allPoints.AddRange(selected.Select(e => new Point2D(e.X, e.Y)));
        allPoints.AddRange(selectedShapes.SelectMany(s => s.Points));
        allPoints.AddRange(selectedTexts.Select(t => new Point2D(t.X, t.Y)));
        var cx = allPoints.Average(p => p.X);
        var cy = allPoints.Average(p => p.Y);
        Centroid = new Point2D(cx, cy);

        var indexById = new Dictionary<Guid, int>();
        for (var i = 0; i < selected.Count; i++)
        {
            var e = selected[i];
            indexById[e.Id] = i;
            _elements.Add(new ElementCopy
            {
                Definition = e.Definition,
                Label = e.Label,
                Comment = e.Comment,
                Function = e.Function,
                Description = e.Description,
                Manufacturer = e.Manufacturer,
                Designation = e.Designation,
                X = e.X,
                Y = e.Y,
                Rotation = e.Rotation,
                TextOverrides = new Dictionary<int, string>(e.TextOverrides)
            });
        }

        // condutores cujas duas pontas estão no conjunto copiado
        foreach (var obj in page.Objects.OfType<Conductor>())
        {
            if (indexById.TryGetValue(obj.From.ElementId, out var fi) &&
                indexById.TryGetValue(obj.To.ElementId, out var ti))
            {
                _conductors.Add(new ConductorCopy
                {
                    FromIndex = fi,
                    FromTerminal = obj.From.TerminalIndex,
                    ToIndex = ti,
                    ToTerminal = obj.To.TerminalIndex,
                    Color = obj.Color ?? "black",
                    Width = obj.Width
                });
            }
        }

        // shapes
        foreach (var s in selectedShapes)
        {
            // Forma sem pontos não tem geometria para copiar (e quebraria o cálculo do centroide).
            if (s.Points.Count == 0)
                continue;

            _shapes.Add(new ShapeCopy
            {
                Kind = s.Kind,
                Points = s.Points.Select(p => new Point2D(p.X, p.Y)).ToList(),
                ControlPoints = s.ControlPoints?.Select(cp => (
                    new Point2D(cp.C1.X, cp.C1.Y),
                    new Point2D(cp.C2.X, cp.C2.Y))).ToList(),
                Color = s.Color,
                PenWidth = s.PenWidth,
                DashStyle = s.DashStyle,
                DashPattern = s.DashPattern.ToList(),
                DashPhase = s.DashPhase,
                LineCap = s.LineCap,
                LineJoin = s.LineJoin,
                Filling = s.Filling,
                Closed = s.Closed,
                ContourStarts = s.SnapshotContourStarts(),
                ImportGroupId = s.ImportGroupId
            });
        }

        // texts
        foreach (var t in selectedTexts)
        {
            _texts.Add(new TextCopy
            {
                Text = t.Text,
                X = t.X,
                Y = t.Y,
                Rotation = t.Rotation,
                FontSizePt = t.FontSizePt,
                FontFamily = t.FontFamily,
                Bold = t.Bold,
                Italic = t.Italic,
                Underline = t.Underline,
                Strikethrough = t.Strikethrough,
                TextAlign = t.TextAlign,
                Color = t.Color,
                ImportGroupId = t.ImportGroupId
            });
        }
    }

    /// <summary>
    /// Cola o conteúdo copiado criando novos objetos, deslocando TODO o conjunto até
    /// <paramref name="target"/> (translação pura: a posição relativa entre os objetos copiados é
    /// preservada — é o que permite reutilizar um desenho do PDF, copiar/colar/girar/mover sem
    /// perder a forma com que os traços estão dispostos). Retorna os objetos criados.
    /// </summary>
    public List<DocumentObject> Paste(Page page, Point2D target)
    {
        var created = new List<DocumentObject>();
        if (!HasContent)
            return created;

        // Deslocamento ÚNICO para todo o conjunto.
        var offsetX = target.X - Centroid.X;
        var offsetY = target.Y - Centroid.Y;

        // Cada grupo copiado vira um grupo NOVO (instância independente), mantendo coeso o
        // conjunto colado — copiar um símbolo importado (ex.: K7.1) gera outro símbolo.
        var groupRemap = new Dictionary<Guid, Guid>();
        Guid? RemapGroup(Guid? oldGroupId)
        {
            if (oldGroupId is not { } oldId)
                return null;
            if (!groupRemap.TryGetValue(oldId, out var newId))
            {
                newId = Guid.NewGuid();
                groupRemap[oldId] = newId;
            }
            return newId;
        }

        var newElements = new List<PlacedElement>(_elements.Count);
        foreach (var copy in _elements)
        {
            var placed = new PlacedElement(copy.Definition)
            {
                Id = Guid.NewGuid(),
                X = copy.X + offsetX,
                Y = copy.Y + offsetY,
                Rotation = copy.Rotation,
                Label = copy.Label,
                Comment = copy.Comment,
                Function = copy.Function,
                Description = copy.Description,
                Manufacturer = copy.Manufacturer,
                Designation = copy.Designation
            };
            foreach (var kvp in copy.TextOverrides)
                placed.TextOverrides[kvp.Key] = kvp.Value;
            page.AddElement(placed);
            newElements.Add(placed);
            created.Add(placed);
        }

        foreach (var copy in _conductors)
        {
            if (copy.FromIndex < 0 || copy.FromIndex >= newElements.Count ||
                copy.ToIndex < 0 || copy.ToIndex >= newElements.Count)
                continue;
            var a = newElements[copy.FromIndex];
            var b = newElements[copy.ToIndex];
            var ta = a.Definition.Terminals[copy.FromTerminal];
            var tb = b.Definition.Terminals[copy.ToTerminal];
            var cond = new Conductor
            {
                Id = Guid.NewGuid(),
                From = new ElementTerminalRef(a.Id, copy.FromTerminal),
                To = new ElementTerminalRef(b.Id, copy.ToTerminal),
                Color = copy.Color,
                Width = copy.Width,
                Path = ConductorRouter.Generate(
                    TerminalGeometry.WorldPosition(a, ta), TerminalGeometry.WorldOrientation(a, ta),
                    TerminalGeometry.WorldPosition(b, tb), TerminalGeometry.WorldOrientation(b, tb))
            };
            page.Add(cond);
            created.Add(cond);
        }

        foreach (var copy in _shapes)
        {
            // Pontos e controles de Bézier deslocados pelo MESMO delta do conjunto.
            var newPoints = copy.Points
                .Select(p => new Point2D(p.X + offsetX, p.Y + offsetY))
                .ToList();

            List<(Point2D C1, Point2D C2)>? newControlPoints = null;
            if (copy.ControlPoints is { } cp)
            {
                newControlPoints = cp.Select(c => (
                    new Point2D(c.C1.X + offsetX, c.C1.Y + offsetY),
                    new Point2D(c.C2.X + offsetX, c.C2.Y + offsetY))).ToList();
            }

            var shape = new ShapeItem
            {
                Id = Guid.NewGuid(),
                Kind = copy.Kind,
                Points = newPoints,
                ControlPoints = newControlPoints,
                ContourStarts = copy.ContourStarts?.ToList(),
                Color = copy.Color,
                PenWidth = copy.PenWidth,
                DashStyle = copy.DashStyle,
                DashPattern = copy.DashPattern.ToList(),
                DashPhase = copy.DashPhase,
                LineCap = copy.LineCap,
                LineJoin = copy.LineJoin,
                Filling = copy.Filling,
                Closed = copy.Closed,
                ImportGroupId = RemapGroup(copy.ImportGroupId)
            };
            page.Add(shape);
            created.Add(shape);
        }

        foreach (var copy in _texts)
        {
            var text = new TextItem
            {
                Id = Guid.NewGuid(),
                Text = copy.Text,
                X = copy.X + offsetX,
                Y = copy.Y + offsetY,
                Rotation = copy.Rotation,
                FontSizePt = copy.FontSizePt,
                FontFamily = copy.FontFamily ?? new TextItem().FontFamily,
                Bold = copy.Bold,
                Italic = copy.Italic,
                Underline = copy.Underline,
                Strikethrough = copy.Strikethrough,
                TextAlign = copy.TextAlign,
                Color = copy.Color,
                ImportGroupId = RemapGroup(copy.ImportGroupId)
            };
            page.Add(text);
            created.Add(text);
        }

        return created;
    }

    public void Clear()
    {
        _elements.Clear();
        _conductors.Clear();
        _shapes.Clear();
        _texts.Clear();
    }
}
