using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OperCAD.Application;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>
/// Diálogo de configuração do OperCAD (análogo ao diálogo de configuração do padrão CAD):
/// páginas Geral, Nova folha, Exportação e Impressão.
/// </summary>
public partial class ConfigurationWindow : Window
{
    private readonly AppSettingsService _service;
    private readonly AppSettings _settings;

    public ConfigurationWindow(AppSettingsService? service = null)
    {
        InitializeComponent();
        _service = service ?? AppSettingsService.Default;
        _settings = _service.Load();
        LoadSettings();
    }

    private void LoadSettings()
    {
        // Geral
        TxtMaxElements.Text = _settings.MaxElementsInList.ToString(CultureInfo.InvariantCulture);
        CmbGridStyle.SelectedIndex = _settings.GridStyle == "dots" ? 1 : 0;
        TxtGridSpacing.Text = _settings.GridSpacing.ToString("0.##", CultureInfo.InvariantCulture);
        ChkSnapGrid.IsChecked = _settings.SnapToGrid;

        // Nova folha
        CmbNewSize.ItemsSource = Enum.GetValues(typeof(PaperSize));
        CmbNewSize.SelectedItem = Enum.TryParse<PaperSize>(_settings.NewPageSize, out var size) ? size : PaperSize.A4;
        TxtNewColumns.Text = _settings.NewPageColumns.ToString(CultureInfo.InvariantCulture);
        TxtNewRows.Text = _settings.NewPageRows.ToString(CultureInfo.InvariantCulture);
        ChkNewFrame.IsChecked = _settings.NewPageShowFrame;

        // Exportação
        CmbExportFormat.ItemsSource = new[] { "png" };
        CmbExportFormat.SelectedIndex = 0;
        TxtExportResolution.Text = _settings.ExportResolutionPxPerMm.ToString("0.##", CultureInfo.InvariantCulture);
        TxtExportFolder.Text = string.IsNullOrWhiteSpace(_settings.ExportFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : _settings.ExportFolder;

        // Impressão
        ChkPrintFrame.IsChecked = _settings.PrintDrawFrame;
        ChkPrintTitleBlock.IsChecked = _settings.PrintDrawTitleBlock;
        ChkPrintColors.IsChecked = _settings.PrintKeepConductorColors;
        ChkPrintTerminals.IsChecked = _settings.PrintDrawTerminals;
        ChkPrintFit.IsChecked = _settings.PrintFitInPage;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TxtMaxElements.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && max > 0)
            _settings.MaxElementsInList = max;
        _settings.GridStyle = CmbGridStyle.SelectedIndex == 1 ? "dots" : "lines";
        if (double.TryParse(TxtGridSpacing.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var spacing) && spacing >= 1)
            _settings.GridSpacing = spacing;
        _settings.SnapToGrid = ChkSnapGrid.IsChecked == true;

        if (CmbNewSize.SelectedItem is PaperSize size)
            _settings.NewPageSize = size.ToString();
        if (int.TryParse(TxtNewColumns.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cols) && cols > 0)
            _settings.NewPageColumns = cols;
        if (int.TryParse(TxtNewRows.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) && rows > 0)
            _settings.NewPageRows = rows;
        _settings.NewPageShowFrame = ChkNewFrame.IsChecked == true;

        if (double.TryParse(TxtExportResolution.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var res) && res > 0)
            _settings.ExportResolutionPxPerMm = res;
        _settings.ExportFolder = TxtExportFolder.Text.Trim();

        _settings.PrintDrawFrame = ChkPrintFrame.IsChecked == true;
        _settings.PrintDrawTitleBlock = ChkPrintTitleBlock.IsChecked == true;
        _settings.PrintKeepConductorColors = ChkPrintColors.IsChecked == true;
        _settings.PrintDrawTerminals = ChkPrintTerminals.IsChecked == true;
        _settings.PrintFitInPage = ChkPrintFit.IsChecked == true;

        _service.Save(_settings);
        DialogResult = true;
    }

    private void OnBrowseExportFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Selecionar pasta padrão de exportação",
            InitialDirectory = Directory.Exists(TxtExportFolder.Text)
                ? TxtExportFolder.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog() == true)
            TxtExportFolder.Text = dlg.FolderName;
    }
}
