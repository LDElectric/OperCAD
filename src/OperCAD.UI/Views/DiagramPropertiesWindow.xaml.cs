using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OperCAD.Application;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>
/// Diálogo "Propriedades da folha": tamanho do papel, escala, quadro, margens e legenda.
/// </summary>
public partial class DiagramPropertiesWindow : Window
{
    private readonly OperCAD.Core.Documents.Page _page;

    public DiagramPropertiesWindow(OperCAD.Core.Documents.Page page)
    {
        InitializeComponent();
        _page = page;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _page.Settings;
        var t = _page.TitleBlock;

        // Tamanho do papel
        CmbSize.ItemsSource = Enum.GetValues(typeof(PaperSize));
        CmbSize.SelectedItem = s.Size;
        TxtCustomWidth.Text = s.CustomWidth.ToString("0.##", CultureInfo.InvariantCulture);
        TxtCustomHeight.Text = s.CustomHeight.ToString("0.##", CultureInfo.InvariantCulture);
        UpdateCustomDimensionsEnabled();
        CmbSize.SelectionChanged += (_, _) => UpdateCustomDimensionsEnabled();

        // Orientação
        var isLandscape = s.Width > s.Height;
        CmbOrientation.SelectedIndex = isLandscape ? 1 : 0;
        CmbOrientation.SelectionChanged += (_, _) =>
        {
            if (CmbSize.SelectedItem is PaperSize ps && ps != PaperSize.Custom)
            {
                var dims = ps.Mm();
                var isLand = CmbOrientation.SelectedIndex == 1;
                TxtCustomWidth.Text = (isLand ? dims.Height : dims.Width).ToString("0.##", CultureInfo.InvariantCulture);
                TxtCustomHeight.Text = (isLand ? dims.Width : dims.Height).ToString("0.##", CultureInfo.InvariantCulture);
            }
        };

        ChkFrame.IsChecked = s.ShowFrame;

        // Quadro
        ChkColumns.IsChecked = s.ShowColumns;
        TxtColumnsCount.Text = s.ColumnsCount.ToString(CultureInfo.InvariantCulture);
        TxtColumnsWidth.Text = s.ColumnsWidth.ToString("0.##", CultureInfo.InvariantCulture);
        TxtColumnsHeaderHeight.Text = s.ColumnsHeaderHeight.ToString("0.##", CultureInfo.InvariantCulture);
        ChkRows.IsChecked = s.ShowRows;
        TxtRowsCount.Text = s.RowsCount.ToString(CultureInfo.InvariantCulture);
        TxtRowsHeight.Text = s.RowsHeight.ToString("0.##", CultureInfo.InvariantCulture);
        TxtRowsHeaderWidth.Text = s.RowsHeaderWidth.ToString("0.##", CultureInfo.InvariantCulture);

        // Margens
        TxtMarginLeft.Text = s.MarginLeft.ToString("0.##", CultureInfo.InvariantCulture);
        TxtMarginRight.Text = s.MarginRight.ToString("0.##", CultureInfo.InvariantCulture);
        TxtMarginTop.Text = s.MarginTop.ToString("0.##", CultureInfo.InvariantCulture);
        TxtMarginBottom.Text = s.MarginBottom.ToString("0.##", CultureInfo.InvariantCulture);

        // Escala dos elementos — seleciona item cujo Tag corresponde ao ElementScale atual
        SelectElementScale(s.ElementScale);
        UpdateScaleNotation();

        // Legenda
        TxtTitle.Text = t.Title;
        TxtCompany.Text = t.Company;
        TxtObra.Text = t.Obra;
        TxtDrawingNumber.Text = t.DrawingNumber;
        TxtRevision.Text = t.Revision;
        TxtSheet.Text = t.Sheet;
        TxtTotalSheets.Text = t.TotalSheets;
        TxtAuthor.Text = t.Author;
        TxtReviewedBy.Text = t.ReviewedBy;
        TxtApprovedBy.Text = t.ApprovedBy;
        TxtDate.Text = DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private void SelectElementScale(double scale)
    {
        foreach (ComboBoxItem item in CmbElementScale.Items)
        {
            if (item.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) &&
                Math.Abs(val - scale) < 0.001)
            {
                CmbElementScale.SelectedItem = item;
                return;
            }
        }
        CmbElementScale.SelectedIndex = 4; // fallback: 1:1
    }

    private void OnScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateScaleNotation();
    }

    private void UpdateScaleNotation()
    {
        if (CmbElementScale.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
        {
            TxtScale.Text = ScaleToNotation(scale);
        }
    }

    private static string ScaleToNotation(double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.001) return "1:1";
        if (scale > 1.0) return $"{scale:0.#}:1";
        return $"1:{1.0 / scale:0.#}";
    }

    private void UpdateCustomDimensionsEnabled()
    {
        var isCustom = CmbSize.SelectedItem is PaperSize ps && ps == PaperSize.Custom;
        TxtCustomWidth.IsEnabled = isCustom;
        TxtCustomHeight.IsEnabled = isCustom;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var s = _page.Settings;
        var t = _page.TitleBlock;

        // Tamanho do papel
        if (CmbSize.SelectedItem is PaperSize size)
            s.Size = size;
        if (double.TryParse(TxtCustomWidth.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cw) && cw > 0)
            s.CustomWidth = cw;
        if (double.TryParse(TxtCustomHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ch) && ch > 0)
            s.CustomHeight = ch;

        s.ShowFrame = ChkFrame.IsChecked == true;

        // Ao ligar a moldura numa folha que ainda não tem nenhuma, aplica a folha padrão
        // do OperCAD (ABNT — template.json), para não existir moldura concorrente.
        if (s.ShowFrame && s.FolhaTemplate is null && string.IsNullOrEmpty(s.FolhaTemplateFile))
        {
            var folha = FolhaTemplateCatalog.Builtin();
            if (folha is not null)
            {
                s.FolhaTemplate = folha;
                s.FolhaTemplateFile = folha.FileName;
                s.FolhaFrameInset = folha.FrameInset > 0 ? folha.FrameInset : s.FolhaFrameInset;
                if (folha.ColumnsCount > 0) s.ColumnsCount = folha.ColumnsCount;
                if (folha.RowsCount > 0) s.RowsCount = folha.RowsCount;
            }
        }

        // Quadro
        s.ShowColumns = ChkColumns.IsChecked == true;
        if (int.TryParse(TxtColumnsCount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cols) && cols > 0)
            s.ColumnsCount = cols;
        if (double.TryParse(TxtColumnsWidth.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cw2) && cw2 > 0)
            s.ColumnsWidth = cw2;
        if (double.TryParse(TxtColumnsHeaderHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var chh) && chh > 0)
            s.ColumnsHeaderHeight = chh;
        s.ShowRows = ChkRows.IsChecked == true;
        if (int.TryParse(TxtRowsCount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) && rows > 0)
            s.RowsCount = rows;
        if (double.TryParse(TxtRowsHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rh) && rh > 0)
            s.RowsHeight = rh;
        if (double.TryParse(TxtRowsHeaderWidth.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rhw) && rhw > 0)
            s.RowsHeaderWidth = rhw;

        // Margens
        if (double.TryParse(TxtMarginLeft.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ml)) s.MarginLeft = ml;
        if (double.TryParse(TxtMarginRight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mr)) s.MarginRight = mr;
        if (double.TryParse(TxtMarginTop.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mt)) s.MarginTop = mt;
        if (double.TryParse(TxtMarginBottom.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)) s.MarginBottom = mb;

        // Escala dos elementos
        if (CmbElementScale.SelectedItem is ComboBoxItem sel && sel.Tag is string tag &&
            double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var esc) && esc > 0)
            s.ElementScale = esc;

        // Legenda — escala é automática (derivada da escala dos elementos)
        var oldSheet = t.Sheet;
        var oldTotalSheets = t.TotalSheets;
        t.Title = TxtTitle.Text;
        t.Company = TxtCompany.Text;
        t.Obra = TxtObra.Text;
        t.DrawingNumber = TxtDrawingNumber.Text;
        t.Revision = TxtRevision.Text;
        t.Scale = TxtScale.Text;
        t.Sheet = TxtSheet.Text;
        t.TotalSheets = TxtTotalSheets.Text;
        if (!string.Equals(oldSheet, t.Sheet, StringComparison.Ordinal))
            t.SheetAutoNumbered = false;
        if (!string.Equals(oldTotalSheets, t.TotalSheets, StringComparison.Ordinal))
            t.TotalSheetsAutoNumbered = false;
        t.Author = TxtAuthor.Text;
        t.ReviewedBy = TxtReviewedBy.Text;
        t.ApprovedBy = TxtApprovedBy.Text;
        t.Date = DateTime.Now;

        DialogResult = true;
    }
}
