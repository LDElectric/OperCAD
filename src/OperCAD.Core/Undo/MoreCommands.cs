using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Undo;

/// <summary>
/// Move objetos de qualquer tipo (elemento, texto, forma, imagem) — cada tipo
/// registra como aplicar/restaurar a posição. Condutores são recalculados.
/// </summary>
public sealed class MoveObjectsCommand : IUndoableCommand
{
    private sealed record Entry(
        DocumentObject Object,
        double OldX, double OldY,
        List<Point2D> OldPoints, // formas
        double NewX, double NewY,
        List<Point2D> NewPoints,
        List<(Point2D C1, Point2D C2)>? OldControlPoints, // curvas Bézier
        List<(Point2D C1, Point2D C2)>? NewControlPoints);

    private readonly Page _page;
    private readonly List<Entry> _entries;

    public string Description { get; }

    public MoveObjectsCommand(Page page,
        IEnumerable<(DocumentObject Object, double OldX, double OldY, List<Point2D> OldPoints,
                     double NewX, double NewY, List<Point2D> NewPoints,
                     List<(Point2D C1, Point2D C2)>? OldControlPoints,
                     List<(Point2D C1, Point2D C2)>? NewControlPoints)> moves,
        string description)
    {
        _page = page;
        _entries = moves.Select(m => new Entry(
            m.Object, m.OldX, m.OldY, m.OldPoints, m.NewX, m.NewY, m.NewPoints,
            m.OldControlPoints, m.NewControlPoints)).ToList();
        Description = description;
    }

    private static void ApplyTo(DocumentObject obj, double x, double y, List<Point2D> points,
        List<(Point2D C1, Point2D C2)>? controlPoints)
    {
        switch (obj)
        {
            case PlacedElement el:
                el.X = x;
                el.Y = y;
                break;
            case TextItem t:
                t.X = x;
                t.Y = y;
                break;
            case ImageItem img:
                img.X = x;
                img.Y = y;
                break;
            case TerminalStripItem stripItem:
                stripItem.X = x;
                stripItem.Y = y;
                break;
            case ShapeItem shape:
                shape.RestorePoints(points);
                shape.RestoreControlPoints(controlPoints);
                break;
        }
    }

    private void Apply(bool toNew)
    {
        foreach (var e in _entries)
        {
            ApplyTo(e.Object,
                toNew ? e.NewX : e.OldX,
                toNew ? e.NewY : e.OldY,
                toNew ? e.NewPoints : e.OldPoints,
                toNew ? e.NewControlPoints : e.OldControlPoints);
        }
        _page.RecomputeConductors();
    }

    public void Undo() => Apply(false);

    public void Redo() => Apply(true);
}

/// <summary>Altera a ordem de empilhamento (z-order) de um objeto na folha.</summary>
public sealed class ZOrderCommand : IUndoableCommand
{
    private readonly Page _page;
    private readonly DocumentObject _object;
    private readonly int _oldIndex;
    private readonly int _newIndex;

    public string Description { get; }

    public ZOrderCommand(Page page, DocumentObject obj, int oldIndex, int newIndex, string description)
    {
        _page = page;
        _object = obj;
        _oldIndex = oldIndex;
        _newIndex = newIndex;
        Description = description;
    }

    private void MoveTo(int index)
    {
        if (_page.Objects.Remove(_object))
            _page.Objects.Insert(Math.Clamp(index, 0, _page.Objects.Count), _object);
    }

    public void Undo() => MoveTo(_oldIndex);

    public void Redo() => MoveTo(_newIndex);
}

/// <summary>Snapshot das propriedades editáveis de um condutor.</summary>
public sealed record ConductorInfoSnapshot(
    string Color, double Width, string Text, int SequenceNumber,
    string Cable, string Function, string Bus, string TensionProtocol,
    string Section, string TextColor, int NumSize, bool DisplayText, string Formula)
{
    public static ConductorInfoSnapshot Of(Conductor c) => new(
        c.Color ?? "black", c.Width, c.Text, c.SequenceNumber,
        c.Cable, c.Function, c.Bus, c.TensionProtocol,
        c.Section, c.TextColor, c.NumSize, c.DisplayText, c.Formula);

    public void Apply(Conductor c)
    {
        c.Color = Color;
        c.Width = Width;
        c.Text = Text;
        c.SequenceNumber = SequenceNumber;
        c.Cable = Cable;
        c.Function = Function;
        c.Bus = Bus;
        c.TensionProtocol = TensionProtocol;
        c.Section = Section;
        c.TextColor = TextColor;
        c.NumSize = NumSize;
        c.DisplayText = DisplayText;
        c.Formula = Formula;
    }
}

/// <summary>Altera as propriedades de um condutor (painel de propriedades).</summary>
public sealed class ChangeConductorPropertiesCommand : IUndoableCommand
{
    private readonly Conductor _conductor;
    private readonly ConductorInfoSnapshot _before;
    private readonly ConductorInfoSnapshot _after;

    public string Description => "Propriedades do condutor";

    public ChangeConductorPropertiesCommand(Conductor conductor, ConductorInfoSnapshot before, ConductorInfoSnapshot after)
    {
        _conductor = conductor;
        _before = before;
        _after = after;
    }

    public void Undo() => _before.Apply(_conductor);

    public void Redo() => _after.Apply(_conductor);
}

/// <summary>Snapshot das propriedades editáveis de um texto independente.</summary>
public sealed record TextItemSnapshot(
    string Text, double X, double Y, double Rotation, string FontFamily, double FontSizePt,
    bool Bold, bool Italic, bool Underline, bool Strikethrough, string Color, string TextAlign)
{
    public static TextItemSnapshot Of(TextItem t) => new(
        t.Text, t.X, t.Y, t.Rotation, t.FontFamily, t.FontSizePt,
        t.Bold, t.Italic, t.Underline, t.Strikethrough, t.Color, t.TextAlign);

    public void Apply(TextItem t)
    {
        t.Text = Text;
        t.X = X;
        t.Y = Y;
        t.Rotation = Rotation;
        t.FontFamily = FontFamily;
        t.FontSizePt = FontSizePt;
        t.Bold = Bold;
        t.Italic = Italic;
        t.Underline = Underline;
        t.Strikethrough = Strikethrough;
        t.Color = Color;
        t.TextAlign = TextAlign;
    }
}

/// <summary>Altera as propriedades de um texto independente.</summary>
public sealed class ChangeTextItemPropertiesCommand : IUndoableCommand
{
    private readonly TextItem _text;
    private readonly TextItemSnapshot _before;
    private readonly TextItemSnapshot _after;

    public string Description => "Propriedades do texto";

    public ChangeTextItemPropertiesCommand(TextItem text, TextItemSnapshot before, TextItemSnapshot after)
    {
        _text = text;
        _before = before;
        _after = after;
    }

    public void Undo() => _before.Apply(_text);

    public void Redo() => _after.Apply(_text);
}

/// <summary>Snapshot das propriedades editáveis de uma forma.</summary>
public sealed record ShapeItemSnapshot(
    ShapeKind Kind, List<Point2D> Points, string Color, double PenWidth, LineDashStyle DashStyle,
    List<double> DashPattern, double DashPhase, ShapeLineCap LineCap, ShapeLineJoin LineJoin,
    string Filling, bool Closed, double StartAngle, double SweepAngle, double Rx, double Ry,
    List<(Point2D C1, Point2D C2)>? ControlPoints = null,
    List<int>? ContourStarts = null)
{
    /// <summary>
    /// Captura o estado da forma. <paramref name="pointsOverride"/> permite guardar pontos
    /// anteriores à transformação (undo); os pontos de controle e os contornos são capturados
    /// do estado atual, pois transformações rígidas não os alteram.
    /// </summary>
    public static ShapeItemSnapshot Of(ShapeItem s, IEnumerable<Point2D>? pointsOverride = null) => new(
        s.Kind, pointsOverride is null ? s.SnapshotPoints() : pointsOverride.ToList(),
        s.Color, s.PenWidth, s.DashStyle, s.DashPattern.ToList(),
        s.DashPhase, s.LineCap, s.LineJoin, s.Filling, s.Closed, s.StartAngle, s.SweepAngle, s.Rx, s.Ry,
        s.SnapshotControlPoints(),
        s.SnapshotContourStarts());

    public void Apply(ShapeItem s)
    {
        s.Kind = Kind;
        s.RestorePoints(Points);
        s.Color = Color;
        s.PenWidth = PenWidth;
        s.DashStyle = DashStyle;
        s.DashPattern = DashPattern.ToList();
        s.DashPhase = DashPhase;
        s.LineCap = LineCap;
        s.LineJoin = LineJoin;
        s.Filling = Filling;
        s.Closed = Closed;
        s.StartAngle = StartAngle;
        s.SweepAngle = SweepAngle;
        s.Rx = Rx;
        s.Ry = Ry;
        s.RestoreControlPoints(ControlPoints);
        s.RestoreContourStarts(ContourStarts);
    }
}

/// <summary>Altera as propriedades de uma forma.</summary>
public sealed class ChangeShapePropertiesCommand : IUndoableCommand
{
    private readonly ShapeItem _shape;
    private readonly ShapeItemSnapshot _before;
    private readonly ShapeItemSnapshot _after;

    public string Description => "Propriedades da forma";

    public ChangeShapePropertiesCommand(ShapeItem shape, ShapeItemSnapshot before, ShapeItemSnapshot after)
    {
        _shape = shape;
        _before = before;
        _after = after;
    }

    public void Undo() => _before.Apply(_shape);

    public void Redo() => _after.Apply(_shape);
}

/// <summary>Altera o Path (rota) de um condutor (edição de vértices).</summary>
public sealed class ChangeConductorPathCommand : IUndoableCommand
{
    private readonly Conductor _conductor;
    private readonly List<Point2D> _oldPath;
    private readonly List<Point2D> _newPath;

    public string Description => "Editar rota do condutor";

    public ChangeConductorPathCommand(Conductor conductor, List<Point2D> oldPath, List<Point2D> newPath)
    {
        _conductor = conductor;
        _oldPath = oldPath;
        _newPath = newPath;
    }

    public void Undo()
    {
        _conductor.Path.Clear();
        _conductor.Path.AddRange(_oldPath);
    }

    public void Redo()
    {
        _conductor.Path.Clear();
        _conductor.Path.AddRange(_newPath);
    }
}

/// <summary>Grupo de comandos aplicados como uma única operação (substituir em massa etc.).</summary>
public sealed class CompositeCommand : IUndoableCommand
{
    private readonly IReadOnlyList<IUndoableCommand> _commands;

    public string Description { get; }

    public CompositeCommand(string description, IEnumerable<IUndoableCommand> commands)
    {
        Description = description;
        _commands = commands.ToList();
    }

    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Undo();
    }

    public void Redo()
    {
        foreach (var c in _commands)
            c.Redo();
    }
}

/// <summary>Mover um texto independente (undo/redo simples de posição).</summary>
public sealed class ChangeTextItemPositionCommand : IUndoableCommand
{
    private readonly TextItem _text;
    private readonly double _oldX, _oldY, _newX, _newY;

    public string Description => "Mover texto";

    public ChangeTextItemPositionCommand(TextItem text, double oldX, double oldY, double newX, double newY)
    {
        _text = text;
        _oldX = oldX;
        _oldY = oldY;
        _newX = newX;
        _newY = newY;
    }

    public void Undo()
    {
        _text.X = _oldX;
        _text.Y = _oldY;
    }

    public void Redo()
    {
        _text.X = _newX;
        _text.Y = _newY;
    }
}

/// <summary>
/// Mudança de escala (SCALE do CAD) de elementos, formas e textos selecionados.
/// Guarda antes/depois COMPLETOS: o Ctrl+Z devolve o traçado exato — inclusive a espessura da
/// caneta e o tracejado das formas, que também escalam na operação.
/// </summary>
public sealed class ScaleObjectsCommand : IUndoableCommand
{
    private sealed record ElementEntry(PlacedElement Element, double Before, double After);
    private sealed record ShapeEntry(ShapeItem Shape, ShapeItemSnapshot Before, ShapeItemSnapshot After);
    private sealed record TextEntry(TextItem Text, TextItemSnapshot Before, TextItemSnapshot After);

    private readonly Page _page;
    private readonly List<ElementEntry> _elements = new();
    private readonly List<ShapeEntry> _shapes = new();
    private readonly List<TextEntry> _texts = new();

    public string Description { get; }

    public ScaleObjectsCommand(Page page, string description,
        IEnumerable<(PlacedElement Element, double Before, double After)>? elements = null,
        IEnumerable<(ShapeItem Shape, ShapeItemSnapshot Before, ShapeItemSnapshot After)>? shapes = null,
        IEnumerable<(TextItem Text, TextItemSnapshot Before, TextItemSnapshot After)>? texts = null)
    {
        _page = page;
        Description = description;
        if (elements is not null)
            _elements.AddRange(elements.Select(e => new ElementEntry(e.Element, e.Before, e.After)));
        if (shapes is not null)
            _shapes.AddRange(shapes.Select(s => new ShapeEntry(s.Shape, s.Before, s.After)));
        if (texts is not null)
            _texts.AddRange(texts.Select(t => new TextEntry(t.Text, t.Before, t.After)));
    }

    /// <summary>Quantidade de objetos afetados (para mensagem/telemetria).</summary>
    public int Count => _elements.Count + _shapes.Count + _texts.Count;

    private void Apply(bool toNew)
    {
        foreach (var e in _elements)
            e.Element.Scale = toNew ? e.After : e.Before;
        foreach (var s in _shapes)
            (toNew ? s.After : s.Before).Apply(s.Shape);
        foreach (var t in _texts)
            (toNew ? t.After : t.Before).Apply(t.Text);
        _page.RecomputeConductors();
    }

    public void Undo() => Apply(false);

    public void Redo() => Apply(true);
}
