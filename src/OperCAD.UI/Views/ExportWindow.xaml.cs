using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Drawing;
using OperCAD.UI.Controls;
using SkiaSharp;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Views;

/// <summary>Linha de exportação de uma folha (checkbox, nome de arquivo, dimensões px).</summary>
public sealed partial class ExportRow : ObservableObject
{
    public Page Page { get; }

    public string Title => Page.Name;

    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private string _fileName;
    [ObservableProperty] private string _widthPx;
    [ObservableProperty] private string _heightPx;
    [ObservableProperty] private bool _keepRatio = true;

    public ExportRow(Page page, AppSettings settings)
    {
        Page = page;
        _fileName = page.Name + ".png";
        _widthPx = ((int)(page.Settings.Width * settings.ExportResolutionPxPerMm)).ToString(CultureInfo.InvariantCulture);
        _heightPx = ((int)(page.Settings.Height * settings.ExportResolutionPxPerMm)).ToString(CultureInfo.InvariantCulture);
    }

    public int ParseWidth => int.TryParse(WidthPx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int ParseHeight => int.TryParse(HeightPx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// Janela de exportação de folhas (análoga ao diálogo de exportação do padrão CAD): escolhe os
/// folhas, o nome de arquivo e as dimensões em px de cada um. Suporta PNG, JPG, BMP e SVG.
/// </summary>
public partial class ExportWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<ExportRow> _rows;
    private readonly Project _project;

    public ExportWindow(Project project, AppSettings settings)
    {
        InitializeComponent();
        _project = project;
        _rows = new System.Collections.ObjectModel.ObservableCollection<ExportRow>(
            project.Pages.Select(p => new ExportRow(p, settings)));
        ListaFolhas.ItemsSource = _rows;

        // Inicializa opções de escala
        CmbScale.SelectedIndex = settings.ExportImageScale switch
        {
            2.0 => 1,
            0.5 => 2,
            _ => 0
        };
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        // Mostra/opções SVG apenas quando SVG está selecionado
        var isSvg = CmbFormat.SelectedIndex == 3;
        SvgOptionsPanel.Visibility = isSvg ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Escala de exportação selecionada.</summary>
    private double SelectedScale => CmbScale.SelectedIndex switch
    {
        1 => 2.0,
        2 => 0.5,
        _ => 1.0
    };

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsChecked = true;
    }

    private void OnUncheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsChecked = false;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Nenhuma folha selecionada.", "Exportar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folderDlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pasta de destino dos arquivos exportados"
        };
        if (folderDlg.ShowDialog() != true)
            return;

        var formatIndex = CmbFormat.SelectedIndex;

        // Exportação SVG (formato vetorial)
        if (formatIndex == 3)
        {
            ExportSvg(selected, folderDlg.FolderName);
            return;
        }

        var formatExt = formatIndex switch
        {
            1 => ".jpg",
            2 => ".bmp",
            _ => ".png"
        };
        var formatEncode = formatIndex switch
        {
            1 => SKEncodedImageFormat.Jpeg,
            2 => SKEncodedImageFormat.Png, // BMP via SkiaSharp não suportado diretamente, usa Png
            _ => SKEncodedImageFormat.Png
        };
        var quality = formatIndex == 1 ? 90 : 100;

        var exported = 0;
        var errors = 0;
        var options = new SceneRenderer.RenderOptions(
            DrawTerminals: ChkTerminals.IsChecked == true,
            DrawTitleBlock: ChkExportTitleBlock.IsChecked == true);
        var scale = SelectedScale;
        foreach (var row in selected)
        {
            var w = (int)(row.ParseWidth * scale);
            var h = (int)(row.ParseHeight * scale);
            if (w <= 0 || h <= 0)
            {
                errors++;
                continue;
            }
            // Ajusta a extensão do nome de arquivo
            var fileName = System.IO.Path.ChangeExtension(Sanitize(row.FileName), formatExt);
            var filePath = System.IO.Path.Combine(folderDlg.FolderName, fileName);
            try
            {
                using var bmp = PageExporter.RenderBitmapSized(row.Page, w, h, row.KeepRatio, options);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(formatEncode, quality);
                using var fs = System.IO.File.Create(filePath);
                data.SaveTo(fs);
                exported++;
            }
            catch
            {
                errors++;
            }
        }

        MessageBox.Show(
            errors == 0
                ? $"{exported} folio(s) exportado(s) para {folderDlg.FolderName}."
                : $"{exported} exportado(s), {errors} erro(s).",
            "Exportar",
            MessageBoxButton.OK,
            errors == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (errors == 0)
            DialogResult = true;
    }

    /// <summary>Exporta folhas selecionadas como SVG (formato vetorial).</summary>
    private void ExportSvg(List<ExportRow> selected, string folderPath)
    {
        var includeFrame = ChkSvgIncludeFrame.IsChecked == true;
        var transparentBg = ChkSvgTransparent.IsChecked == true;
        var exported = 0;
        var errors = 0;

        foreach (var row in selected)
        {
            var fileName = System.IO.Path.ChangeExtension(Sanitize(row.FileName), ".svg");
            var filePath = System.IO.Path.Combine(folderPath, fileName);
            try
            {
                SvgExporter.SaveToFile(row.Page, filePath, includeFrame, transparentBg);
                exported++;
            }
            catch
            {
                errors++;
            }
        }

        MessageBox.Show(
            errors == 0
                ? $"{exported} folio(s) SVG exportado(s) para {folderPath}."
                : $"{exported} exportado(s), {errors} erro(s).",
            "Exportar SVG",
            MessageBoxButton.OK,
            errors == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (errors == 0)
            DialogResult = true;
    }

    /// <summary>Copia a primeira folha marcada para a área de transferência (como o "Copier" do padrão CAD).</summary>
    private void OnCopyToClipboard(object sender, RoutedEventArgs e)
    {
        var row = _rows.FirstOrDefault(r => r.IsChecked);
        if (row is null)
        {
            MessageBox.Show("Marque uma folha para copiar.", "Copiar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = row.ParseWidth;
        var h = row.ParseHeight;
        if (w <= 0 || h <= 0)
            return;
        var options = new SceneRenderer.RenderOptions(DrawTerminals: ChkTerminals.IsChecked == true);
        using var bmp = PageExporter.RenderBitmapSized(row.Page, w, h, row.KeepRatio, options);
        try
        {
            System.Windows.Clipboard.SetImage(SkiaBitmapExtensions.ToBitmapSource(bmp));
            MessageBox.Show("Folha copiada para a área de transferência.", "Copiar",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("Não foi possível acessar a área de transferência.", "Copiar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "folha.png" : cleaned;
    }
}
