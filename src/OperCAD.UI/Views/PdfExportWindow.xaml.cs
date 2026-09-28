using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OperCAD.Core.Documents;
using OperCAD.Drawing;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Views;

/// <summary>Linha de seleção de uma folha para exportar em PDF.</summary>
public sealed partial class PdfExportRow : ObservableObject
{
    public Page Page { get; }
    public string Title => Page.Name;

    [ObservableProperty] private bool _isChecked = true;

    public PdfExportRow(Page page) => Page = page;
}

/// <summary>
/// Janela de exportação em PDF: permite escolher 1 ou várias folhas do projeto, que são
/// gravadas juntas num único arquivo PDF (cada folha vira uma página).
/// </summary>
public partial class PdfExportWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<PdfExportRow> _rows;

    public PdfExportWindow(Project project)
    {
        InitializeComponent();
        _rows = new System.Collections.ObjectModel.ObservableCollection<PdfExportRow>(
            project.Pages.Select(p => new PdfExportRow(p)));
        GridFolhas.ItemsSource = _rows;
    }

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

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsChecked).Select(r => r.Page).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Selecione ao menos uma folha para exportar.", "Exportar PDF",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Documento PDF (*.pdf)|*.pdf",
            FileName = selected.Count == 1 ? selected[0].Name : "projeto"
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            PageExporter.ExportPdf(selected, dlg.FileName);
            DialogResult = true;
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("Erro ao exportar PDF: " + ex.Message, "Exportar PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
