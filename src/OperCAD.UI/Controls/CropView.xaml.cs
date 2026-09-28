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
/// Visualização de uma página importada (conjunto de ShapeItem/TextItem) com zoom/pano e
/// seleção de um retângulo de recorte em COORDENADAS DO MUNDO (mm). O zoom é apenas visual:
/// não afeta as dimensões do recorte nem do conteúdo.
/// </summary>
public partial class CropView : UserControl
{
    private Page? _page;
    private ViewTransform _view = ViewTransform.Identity;
    private double _minX, _minY, _maxX, _maxY; // recorte em mm

    // Interação
    private bool _isPanning;
    private bool _isDrawing;
    private Point2D _drawStartWorld;
    private System.Windows.Point _lastScreen;
    private System.Windows.Point _downScreen;
    private CropHandle _activeHandle = CropHandle.None;
    private (double MinX, double MinY, double MaxX, double MaxY) _downRect;
    private bool _fitPending;

    public CropView()
    {
        InitializeComponent();
        // Re-enquadra quando o controle finalmente ganha tamanho (no construtor ainda é 0).
        SizeChanged += (_, _) => TryAutoFit();
    }

    /// <summary>Define a página a ser exibida (conteúdo em mm da página PDF).</summary>
    public void SetPage(Page? page)
    {
        _page = page;
        if (page is not null)
        {
            var shapes = page.Objects.OfType<ShapeItem>().ToList();
            // Detecta a área útil pela MOLDURA (robusto a molduras compostas e a molduras
            // desenhadas como linhas soltas), usando as dimensões do papel da página.
            var crop = PdfAreaDetector.Detect(shapes, page.Settings.Width, page.Settings.Height)
                       ?? ContentBounds(shapes, page.Objects.OfType<TextItem>().ToList());
            if (crop is { } c)
            {
                _minX = c.MinX; _minY = c.MinY; _maxX = c.MaxX; _maxY = c.MaxY;
            }
            else
            {
                _minX = 0; _minY = 0; _maxX = page.Settings.Width; _maxY = page.Settings.Height;
            }
        }
        _fitPending = true;
        TryAutoFit();
        Canvas.InvalidateVisual();
    }

    /// <summary>Enquadra a página assim que o controle tiver tamanho real (evita iniciar no canto esquerdo superior).</summary>
    private void TryAutoFit()
    {
        if (!_fitPending || _page is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        FitToPage();
        _fitPending = false;
        Canvas.InvalidateVisual();
    }

    public Rect2D CropRect => new(
        Math.Min(_minX, _maxX),
        Math.Min(_minY, _maxY),
        Math.Abs(_maxX - _minX),
        Math.Abs(_maxY - _minY));

    /// <summary>Define explicitamente o retângulo de recorte (mm) — usado ao voltar a uma página já ajustada.</summary>
    public void SetCrop(Rect2D rect)
    {
        _minX = rect.MinX; _minY = rect.MinY; _maxX = rect.MaxX; _maxY = rect.MaxY;
        Canvas.InvalidateVisual();
    }

    public event EventHandler? CropChanged;

    private void FitToPage()
    {
        if (_page is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var bounds = new Rect2D(0, 0, _page.Settings.Width, _page.Settings.Height);
        _view = ViewTransform.Identity.FitToView(bounds, ActualWidth, ActualHeight, 32);
    }

    private static Rect2D? ContentBounds(List<ShapeItem> shapes, List<TextItem> texts)
    {
        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        var any = false;
        foreach (var s in shapes)
        {
            var b = s.Bounds;
            minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY);
            maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY);
            any = true;
        }
        foreach (var t in texts)
        {
            minX = Math.Min(minX, t.X); minY = Math.Min(minY, t.Y);
            maxX = Math.Max(maxX, t.X); maxY = Math.Max(maxY, t.Y);
            any = true;
        }
        return any ? new Rect2D(minX, minY, maxX - minX, maxY - minY) : null;
    }

    private void OnPaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        if (_page is null)
            return;

        foreach (var obj in _page.Objects)
        {
            switch (obj)
            {
                case ShapeItem shape:
                    ShapeRenderer.Render(canvas, shape, _view);
                    break;
                case TextItem text:
                    TextItemRenderer.Render(canvas, text, _view);
                    break;
            }
        }

        DrawCropOverlay(canvas);
    }

    private void DrawCropOverlay(SKCanvas canvas)
    {
        var tl = _view.WorldToScreen(new Point2D(Math.Min(_minX, _maxX), Math.Max(_minY, _maxY)));
        var br = _view.WorldToScreen(new Point2D(Math.Max(_minX, _maxX), Math.Min(_minY, _maxY)));
        var rect = SKRect.Create(
            (float)Math.Min(tl.X, br.X), (float)Math.Min(tl.Y, br.Y),
            (float)Math.Abs(br.X - tl.X), (float)Math.Abs(br.Y - tl.Y));

        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0, 120, 215), StrokeWidth = 2, IsAntialias = true };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 120, 215, 24), IsAntialias = true };
        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, stroke);

        using var handle = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 120, 215), IsAntialias = true };
        var h = 6f;
        foreach (var corner in new[]
        {
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Top),
            new SKPoint(rect.Left, rect.Bottom),
            new SKPoint(rect.Right, rect.Bottom)
        })
        {
            canvas.DrawRect(corner.X - h, corner.Y - h, h * 2, h * 2, handle);
        }
    }

    private enum CropHandle { None, Move, NW, NE, SW, SE }

    private CropHandle HitTestHandle(Point2D screen)
    {
        var tl = _view.WorldToScreen(new Point2D(Math.Min(_minX, _maxX), Math.Max(_minY, _maxY)));
        var br = _view.WorldToScreen(new Point2D(Math.Max(_minX, _maxX), Math.Min(_minY, _maxY)));
        var tol = 8.0;
        var near = new Dictionary<CropHandle, Point2D>
        {
            [CropHandle.NW] = tl,
            [CropHandle.NE] = new(br.X, tl.Y),
            [CropHandle.SW] = new(tl.X, br.Y),
            [CropHandle.SE] = br
        };
        foreach (var pair in near)
            if (screen.DistanceTo(pair.Value) <= tol)
                return pair.Key;

        var inside = screen.X >= Math.Min(tl.X, br.X) && screen.X <= Math.Max(tl.X, br.X) &&
                     screen.Y >= Math.Min(tl.Y, br.Y) && screen.Y <= Math.Max(tl.Y, br.Y);
        return inside ? CropHandle.Move : CropHandle.None;
    }

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
        _lastScreen = pos;
        _downScreen = pos;
        var screen = new Point2D(pos.X, pos.Y);

        if (e.ChangedButton == MouseButton.Left)
        {
            _activeHandle = HitTestHandle(screen);
            _downRect = (_minX, _minY, _maxX, _maxY);
            if (_activeHandle != CropHandle.None)
            {
                Canvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // Área vazia com botão esquerdo → DESENHAR um novo retângulo de recorte.
            _isDrawing = true;
            var world = _view.ScreenToWorld(screen);
            _drawStartWorld = world;
            _minX = _maxX = world.X;
            _minY = _maxY = world.Y;
            Canvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Pan com botão direito ou meio.
        _isPanning = true;
        Canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Canvas);
        var screen = new Point2D(pos.X, pos.Y);

        if (_activeHandle != CropHandle.None)
        {
            var world = _view.ScreenToWorld(screen);
            var downWorld = _view.ScreenToWorld(new Point2D(_downScreen.X, _downScreen.Y));
            var minSizeMm = 5.0;
            if (_activeHandle == CropHandle.Move)
            {
                var dx = world.X - downWorld.X;
                var dy = world.Y - downWorld.Y;
                _minX = _downRect.MinX + dx; _maxX = _downRect.MaxX + dx;
                _minY = _downRect.MinY + dy; _maxY = _downRect.MaxY + dy;
            }
            else
            {
                if (_activeHandle is CropHandle.NW or CropHandle.SW) _minX = Math.Min(world.X, _maxX - minSizeMm);
                if (_activeHandle is CropHandle.NE or CropHandle.SE) _maxX = Math.Max(world.X, _minX + minSizeMm);
                if (_activeHandle is CropHandle.NW or CropHandle.NE) _maxY = Math.Max(world.Y, _minY + minSizeMm);
                if (_activeHandle is CropHandle.SW or CropHandle.SE) _minY = Math.Min(world.Y, _maxY - minSizeMm);
            }
            Canvas.InvalidateVisual();
            CropChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isDrawing)
        {
            var world = _view.ScreenToWorld(screen);
            _minX = Math.Min(_drawStartWorld.X, world.X);
            _maxX = Math.Max(_drawStartWorld.X, world.X);
            _minY = Math.Min(_drawStartWorld.Y, world.Y);
            _maxY = Math.Max(_drawStartWorld.Y, world.Y);
            Canvas.InvalidateVisual();
            CropChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isPanning)
        {
            var dx = pos.X - _lastScreen.X;
            var dy = pos.Y - _lastScreen.Y;
            _view = _view.Pan(dx, dy);
            _lastScreen = pos;
            Canvas.InvalidateVisual();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _activeHandle = CropHandle.None;
        _isPanning = false;
        _isDrawing = false;
        Canvas.ReleaseMouseCapture();
    }
}
