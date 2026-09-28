using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Drawing;
using OperCAD.UI.Controls;
using SkiaSharp;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Views;

/// <summary>Linha de seleção de folha para impressão.</summary>
public sealed class PrintRow
{
    public required Page Page { get; init; }
    public bool IsChecked { get; set; } = true;
    public string Title => Page.Name;
}

/// <summary>Paginator que imprime uma página por folio selecionado.</summary>
public sealed class FolhaPaginator : System.Windows.Documents.DocumentPaginator
{
    private readonly List<BitmapSource> _pages;

    public FolhaPaginator(IEnumerable<BitmapSource> pages, Size pageSize)
    {
        _pages = pages.ToList();
        PageSize = pageSize;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pages.Count;
    public override System.Windows.Documents.IDocumentPaginatorSource Source => null!;
    public override Size PageSize { get; set; }

    public override System.Windows.Documents.DocumentPage GetPage(int pageNumber)
    {
        var image = new Image
        {
            Source = _pages[pageNumber],
            Stretch = Stretch.Uniform,
            Width = PageSize.Width,
            Height = PageSize.Height
        };
        image.Measure(PageSize);
        image.Arrange(new Rect(0, 0, PageSize.Width, PageSize.Height));
        image.UpdateLayout();
        var full = new Rect(0, 0, PageSize.Width, PageSize.Height);
        return new System.Windows.Documents.DocumentPage(image, PageSize, full, full);
    }
}

/// <summary>
/// Janela de impressão com preview (análoga ao janela de impressão do padrão CAD):
/// seleção de folhas, opções de render/impressão, orientação e navegação no preview.
/// </summary>
public partial class PrintWindow : Window
{
    private readonly Project _project;
    private readonly AppSettings _settings;
    private readonly List<PrintRow> _rows;
    private double _scale = 2.0; // px/mm do preview
    private int _pageIndex;

    /// <summary>Largura do papel selecionado em mm.</summary>
    private double _paperWidth = 297;
    /// <summary>Altura do papel selecionado em mm.</summary>
    private double _paperHeight = 210;

    public PrintWindow(Project project, AppSettings settings)
    {
        InitializeComponent();
        _project = project;
        _settings = settings;
        _rows = project.Pages.Select(p => new PrintRow { Page = p }).ToList();
        ListaFolhas.ItemsSource = _rows;

        ChkFrame.IsChecked = settings.PrintDrawFrame;
        ChkTitleBlock.IsChecked = settings.PrintDrawTitleBlock;
        ChkColors.IsChecked = settings.PrintKeepConductorColors;
        ChkTerminals.IsChecked = settings.PrintDrawTerminals;
        ChkFit.IsChecked = settings.PrintFitInPage;

        if (_rows.Count > 0)
            _pageIndex = 0;
        UpdatePreview();
    }

    private List<PrintRow> SelectedRows
    {
        get
        {
            // Se "Imprimir todas as folhas" está marcado, seleciona todas
            if (ChkPrintAllSheets.IsChecked == true)
                return _rows.ToList();
            return _rows.Where(r => r.IsChecked).ToList();
        }
    }

    private SceneRenderer.RenderOptions CurrentOptions => new(
        DrawFrame: ChkFrame.IsChecked == true,
        DrawTitleBlock: ChkTitleBlock.IsChecked == true,
        ColoredConductors: ChkColors.IsChecked == true,
        DrawTerminals: ChkTerminals.IsChecked == true);

    private void UpdatePreview()
    {
        var sel = SelectedRows;
        if (sel.Count == 0)
        {
            PreviewImage.Source = null;
            TxtPage.Text = "Nenhuma folha selecionada";
            return;
        }
        if (_pageIndex >= sel.Count)
            _pageIndex = sel.Count - 1;
        if (_pageIndex < 0)
            _pageIndex = 0;
        var page = sel[_pageIndex].Page;
        using var bmp = PageExporter.RenderBitmap(page, _scale, CurrentOptions);
        PreviewImage.Source = SkiaBitmapExtensions.ToBitmapSource(bmp);
        TxtPage.Text = $"Página {_pageIndex + 1} / {sel.Count} — {page.Name}";
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        _scale = Math.Min(_scale * 1.25, 16);
        UpdatePreview();
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        _scale = Math.Max(_scale / 1.25, 0.25);
        UpdatePreview();
    }

    private void OnFitPage(object sender, RoutedEventArgs e)
    {
        _scale = 2.0;
        UpdatePreview();
    }

    private void OnPrevious(object sender, RoutedEventArgs e)
    {
        if (_pageIndex > 0)
        {
            _pageIndex--;
            UpdatePreview();
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_pageIndex < SelectedRows.Count - 1)
        {
            _pageIndex++;
            UpdatePreview();
        }
    }

    private void OnPaperSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Atualiza dimensões do papel conforme seleção
        switch (CmbPaperSize.SelectedIndex)
        {
            case 0: _paperWidth = 297; _paperHeight = 210; break; // A4
            case 1: _paperWidth = 420; _paperHeight = 297; break; // A3
            case 2: _paperWidth = 594; _paperHeight = 420; break; // A2
            case 3: _paperWidth = 841; _paperHeight = 594; break; // A1
            case 4: _paperWidth = 1189; _paperHeight = 841; break; // A0
            default: _paperWidth = 297; _paperHeight = 210; break; // Personalizado
        }
        UpdatePreview();
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        var sel = SelectedRows;
        if (sel.Count == 0)
        {
            MessageBox.Show("Nenhuma folha selecionada para imprimir.", "Imprimir",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pd = new System.Windows.Controls.PrintDialog();
        if (pd.ShowDialog() != true)
            return;

        // Aplica orientação selecionada
        if (CmbOrientation.SelectedIndex == 1) // Paisagem
        {
            // Inverte largura e altura para paisagem
            var temp = _paperWidth;
            _paperWidth = _paperHeight;
            _paperHeight = temp;
        }

        // Dimensões do papel em pontos (1 mm ≈ 3.7795 pt)
        var mmToPt = 3.7795;
        var pageWidthPt = _paperWidth * mmToPt;
        var pageHeightPt = _paperHeight * mmToPt;
        var pageSize = new Size(pageWidthPt, pageHeightPt);

        // Se "página inteira" está marcado, renderiza sem moldura/margens
        var renderOptions = ChkFullPage.IsChecked == true
            ? CurrentOptions with { DrawFrame = false, DrawTitleBlock = false }
            : CurrentOptions;

        // Renderiza todas as folhas selecionadas em resolução de impressão
        // (~12 px/mm ≈ 300 DPI) para saída nítida.
        const double printPxPerMm = 12.0;
        var pages = new List<BitmapSource>();
        foreach (var row in sel)
        {
            using var bmp = PageExporter.RenderBitmap(row.Page, printPxPerMm, renderOptions);
            pages.Add(SkiaBitmapExtensions.ToBitmapSource(bmp));
        }

        pd.PrintDocument(new FolhaPaginator(pages, pageSize), "OperCAD — impressão do projeto");
        DialogResult = true;
    }
}
