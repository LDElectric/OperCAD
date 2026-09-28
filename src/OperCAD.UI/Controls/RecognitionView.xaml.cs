using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using OperCAD.Drawing;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Controls;

/// <summary>
/// Pré-visualização da folha OperCAD com o conteúdo recortado posicionado.
/// Mostra a moldura ABNT, grid, e as shapes/textos como objetos nativos selecionáveis.
/// </summary>
public partial class RecognitionView : UserControl
{
    private Page? _previewPage;
    private ViewTransform _view = ViewTransform.Identity;
    private List<ShapeItem> _shapes = new();
    private List<TextItem> _texts = new();
    private int _selectedIndex = -1;

    // Interação
    private bool _isPanning;
    private System.Windows.Point _lastScreen;

    public RecognitionView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => TryAutoFit();
    }

    /// <summary>Forma selecionada atualmente (ou null).</summary>
    public ShapeItem? SelectedShape =>
        _selectedIndex >= 0 && _selectedIndex < _shapes.Count ? _shapes[_selectedIndex] : null;

    /// <summary>Todas as shapes traduzidas (para clustering posterior).</summary>
    public IReadOnlyList<ShapeItem> TranslatedShapes => _shapes;

    /// <summary>Todos os textos traduzidos.</summary>
    public IReadOnlyList<TextItem> TranslatedTexts => _texts;

    /// <summary>Evento disparado quando a seleção muda.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Configura a preview: traduz shapes/textos recortados para a área de desenho da folha ABNT.
    /// Retorna as shapes traduzidas para uso externo (clustering, etc).
    /// </summary>
    public (List<ShapeItem> shapes, List<TextItem> texts) Setup(
        Page originalPage, Rect2D crop, List<ShapeItem> shapes, List<TextItem> texts)
    {
        _selectedIndex = -1;

        // Criar página preview com template ABNT
        _previewPage = CreatePreviewPage(originalPage, crop, shapes, texts);

        // Adicionar shapes traduzidas para a página preview
        foreach (var shape in _shapes)
            _previewPage.Add(shape);
        foreach (var text in _texts)
            _previewPage.Add(text);

        TryAutoFit();
        Canvas.InvalidateVisual();
        UpdateStatus();

        return (_shapes, _texts);
    }

    private Page CreatePreviewPage(Page originalPage, Rect2D crop,
        List<ShapeItem> incomingShapes, List<TextItem> incomingTexts)
    {
        var preview = new Page();
        var s = preview.Settings;

        // Copiar tamanho da página original
        s.Size = originalPage.Settings.Size;

        // Aplicar template ABNT
        var builtin = FolhaTemplateCatalog.Builtin();
        if (builtin is not null)
        {
            s.FolhaTemplate = builtin;
            s.FolhaFrameInset = builtin.FrameInset;
            s.ColumnsCount = builtin.ColumnsCount;
            s.RowsCount = builtin.RowsCount;
        }

        s.ShowFrame = true;
        s.ShowGrid = true;

        // Traduzir shapes da área de crop para a área de desenho da folha
        if (builtin is not null && crop.Width > 0 && crop.Height > 0)
        {
            var drawingArea = FolhaTemplateRenderer.DrawingArea(builtin, s);
            var scaleX = drawingArea.Width / crop.Width;
            var scaleY = drawingArea.Height / crop.Height;
            var scale = Math.Min(scaleX, scaleY);

            var scaledW = crop.Width * scale;
            var scaledH = crop.Height * scale;
            var offsetX = drawingArea.MinX + (drawingArea.Width - scaledW) / 2.0;
            var offsetY = drawingArea.MinY + (drawingArea.Height - scaledH) / 2.0;

            var dx = offsetX - crop.MinX * scale;
            var dy = offsetY - crop.MinY * scale;

            // Criar novas listas com shapes traduzidas
            _shapes = incomingShapes.Select(s => TranslateShape(s, scale, dx, dy)).ToList();
            _texts = incomingTexts.Select(t => TranslateText(t, scale, dx, dy)).ToList();
        }
        else
        {
            _shapes = new List<ShapeItem>(incomingShapes);
            _texts = new List<TextItem>(incomingTexts);
        }

        return preview;
    }

    private static ShapeItem TranslateShape(ShapeItem shape, double scale, double dx, double dy)
    {
        return new ShapeItem
        {
            Kind = shape.Kind,
            Color = shape.Color,
            PenWidth = shape.PenWidth,
            DashStyle = shape.DashStyle,
            DashPattern = new List<double>(shape.DashPattern),
            DashPhase = shape.DashPhase,
            LineCap = shape.LineCap,
            LineJoin = shape.LineJoin,
            Filling = shape.Filling,
            Closed = shape.Closed,
            ImportGroupId = shape.ImportGroupId,
            Points = shape.Points.Select(p => new Point2D(p.X * scale + dx, p.Y * scale + dy)).ToList()
        };
    }

    private static TextItem TranslateText(TextItem text, double scale, double dx, double dy)
    {
        return new TextItem
        {
            Text = text.Text,
            X = text.X * scale + dx,
            Y = text.Y * scale + dy,
            FontFamily = text.FontFamily,
            FontSizePt = text.FontSizePt * scale,
            Bold = text.Bold,
            Italic = text.Italic,
            Underline = text.Underline,
            Strikethrough = text.Strikethrough,
            Rotation = text.Rotation,
            Color = text.Color,
            TextAlign = text.TextAlign,
            ImportGroupId = text.ImportGroupId,
            IsLocked = text.IsLocked
        };
    }

    private void TryAutoFit()
    {
        if (_previewPage is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var bounds = new Rect2D(0, 0, _previewPage.Settings.Width, _previewPage.Settings.Height);
        _view = ViewTransform.Identity.FitToView(bounds, ActualWidth, ActualHeight, 24);
        Canvas.InvalidateVisual();
    }

    private void UpdateStatus()
    {
        TxtShapeCount.Text = $"Formas: {_shapes.Count}";
        var sel = _selectedIndex >= 0 ? _shapes[_selectedIndex].Kind.ToString() : "nenhuma";
        TxtSelected.Text = $"Selecionada: {sel}";
    }

    // --- Renderização ---

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_previewPage is null) return;

        var canvas = e.Surface.Canvas;
        var wDip = Math.Max(ActualWidth, 1.0);
        var hDip = Math.Max(ActualHeight, 1.0);

        var dpiX = e.Info.Width / wDip;
        var dpiY = e.Info.Height / hDip;
        if (Math.Abs(dpiX - 1.0) > 0.001)
            canvas.Scale((float)dpiX, (float)dpiY);

        var grid = new GridSettings { IsVisible = true };
        SceneRenderer.Render(canvas, _previewPage, _view, grid, wDip, hDip,
            new[] { _previewPage }, greyBackground: false);

        // Destacar forma selecionada
        if (_selectedIndex >= 0 && _selectedIndex < _shapes.Count)
        {
            var b = ShapeRenderer.ComputeBounds(_shapes[_selectedIndex]);
            var tl = _view.WorldToScreen(new Point2D(b.MinX, b.MinY));
            var br = _view.WorldToScreen(new Point2D(b.MaxX, b.MaxY));
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(0, 120, 215),
                StrokeWidth = 2,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new float[] { 6, 3 }, 0)
            };
            canvas.DrawRect(
                (float)Math.Min(tl.X, br.X), (float)Math.Min(tl.Y, br.Y),
                (float)Math.Abs(br.X - tl.X), (float)Math.Abs(br.Y - tl.Y), paint);
        }
    }

    // --- Interação ---

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(Canvas);
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        _view = _view.ZoomAt(factor, new Point2D(pos.X, pos.Y));
        Canvas.InvalidateVisual();
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Canvas.Focus();
        var pos = e.GetPosition(Canvas);

        if (e.ChangedButton == MouseButton.Left)
        {
            var world = _view.ScreenToWorld(new Point2D(pos.X, pos.Y));
            _selectedIndex = HitTestShape(world);
            UpdateStatus();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Canvas.InvalidateVisual();
            e.Handled = true;
            return;
        }

        _isPanning = true;
        _lastScreen = pos;
        Canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetPosition(Canvas);
            var dx = pos.X - _lastScreen.X;
            var dy = pos.Y - _lastScreen.Y;
            _view = _view.Pan(dx, dy);
            _lastScreen = pos;
            Canvas.InvalidateVisual();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        Canvas.ReleaseMouseCapture();
    }

    private int HitTestShape(Point2D world)
    {
        const double tolerance = 3.0;
        var bestDist = double.MaxValue;
        var bestIdx = -1;
        for (var i = 0; i < _shapes.Count; i++)
        {
            var shape = _shapes[i];
            var d = ShapeGeometry.DistanceToOutline(shape, world);
            if (d < tolerance && d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }
        return bestIdx;
    }
}
