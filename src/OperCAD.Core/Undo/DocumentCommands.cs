using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Undo;

/// <summary>Adiciona objetos a uma folha (inserir elemento, criar condutor...).</summary>
public sealed class AddObjectsCommand : IUndoableCommand
{
    private readonly Page _page;
    private readonly List<DocumentObject> _objects;

    public string Description { get; }

    public AddObjectsCommand(Page page, IEnumerable<DocumentObject> objects, string description)
    {
        _page = page;
        _objects = objects.ToList();
        Description = description;
    }

    public void Undo()
    {
        foreach (var obj in _objects)
            _page.Objects.Remove(obj);
    }

    public void Redo()
    {
        foreach (var obj in _objects)
            _page.Objects.Add(obj);
    }
}

/// <summary>Remove objetos de uma folha (excluir seleção...).</summary>
public sealed class RemoveObjectsCommand : IUndoableCommand
{
    private readonly Page _page;
    private readonly List<DocumentObject> _objects;

    public string Description { get; }

    public RemoveObjectsCommand(Page page, IEnumerable<DocumentObject> objects, string description)
    {
        _page = page;
        _objects = objects.ToList();
        Description = description;
    }

    public void Undo()
    {
        foreach (var obj in _objects)
            _page.Objects.Add(obj);
    }

    public void Redo()
    {
        foreach (var obj in _objects)
            _page.Objects.Remove(obj);
    }
}

/// <summary>Move elementos de uma posição para outra (arrastar).</summary>
public sealed class MoveElementsCommand : IUndoableCommand
{
    private sealed record MoveEntry(PlacedElement Element, double OldX, double OldY, double NewX, double NewY);
    private sealed record ConductorEntry(Conductor Conductor, List<Point2D> OldPath, List<Point2D> NewPath);

    private readonly Page _page;
    private readonly List<MoveEntry> _entries;
    private readonly List<ConductorEntry> _conductorEntries;

    public string Description { get; }

    public MoveElementsCommand(Page page,
        IEnumerable<(PlacedElement Element, double OldX, double OldY, double NewX, double NewY)> moves,
        string description,
        IEnumerable<(Conductor Conductor, List<Point2D> OldPath, List<Point2D> NewPath)>? conductorMoves = null)
    {
        _page = page;
        _entries = moves.Select(m => new MoveEntry(m.Element, m.OldX, m.OldY, m.NewX, m.NewY)).ToList();
        _conductorEntries = conductorMoves?.Select(c => new ConductorEntry(c.Conductor,
            new List<Point2D>(c.OldPath), new List<Point2D>(c.NewPath))).ToList()
            ?? new List<ConductorEntry>();
        Description = description;
    }

    private void Apply(bool toNew)
    {
        foreach (var e in _entries)
        {
            e.Element.X = toNew ? e.NewX : e.OldX;
            e.Element.Y = toNew ? e.NewY : e.OldY;
        }
        foreach (var c in _conductorEntries)
        {
            var path = toNew ? c.NewPath : c.OldPath;
            c.Conductor.Path.Clear();
            c.Conductor.Path.AddRange(path);
        }
        _page.RecomputeConductors();
    }

    public void Undo() => Apply(false);

    public void Redo() => Apply(true);
}

/// <summary>Gira os elementos selecionados em +90° (Espaço, como no padrão CAD).</summary>
public sealed class RotateElementsCommand : IUndoableCommand
{
    private readonly Page _page;
    private readonly List<PlacedElement> _elements;

    public string Description { get; }

    public RotateElementsCommand(Page page, IEnumerable<PlacedElement> elements, string description)
    {
        _page = page;
        _elements = elements.ToList();
        Description = description;
    }

    private void Apply(int delta)
    {
        foreach (var el in _elements)
            el.Rotation = (el.Rotation + delta + 360) % 360;
        _page.RecomputeConductors();
    }

    public void Undo() => Apply(-90);

    public void Redo() => Apply(+90);
}

/// <summary>
/// Altera a rotação de um ou mais elementos para um ângulo ARBITRÁRIO (rotação com
/// o mouse ou pelo campo de graus das propriedades), com snapshot de antes/depois.
/// </summary>
public sealed class ChangeElementRotationCommand : IUndoableCommand
{
    private readonly Page _page;
    private readonly List<PlacedElement> _elements;
    private readonly List<double> _before;
    private readonly List<double> _after;

    public string Description { get; }

    public ChangeElementRotationCommand(Page page, IEnumerable<(PlacedElement El, double Before, double After)> changes)
    {
        _page = page;
        _elements = changes.Select(c => c.El).ToList();
        _before = changes.Select(c => c.Before).ToList();
        _after = changes.Select(c => c.After).ToList();
        Description = "Girar seleção";
    }

    private void Apply(IReadOnlyList<double> angles)
    {
        for (var i = 0; i < _elements.Count; i++)
            _elements[i].Rotation = angles[i];
        _page.RecomputeConductors();
    }

    public void Undo() => Apply(_before);

    public void Redo() => Apply(_after);
}

/// <summary>Snapshot das informações editáveis de um elemento posicionado.</summary>
public sealed record ElementInfoSnapshot(
    string Label, string Comment, string Function, string Description,
    string Manufacturer, string Designation,
    System.Collections.Generic.IReadOnlyDictionary<int, string> TextOverrides)
{
    public static ElementInfoSnapshot Of(PlacedElement e) => new(
        e.Label, e.Comment, e.Function, e.Description, e.Manufacturer, e.Designation,
        new System.Collections.Generic.Dictionary<int, string>(e.TextOverrides));

    public void Apply(PlacedElement e)
    {
        e.Label = Label;
        e.Comment = Comment;
        e.Function = Function;
        e.Description = Description;
        e.Manufacturer = Manufacturer;
        e.Designation = Designation;
        e.TextOverrides.Clear();
        foreach (var kvp in TextOverrides)
            e.TextOverrides[kvp.Key] = kvp.Value;
    }
}

/// <summary>Altera as informações de um elemento (diálogo de propriedades).</summary>
public sealed class ChangeElementPropertiesCommand : IUndoableCommand
{
    private readonly PlacedElement _element;
    private readonly ElementInfoSnapshot _before;
    private readonly ElementInfoSnapshot _after;

    public string Description { get; }

    public ChangeElementPropertiesCommand(PlacedElement element, ElementInfoSnapshot before, ElementInfoSnapshot after)
    {
        _element = element;
        _before = before;
        _after = after;
        Description = $"Editar {element.Label}";
    }

    public void Undo() => _before.Apply(_element);

    public void Redo() => _after.Apply(_element);
}

/// <summary>Snapshot das configurações de uma folha (moldura, legenda, condutores...).</summary>
public sealed record FolhaSnapshot(
    PaperSize Size, double CustomWidth, double CustomHeight, bool ShowFrame,
    double MarginLeft, double MarginRight, double MarginTop, double MarginBottom,
    bool ShowColumns, bool ShowRows, int ColumnsCount, double ColumnsWidth,
    double ColumnsHeaderHeight, int RowsCount, double RowsHeight, double RowsHeaderWidth,
    string ConductorColor, double ConductorWidth,
    string? TitleBlockFile, TitleBlockPosition TitleBlockPosition,
    string Title, string Company, string Obra, string DrawingNumber, string Revision, string Scale,
    string Sheet, string TotalSheets, bool SheetAutoNumbered, bool TotalSheetsAutoNumbered,
    string Author, string ReviewedBy, string ApprovedBy, DateTime Date)
{
    public static FolhaSnapshot Of(Page page)
    {
        var s = page.Settings;
        var t = page.TitleBlock;
        return new FolhaSnapshot(
            s.Size, s.CustomWidth, s.CustomHeight, s.ShowFrame,
            s.MarginLeft, s.MarginRight, s.MarginTop, s.MarginBottom,
            s.ShowColumns, s.ShowRows, s.ColumnsCount, s.ColumnsWidth,
            s.ColumnsHeaderHeight, s.RowsCount, s.RowsHeight, s.RowsHeaderWidth,
            s.ConductorColor, s.ConductorWidth,
            s.TitleBlockFile, s.TitleBlockPosition,
            t.Title, t.Company, t.Obra, t.DrawingNumber, t.Revision, t.Scale,
            t.Sheet, t.TotalSheets, t.SheetAutoNumbered, t.TotalSheetsAutoNumbered,
            t.Author, t.ReviewedBy, t.ApprovedBy, t.Date);
    }

    public void Apply(Page page)
    {
        var s = page.Settings;
        var t = page.TitleBlock;
        s.Size = Size;
        s.CustomWidth = CustomWidth;
        s.CustomHeight = CustomHeight;
        s.ShowFrame = ShowFrame;
        s.MarginLeft = MarginLeft;
        s.MarginRight = MarginRight;
        s.MarginTop = MarginTop;
        s.MarginBottom = MarginBottom;
        s.ShowColumns = ShowColumns;
        s.ShowRows = ShowRows;
        s.ColumnsCount = ColumnsCount;
        s.ColumnsWidth = ColumnsWidth;
        s.ColumnsHeaderHeight = ColumnsHeaderHeight;
        s.RowsCount = RowsCount;
        s.RowsHeight = RowsHeight;
        s.RowsHeaderWidth = RowsHeaderWidth;
        s.ConductorColor = ConductorColor;
        s.ConductorWidth = ConductorWidth;
        s.TitleBlockFile = TitleBlockFile;
        s.TitleBlockPosition = TitleBlockPosition;
        t.Title = Title;
        t.Company = Company;
        t.Obra = Obra;
        t.DrawingNumber = DrawingNumber;
        t.Revision = Revision;
        t.Scale = Scale;
        t.Sheet = Sheet;
        t.TotalSheets = TotalSheets;
        t.SheetAutoNumbered = SheetAutoNumbered;
        t.TotalSheetsAutoNumbered = TotalSheetsAutoNumbered;
        t.Author = Author;
        t.ReviewedBy = ReviewedBy;
        t.ApprovedBy = ApprovedBy;
        t.Date = Date;
    }
}

/// <summary>Altera as propriedades da folha (diálogo de propriedades do esquema).</summary>
public sealed class ChangeFolhaPropertiesCommand : IUndoableCommand
{
    private readonly Page _page;
    private readonly FolhaSnapshot _before;
    private readonly FolhaSnapshot _after;

    public string Description => "Propriedades do folio";

    public ChangeFolhaPropertiesCommand(Page page, FolhaSnapshot before, FolhaSnapshot after)
    {
        _page = page;
        _before = before;
        _after = after;
    }

    public void Undo() => _before.Apply(_page);

    public void Redo() => _after.Apply(_page);
}
