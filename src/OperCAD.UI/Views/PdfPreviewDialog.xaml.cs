using System.Diagnostics;
using System.Windows;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using OperCAD.Drawing;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Views;

/// <summary>
/// Diálogo de pré-visualização: mostra o resultado da importação na folha OperCAD
/// com a área detectada automaticamente. O usuário pode importar direto ou
/// voltar para seleção manual.
/// </summary>
public partial class PdfPreviewDialog : Window
{
    private readonly string _pdfPath;
    private Project? _importedProject;
    private Page? _currentPage;
    private ViewTransform _view = ViewTransform.Identity;

    /// <summary>Shapes importadas (disponíveis após DialogResult = true).</summary>
    public List<ShapeItem> ImportedShapes { get; private set; } = new();

    /// <summary>Textos importados (disponíveis após DialogResult = true).</summary>
    public List<TextItem> ImportedTexts { get; private set; } = new();

    /// <summary>Configuração de folha decidida pelo importador.</summary>
    public PageSettings? ImportedPageSettings { get; private set; }

    /// <summary>True se o usuário escolheu seleção manual.</summary>
    public bool WantsManualSelection { get; private set; }

    [Conditional("DEBUG")]
    private static void Log(string msg) => Debug.WriteLine($"[PREVIEW] {msg}");

    public PdfPreviewDialog(string pdfPath)
    {
        InitializeComponent();
        _pdfPath = pdfPath;
        TxtTitle.Text = $"Prévia — {System.IO.Path.GetFileName(pdfPath)}";

        Log($"=== Preview aberto === PDF: {pdfPath}");
        LoadAndPreview();
    }

    private void LoadAndPreview()
    {
        try
        {
            // Importar com detecção automática (Target = NativeSheet usa PdfAreaDetector)
            var options = new PdfImportOptions
            {
                Target = PdfImportTarget.NativeSheet,
                ShowFrame = true,
                ShowGrid = true,
                ScaleMode = PdfImportScaleMode.RealScale,
                AutoPaperSize = true,
                LineWidthMode = PdfLineWidthMode.PdfNative
            };

            _importedProject = new PdfImporter(options).Import(_pdfPath);

            if (_importedProject.Pages.Count > 0)
            {
                _currentPage = _importedProject.Pages[0];
                ImportedPageSettings = _currentPage.Settings;
                ImportedShapes = _currentPage.Objects.OfType<ShapeItem>().ToList();
                ImportedTexts = _currentPage.Objects.OfType<TextItem>().ToList();

                Log($"  Importado: {ImportedShapes.Count} shapes, {ImportedTexts.Count} textos");
                Log($"  Folha: {_currentPage.Settings.Width:F0}x{_currentPage.Settings.Height:F0}mm");

                TxtInfo.Text = $"Folha {_currentPage.Settings.Width:F0}×{_currentPage.Settings.Height:F0} mm" +
                               $" | {ImportedShapes.Count} formas, {ImportedTexts.Count} textos";
                TxtStatus.Text = "Área detectada automaticamente — clique Importar para confirmar";
            }
        }
        catch (Exception ex)
        {
            Log($"  ERRO: {ex.Message}");
            MessageBox.Show($"Erro ao importar PDF: {ex.Message}", "Erro",
                MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            return;
        }

        PreviewCanvas.InvalidateVisual();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.White);

        if (_currentPage is null)
            return;

        // Calcular view transform para caber na página
        var bounds = new Rect2D(0, 0, _currentPage.Settings.Width, _currentPage.Settings.Height);
        _view = ViewTransform.Identity.FitToView(bounds, e.Info.Width, e.Info.Height, 32);

        // Renderizar moldura ABNT (fundo da folha)
        RenderFrame(canvas, e.Info.Width, e.Info.Height);

        // Renderizar shapes e textos
        foreach (var obj in _currentPage.Objects)
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
    }

    private void RenderFrame(SKCanvas canvas, int width, int height)
    {
        if (_currentPage is null) return;

        var settings = _currentPage.Settings;
        var pageW = settings.Width;
        var pageH = settings.Height;

        // Desenhar borda da folha
        var tl = _view.WorldToScreen(new Point2D(0, pageH));
        var br = _view.WorldToScreen(new Point2D(pageW, 0));
        var pageRect = SKRect.Create(
            (float)Math.Min(tl.X, br.X), (float)Math.Min(tl.Y, br.Y),
            (float)Math.Abs(br.X - tl.X), (float)Math.Abs(br.Y - tl.Y));

        using var pagePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(200, 200, 200),
            StrokeWidth = 1,
            IsAntialias = true
        };
        canvas.DrawRect(pageRect, pagePaint);

        // Desenhar área útil (recorte azul claro)
        if (settings.MarginLeft > 0 || settings.MarginRight > 0 ||
            settings.MarginTop > 0 || settings.MarginBottom > 0)
        {
            var areaLeft = settings.MarginLeft;
            var areaBottom = settings.MarginBottom;
            var areaRight = pageW - settings.MarginRight;
            var areaTop = pageH - settings.MarginTop;

            var areaTL = _view.WorldToScreen(new Point2D(areaLeft, areaTop));
            var areaBR = _view.WorldToScreen(new Point2D(areaRight, areaBottom));
            var areaRect = SKRect.Create(
                (float)Math.Min(areaTL.X, areaBR.X), (float)Math.Min(areaTL.Y, areaBR.Y),
                (float)Math.Abs(areaBR.X - areaTL.X), (float)Math.Abs(areaBR.Y - areaTL.Y));

            using var areaPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(230, 240, 255),
                IsAntialias = true
            };
            canvas.DrawRect(areaRect, areaPaint);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnManual(object sender, RoutedEventArgs e)
    {
        WantsManualSelection = true;
        DialogResult = false;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (ImportedShapes.Count == 0 && ImportedTexts.Count == 0)
        {
            MessageBox.Show("Nenhum conteúdo para importar.", "Aviso",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Log($"  Importando: {ImportedShapes.Count} shapes, {ImportedTexts.Count} textos");
        DialogResult = true;
    }
}
