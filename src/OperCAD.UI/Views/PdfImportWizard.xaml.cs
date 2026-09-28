using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;
using OperCAD.Drawing;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Views;

/// <summary>
/// Wizard de importação de PDF — fase única: seleção da área útil (recorte).
/// O recorte é interpretado em MILÍMETROS do mundo da página do PDF (a visualização tem zoom,
/// mas o zoom é apenas visual e não afeta o resultado). O conteúdo recortado é importado em
/// ESCALA REAL (1:1) para a menor folha ABNT que o comporte — as dimensões do PDF são
/// preservadas para que o desenho importado possa ser reutilizado (copiar/mover/girar).
/// Alternativamente, o usuário pode escolher uma folha fixa (A4/A3) e ajustar o conteúdo à área
/// útil dela.
/// </summary>
public partial class PdfImportWizard : Window
{
    private readonly string _pdfPath;
    private Project? _preview;
    private int _currentPage = 1;
    private readonly Dictionary<int, Rect2D> _crops = new();

    /// <summary>Opções de uma linha do seletor de folha de destino.</summary>
    private sealed record SheetChoice(PdfImportScaleMode Mode, bool AutoPaperSize, PaperSize Paper)
    {
        public override string ToString() => (Mode, AutoPaperSize, Paper) switch
        {
            (PdfImportScaleMode.RealScale, true, _) => "Automática — menor folha, escala 1:1",
            (PdfImportScaleMode.RealScale, false, PaperSize.A3) => "A3 — manter 1:1 (corta o excedente)",
            (PdfImportScaleMode.RealScale, false, _) => "A4 — manter 1:1 (corta o excedente)",
            (PdfImportScaleMode.FitToSheet, _, PaperSize.A3) => "A3 — ajustar conteúdo à área útil",
            _ => "A4 — ajustar conteúdo à área útil"
        };
    }

    public PdfImportOptions Options { get; private set; } = new();

    /// <summary>Configuração de FOLHA decidida pelo importador (papel, moldura, escala) — deve ser
    /// aplicada à página ativa, senão forçar A4 aqui anularia a escala real do desenho.</summary>
    public PageSettings? ImportedPageSettings { get; private set; }

    /// <summary>Shapes traduzidas para coordenadas da folha (disponíveis após OnImport).</summary>
    public List<ShapeItem> ImportedShapes { get; private set; } = new();

    /// <summary>Textos traduzidos para coordenadas da folha (disponíveis após OnImport).</summary>
    public List<TextItem> ImportedTexts { get; private set; } = new();

    [Conditional("DEBUG")]
    private static void Log(string msg) => Debug.WriteLine($"[WIZARD] {msg}");

    public PdfImportWizard(string pdfPath)
    {
        InitializeComponent();
        _pdfPath = pdfPath;
        TxtFile.Text = System.IO.Path.GetFileName(pdfPath);

        SheetMode.Items.Add(new SheetChoice(PdfImportScaleMode.RealScale, true, PaperSize.A4));
        SheetMode.Items.Add(new SheetChoice(PdfImportScaleMode.FitToSheet, false, PaperSize.A4));
        SheetMode.Items.Add(new SheetChoice(PdfImportScaleMode.FitToSheet, false, PaperSize.A3));
        SheetMode.Items.Add(new SheetChoice(PdfImportScaleMode.RealScale, false, PaperSize.A4));
        SheetMode.Items.Add(new SheetChoice(PdfImportScaleMode.RealScale, false, PaperSize.A3));
        SheetMode.SelectedIndex = 0;

        // O recorte é em mm: alterar a seleção atualiza a prévia do RESULTADO (folha + escala).
        Crop.CropChanged += (_, _) => UpdateSheetPlan();

        Log($"=== Wizard aberto === PDF: {pdfPath}");
        LoadPreview();
    }

    private void LoadPreview()
    {
        Log("--- LoadPreview: Importando PDF...");
        try
        {
            _preview = new PdfImporter().Import(_pdfPath);
            Log($"  PDF importado: {_preview.Pages.Count} página(s)");
        }
        catch (Exception ex)
        {
            Log($"  ERRO ao importar PDF: {ex.Message}");
            MessageBox.Show($"Erro ao carregar PDF: {ex.Message}", "Erro",
                MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            return;
        }

        for (var i = 0; i < _preview.Pages.Count; i++)
            PageSelector.Items.Add(new ComboBoxItem { Content = _preview.Pages[i].Name, Tag = i + 1 });

        TxtPageCount.Text = $"de {_preview.Pages.Count}";
        BtnPrevPage.IsEnabled = false;
        BtnNextPage.IsEnabled = _preview.Pages.Count > 1;

        PageSelector.SelectedIndex = 0;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var shapeCount = 0;
        var textCount = 0;
        if (_preview is not null && _currentPage >= 1 && _currentPage <= _preview.Pages.Count)
        {
            var page = _preview.Pages[_currentPage - 1];
            shapeCount = page.Objects.Count(o => o is ShapeItem);
            textCount = page.Objects.Count(o => o is TextItem);
        }
        TxtStatus.Text = $"Página {_currentPage}: {shapeCount} formas, {textCount} textos";
    }

    // --- Navegação de páginas ---

    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSelector.SelectedItem is not ComboBoxItem item || item.Tag is not int pageNumber)
            return;

        // Salvar crop da página anterior
        if (_currentPage >= 1 && _currentPage <= _preview?.Pages.Count)
        {
            _crops[_currentPage] = Crop.CropRect;
        }

        _currentPage = pageNumber;
        var page = _preview!.Pages[pageNumber - 1];
        Crop.SetPage(page);

        if (_crops.TryGetValue(pageNumber, out var saved))
            Crop.SetCrop(saved);

        BtnPrevPage.IsEnabled = pageNumber > 1;
        BtnNextPage.IsEnabled = pageNumber < _preview.Pages.Count;
        UpdateStatus();
        UpdateSheetPlan();
    }

    private void MovePage(int delta)
    {
        var idx = PageSelector.SelectedIndex + delta;
        if (idx < 0 || idx >= PageSelector.Items.Count)
            return;
        PageSelector.SelectedIndex = idx;
    }

    private void OnPrevPage(object sender, RoutedEventArgs e) => MovePage(-1);
    private void OnNextPage(object sender, RoutedEventArgs e) => MovePage(+1);

    // --- Ações ---

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnSheetModeChanged(object sender, SelectionChangedEventArgs e) => UpdateSheetPlan();

    private void OnPdfLineWidthsChanged(object sender, RoutedEventArgs e) => UpdateSheetPlan();

    /// <summary>Monta as opções de importação a partir da seleção atual do wizard.</summary>
    private PdfImportOptions BuildOptions()
    {
        var choice = SheetMode.SelectedItem as SheetChoice
                     ?? new SheetChoice(PdfImportScaleMode.RealScale, true, PaperSize.A4);

        return new PdfImportOptions
        {
            Target = PdfImportTarget.NativeSheet,
            PageCrops = _crops
                .Select(kvp => new PdfPageCrop(kvp.Key, kvp.Value))
                .ToList(),
            ShowFrame = true,
            ShowGrid = true,
            ScaleMode = choice.Mode,
            AutoPaperSize = choice.AutoPaperSize,
            Paper = choice.Paper,
            // Fidelidade: a espessura de CADA linha vem do PDF (pt → mm). Desmarcado, todas as
            // linhas passam a usar a espessura padrão do OperCAD (0,29 mm).
            LineWidthMode = ChkPdfLineWidths.IsChecked == false
                ? PdfLineWidthMode.Uniform
                : PdfLineWidthMode.PdfNative
        };
    }

    /// <summary>
    /// Mostra o RESULTADO previsto para o recorte atual (folha escolhida e escala). É a mesma
    /// decisão usada na importação — evita a surpresa de o desenho sair diferente do esperado
    /// porque a folha de destino o reescalou.
    /// </summary>
    private void UpdateSheetPlan()
    {
        // TxtSheet ainda não existe quando os eventos disparam durante o InitializeComponent.
        if (TxtSheet is null || _preview is null || _currentPage < 1 || _currentPage > _preview.Pages.Count)
        {
            if (TxtSheet is not null)
                TxtSheet.Text = string.Empty;
            return;
        }

        var crop = Crop.CropRect;
        var plan = PdfImporter.PlanSheet(crop, FolhaTemplateCatalog.Builtin(), BuildOptions());
        TxtSheet.Text = plan.Describe(crop);
        TxtSheet.Foreground = plan.FitsSheet
            ? System.Windows.Media.Brushes.DarkBlue
            : System.Windows.Media.Brushes.DarkRed;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_preview is null)
            return;

        // Salvar crop da página atual
        if (_currentPage >= 1 && _currentPage <= _preview.Pages.Count)
            _crops[_currentPage] = Crop.CropRect;

        Options = BuildOptions();

        // A importação é feita pelo PdfImporter: recorte, descarte de moldura (detector
        // robusto), escolha da folha/escala real, espessura padrão do OperCAD, fidelidade de
        // fontes e AGRUPAMENTO POR SÍMBOLO ficam numa única implementação, compartilhada com o
        // resto do app.
        try
        {
            var imported = new PdfImporter(Options).Import(_pdfPath);
            var page = imported.Pages.FirstOrDefault();

            ImportedPageSettings = page?.Settings;
            ImportedShapes = page?.Objects.OfType<ShapeItem>().ToList() ?? new List<ShapeItem>();
            ImportedTexts = page?.Objects.OfType<TextItem>().ToList() ?? new List<TextItem>();
        }
        catch (Exception ex)
        {
            Log($"  ERRO ao importar: {ex.Message}");
            MessageBox.Show($"Erro ao importar PDF: {ex.Message}", "Erro",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log($"  Total: {ImportedShapes.Count} shapes, {ImportedTexts.Count} textos importados");
        DialogResult = true;
    }
}
