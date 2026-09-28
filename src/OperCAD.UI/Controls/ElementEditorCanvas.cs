using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;
using OperCAD.Drawing;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace OperCAD.UI.Controls;

/// <summary>
/// Canvas do editor de elementos (WYSIWYG): renderiza a definição em edição
/// (espaço y-up, 1 unidade = 1 mm), permite selecionar/mover primitivas e
/// desenhar novas (linha, retângulo, elipse, polilinha, texto, terminal).
/// </summary>
public sealed class ElementEditorCanvas : Grid
{
    private readonly SKElement _canvas;
    private ElementDefinition _def = new() { Name = "novo_elemento" };
    private ViewTransform _view = ViewTransform.Identity;

    public event EventHandler? DefinitionChanged;
    public event EventHandler? SelectionChanged;

    public ElementDefinition Def
    {
        get => _def;
        set
        {
            _def = value;
            FitToContent();
            Invalidate();
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Índice da primitiva selecionada (-1 = nenhuma).</summary>
    public int SelectedPrimitive { get; private set; } = -1;

    /// <summary>Índice do terminal selecionado (-1 = nenhum).</summary>
    public int SelectedTerminal { get; private set; } = -1;

    private InsertTool _tool;
    private readonly List<Point2D> _toolPoints = new();
    private Point2D _cursorDef;
    private bool _dragging;
    private Point2D _dragStartDef;
    private (double X, double Y) _dragOrig;

    public ElementEditorCanvas()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        _canvas = new SKElement { Focusable = true };
        Children.Add(_canvas);
        _canvas.PaintSurface += OnPaint;
        _canvas.MouseDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseUp += OnMouseUp;
        _canvas.MouseWheel += (_, e) =>
        {
            var p = e.GetPosition(_canvas);
            _view = _view.ZoomAt(e.Delta > 0 ? 1.15 : 1 / 1.15, new Point2D(p.X, p.Y));
            Invalidate();
        };
        _canvas.KeyDown += OnKeyDown;
        _canvas.SizeChanged += (_, _) => Invalidate();
        Loaded += (_, _) => FitToContent();
    }

    public void BeginTool(InsertTool tool)
    {
        _tool = tool;
        _toolPoints.Clear();
        SelectedPrimitive = -1;
        SelectedTerminal = -1;
        Cursor = Cursors.Cross;
        _canvas.Focus();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EndTool()
    {
        _tool = InsertTool.None;
        _toolPoints.Clear();
        Cursor = Cursors.Arrow;
        Invalidate();
    }

    /// <summary>Adiciona uma primitiva (usado pelos diálogos de texto/terminal).</summary>
    public void AddPrimitive(ElementPrimitive prim)
    {
        PushSnapshot();
        _def.Primitives.Add(prim);
        Invalidate();
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddTerminal(ElementTerminal terminal)
    {
        PushSnapshot();
        _def.Terminals.Add(terminal);
        Invalidate();
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove a primitiva/terminal selecionada (Delete).</summary>
    public void DeleteSelection()
    {
        if (SelectedPrimitive >= 0 && SelectedPrimitive < _def.Primitives.Count)
        {
            PushSnapshot();
            _def.Primitives.RemoveAt(SelectedPrimitive);
            SelectedPrimitive = -1;
        }
        else if (SelectedTerminal >= 0 && SelectedTerminal < _def.Terminals.Count)
        {
            PushSnapshot();
            _def.Terminals.RemoveAt(SelectedTerminal);
            SelectedTerminal = -1;
        }
        else
        {
            return;
        }
        Invalidate();
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Aplica novas propriedades de terminal selecionado.</summary>
    public void UpdateSelectedTerminal(ElementTerminal updated)
    {
        if (SelectedTerminal < 0 || SelectedTerminal >= _def.Terminals.Count)
            return;
        PushSnapshot();
        _def.Terminals[SelectedTerminal] = updated;
        Invalidate();
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============ Desfazer/refazer do editor (snapshots imutáveis da definição) ============

    private readonly Stack<ElementDefinition> _undo = new();
    private readonly Stack<ElementDefinition> _redo = new();

    /// <summary>Disparado quando o estado de undo/redo muda (para habilitar botões).</summary>
    public event EventHandler? UndoStateChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Registra o estado atual ANTES de uma mutação (chamado pelos gestos/ferramentas).</summary>
    public void PushSnapshot()
    {
        _undo.Push(_def.DeepCopy());
        if (_undo.Count > 100)
            _undo.TrimExcess();
        _redo.Clear();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Push(_def.DeepCopy());
        Def = _undo.Pop();
        Invalidate();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Push(_def.DeepCopy());
        Def = _redo.Pop();
        Invalidate();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FitToContent()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var b = ElementRenderer.ComputeBounds(_def);
        var w = Math.Max(b.Width, 1);
        var h = Math.Max(b.Height, 1);
        var scale = Math.Min((ActualWidth - 40) / w, (ActualHeight - 40) / h);
        if (scale <= 0)
            scale = 1;
        var center = new Point2D(b.MinX + w / 2, b.MinY + h / 2);
        _view = new ViewTransform(scale,
            ActualWidth / 2 - center.X * scale,
            ActualHeight / 2 + center.Y * scale);
        Invalidate();
    }

    private void Invalidate() => _canvas.InvalidateVisual();

    private Point2D ToScreen(Point2D defPoint)
        => _view.WorldToScreen(new Point2D(defPoint.X - _def.HotspotX, defPoint.Y - _def.HotspotY));

    private Point2D ToDef(Point screen)
    {
        var w = _view.ScreenToWorld(new Point2D(screen.X, screen.Y));
        return new Point2D(w.X + _def.HotspotX, w.Y + _def.HotspotY);
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColor.Parse("#FAFAFA"));

        // grid 5 mm
        var s = _view.Scale * 5;
        if (s >= 6)
        {
            using var gridPaint = new SKPaint { Color = new SKColor(0, 0, 0, 25) };
            var w0 = _view.ScreenToWorld(new Point2D(0, 0));
            var w1 = _view.ScreenToWorld(new Point2D(e.Info.Width, e.Info.Height));
            for (var x = Math.Floor(w0.X / 5) * 5; x <= w1.X; x += 5)
            {
                var p = ToScreen(new Point2D(x, 0));
                canvas.DrawLine((float)p.X, 0, (float)p.X, e.Info.Height, gridPaint);
            }
            for (var y = Math.Floor(Math.Min(w0.Y, w1.Y) / 5) * 5; y <= Math.Max(w0.Y, w1.Y); y += 5)
            {
                var p = ToScreen(new Point2D(0, y));
                canvas.DrawLine(0, (float)p.Y, e.Info.Width, (float)p.Y, gridPaint);
            }
        }

        // eixos do hotspot
        var hp = ToScreen(new Point2D(_def.HotspotX, _def.HotspotY));
        using (var axis = new SKPaint { Color = new SKColor(60, 120, 200, 90), StrokeWidth = 1 })
        {
            canvas.DrawLine((float)hp.X, 0, (float)hp.X, e.Info.Height, axis);
            canvas.DrawLine(0, (float)hp.Y, e.Info.Width, (float)hp.Y, axis);
        }

        // definição (com o mesmo pipeline da folha)
        ElementRenderer.Render(canvas, _def, 0, 0, 0, _view, null);

        // seleção
        if (SelectedPrimitive >= 0 && SelectedPrimitive < _def.Primitives.Count)
            DrawSelectionRect(canvas, PrimitiveBounds(_def.Primitives[SelectedPrimitive]));
        else if (SelectedTerminal >= 0 && SelectedTerminal < _def.Terminals.Count)
        {
            var t = _def.Terminals[SelectedTerminal];
            DrawSelectionRect(canvas, new Rect2D(t.X - 2, t.Y - 2, 4, 4));
        }

        // pré-visualização da ferramenta
        if (_tool != InsertTool.None && _toolPoints.Count > 0)
        {
            using var dash = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 122, 0),
                StrokeWidth = 1.4f,
                PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0)
            };
            var last = ToScreen(_toolPoints[^1]);
            var cur = ToScreen(_cursorDef);
            if (_tool is InsertTool.Line or InsertTool.Polygon)
                canvas.DrawLine((float)last.X, (float)last.Y, (float)cur.X, (float)cur.Y, dash);
            else
            {
                var r = SKRect.Create(
                    (float)Math.Min(last.X, cur.X), (float)Math.Min(last.Y, cur.Y),
                    (float)Math.Abs(cur.X - last.X), (float)Math.Abs(cur.Y - last.Y));
                if (_tool == InsertTool.Ellipse)
                    canvas.DrawOval(r, dash);
                else
                    canvas.DrawRect(r, dash);
            }
        }
    }

    private void DrawSelectionRect(SKCanvas canvas, Rect2D defRect)
    {
        var tl = ToScreen(new Point2D(defRect.MinX, defRect.MaxY));
        var br = ToScreen(new Point2D(defRect.MaxX, defRect.MinY));
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Gray,
            StrokeWidth = 1,
            PathEffect = SKPathEffect.CreateDash(new[] { 6f, 3f, 1f, 3f }, 0)
        };
        canvas.DrawRect(SKRect.Create((float)tl.X, (float)tl.Y, (float)(br.X - tl.X), (float)(br.Y - tl.Y)), paint);
    }

    private static Rect2D PrimitiveBounds(ElementPrimitive p) => p switch
    {
        ElementLine l => new Rect2D(Math.Min(l.X1, l.X2), Math.Min(l.Y1, l.Y2), Math.Abs(l.X2 - l.X1), Math.Abs(l.Y2 - l.Y1)),
        ElementRect r => new Rect2D(r.X, r.Y, r.Width, r.Height),
        ElementEllipse e => new Rect2D(e.X, e.Y, e.Width, e.Height),
        ElementArc a => new Rect2D(a.X, a.Y, a.Width, a.Height),
        ElementPolygon pol => pol.Points.Count == 0
            ? new Rect2D(0, 0, 0, 0)
            : new Rect2D(pol.Points.Min(x => x.X), pol.Points.Min(x => x.Y),
                pol.Points.Max(x => x.X) - pol.Points.Min(x => x.X),
                pol.Points.Max(x => x.Y) - pol.Points.Min(x => x.Y)),
        ElementText t => new Rect2D(t.X - 1, t.Y - 1, 2 + t.Text.Length * 2.5, 4),
        _ => new Rect2D(0, 0, 0, 0)
    };

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _def is null)
            return;
        var def = SnapToGridDef(ToDef(e.GetPosition(_canvas)));

        if (_tool != InsertTool.None)
        {
            HandleToolClick(def);
            e.Handled = true;
            return;
        }

        // hit-test primitiva (da última para a primeira — a de cima primeiro)
        var idx = HitTestPrimitive(def);
        if (idx >= 0)
        {
            SelectedPrimitive = idx;
            SelectedTerminal = -1;
            _dragging = true;
            PushSnapshot();
            _dragStartDef = def;
            _dragOrig = (CurrentPrimXOf(_def.Primitives[idx]), CurrentPrimYOf(_def.Primitives[idx]));
            _canvas.CaptureMouse();
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        var tidx = HitTestTerminal(def);
        if (tidx >= 0)
        {
            SelectedPrimitive = -1;
            SelectedTerminal = tidx;
            _dragging = true;
            PushSnapshot();
            _dragStartDef = def;
            _dragOrig = (_def.Terminals[tidx].X, _def.Terminals[tidx].Y);
            _canvas.CaptureMouse();
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        SelectedPrimitive = -1;
        SelectedTerminal = -1;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        _cursorDef = ToDef(e.GetPosition(_canvas));
        if (_dragging)
        {
            var dx = _cursorDef.X - _dragStartDef.X;
            var dy = _cursorDef.Y - _dragStartDef.Y;
            var (nx, ny) = (Snap(_dragOrig.X + dx), Snap(_dragOrig.Y + dy));
            if (SelectedPrimitive >= 0)
                MovePrimitive(SelectedPrimitive, nx, ny);
            else if (SelectedTerminal >= 0)
            {
                var t = _def.Terminals[SelectedTerminal];
                _def.Terminals[SelectedTerminal] = new ElementTerminal
                {
                    Uuid = t.Uuid, Name = t.Name, X = nx, Y = ny,
                    Orientation = t.Orientation, Type = t.Type
                };
            }
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (_tool != InsertTool.None && _toolPoints.Count > 0)
            Invalidate();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _canvas.ReleaseMouseCapture();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (_tool is InsertTool.Line or InsertTool.Polygon))
        {
            FinishPolygon();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_tool != InsertTool.None)
                EndTool();
            else
            {
                SelectedPrimitive = -1;
                SelectedTerminal = -1;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
    }

    private void HandleToolClick(Point2D def)
    {
        switch (_tool)
        {
            case InsertTool.Line:
                _toolPoints.Add(def);
                if (_toolPoints.Count == 2)
                {
                    PushSnapshot();
                    _def.Primitives.Add(new ElementLine
                    {
                        X1 = _toolPoints[0].X, Y1 = _toolPoints[0].Y,
                        X2 = _toolPoints[1].X, Y2 = _toolPoints[1].Y
                    });
                    _toolPoints.Clear();
                    DefinitionChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case InsertTool.Rectangle:
                _toolPoints.Add(def);
                if (_toolPoints.Count == 2)
                {
                    PushSnapshot();
                    var (x, y, w, h) = Normalize(_toolPoints[0], _toolPoints[1]);
                    _def.Primitives.Add(new ElementRect { X = x, Y = y, Width = w, Height = h });
                    _toolPoints.Clear();
                    DefinitionChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case InsertTool.Ellipse:
                _toolPoints.Add(def);
                if (_toolPoints.Count == 2)
                {
                    PushSnapshot();
                    var (x, y, w, h) = Normalize(_toolPoints[0], _toolPoints[1]);
                    _def.Primitives.Add(new ElementEllipse { X = x, Y = y, Width = w, Height = h });
                    _toolPoints.Clear();
                    DefinitionChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case InsertTool.Polygon:
                _toolPoints.Add(def);
                break;
        }
        Invalidate();
    }

    private void FinishPolygon()
    {
        if (_toolPoints.Count >= 2)
        {
            PushSnapshot();
            _def.Primitives.Add(new ElementPolygon { Points = _toolPoints.ToList(), Closed = false });
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
        _toolPoints.Clear();
        Invalidate();
    }

    private int HitTestPrimitive(Point2D def)
    {
        for (var i = _def.Primitives.Count - 1; i >= 0; i--)
        {
            var b = PrimitiveBounds(_def.Primitives[i]).Inflate(2);
            if (b.Contains(def))
                return i;
        }
        return -1;
    }

    private int HitTestTerminal(Point2D def)
    {
        for (var i = _def.Terminals.Count - 1; i >= 0; i--)
        {
            var t = _def.Terminals[i];
            if (Math.Abs(t.X - def.X) <= 3 && Math.Abs(t.Y - def.Y) <= 3)
                return i;
        }
        return -1;
    }

    private double CurrentPrimXOf(ElementPrimitive p) => p switch
    {
        ElementLine l => l.X1,
        ElementRect r => r.X,
        ElementEllipse e => e.X,
        ElementArc a => a.X,
        ElementPolygon pol => pol.Points.Count > 0 ? pol.Points[0].X : 0,
        ElementText t => t.X,
        _ => 0
    };

    private double CurrentPrimYOf(ElementPrimitive p) => p switch
    {
        ElementLine l => l.Y1,
        ElementRect r => r.Y,
        ElementEllipse e => e.Y,
        ElementArc a => a.Y,
        ElementPolygon pol => pol.Points.Count > 0 ? pol.Points[0].Y : 0,
        ElementText t => t.Y,
        _ => 0
    };

    private void MovePrimitive(int idx, double nx, double ny)
    {
        var p = _def.Primitives[idx];
        var dx = nx - CurrentPrimXOf(p);
        var dy = ny - CurrentPrimYOf(p);
        _def.Primitives[idx] = p switch
        {
            ElementLine l => l with { X1 = l.X1 + dx, Y1 = l.Y1 + dy, X2 = l.X2 + dx, Y2 = l.Y2 + dy },
            ElementRect r => r with { X = r.X + dx, Y = r.Y + dy },
            ElementEllipse e => e with { X = e.X + dx, Y = e.Y + dy },
            ElementArc a => a with { X = a.X + dx, Y = a.Y + dy },
            ElementPolygon pol => pol with { Points = pol.Points.Select(q => new Point2D(q.X + dx, q.Y + dy)).ToList() },
            ElementText t => t with { X = t.X + dx, Y = t.Y + dy },
            _ => p
        };
        Invalidate();
    }

    private static (double X, double Y, double W, double H) Normalize(Point2D a, Point2D b)
        => (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private static double Snap(double v) => Math.Round(v * 2) / 2; // 0,5 mm

    /// <summary>
    /// Snap fino do cursor no editor de elementos (0,5 mm). Mantém-se ligado por padrão
    /// (é importante para desenhar símbolos), independente do snap de colocação da folha.
    /// </summary>
    public bool IsSnapEnabled { get; set; } = true;

    private Point2D SnapToGridDef(Point2D p)
        => IsSnapEnabled ? new Point2D(Snap(p.X), Snap(p.Y)) : p;
}
