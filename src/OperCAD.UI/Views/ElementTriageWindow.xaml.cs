using System.Collections.ObjectModel;
using System.Windows;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;

namespace OperCAD.UI.Views;

/// <summary>Item da lista de amostras marcadas.</summary>
public sealed record SampleItem(string Display, string KindLabel);

/// <summary>
/// Janela de reconhecimento assistido: marca o aglomerado selecionado como um elemento da
/// biblioteca (ou "não é elemento") e sugere rótulos por similaridade (triagem).
/// </summary>
public partial class ElementTriageWindow : Window
{
    private readonly Func<IReadOnlyList<ShapeItem>> _getSelectedShapes;
    private readonly ObservableCollection<SampleItem> _samples = new();

    public ElementTriageWindow(Func<IReadOnlyList<ShapeItem>> getSelectedShapes)
    {
        InitializeComponent();
        _getSelectedShapes = getSelectedShapes;
        ListSamples.ItemsSource = _samples;
        RefreshSelection();
        RefreshSamples();
    }

    private void RefreshSelection()
    {
        var shapes = _getSelectedShapes();
        TxtSelection.Text = shapes.Count == 0
            ? "Seleção: nenhuma (selecione primitivas na folha)"
            : $"Seleção: {shapes.Count} primitiva(s)";
    }

    private void RefreshSamples()
    {
        _samples.Clear();
        foreach (var e in ElementTriageService.Default.Entries)
            _samples.Add(new SampleItem(
                e.IsElement ? e.Label : "(negativa)",
                e.IsElement ? " · ELEMENTO" : " · NÃO é elemento"));
    }

    private void ShowSuggestion(string text)
    {
        SuggestBox.Visibility = Visibility.Visible;
        TxtSuggestion.Text = text;
    }

    private void OnMarkElement(object sender, RoutedEventArgs e)
    {
        var shapes = _getSelectedShapes();
        if (shapes.Count == 0)
        {
            MessageBox.Show("Selecione na folha as primitivas do símbolo antes de marcar.",
                "Marcar elemento", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new ElementPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedElement is { } def)
        {
            ElementTriageService.Default.MarkAsElement(ElementPickerWindow.DisplayName(def), shapes);
            RefreshSamples();
            ShowSuggestion($"Amostra marcada como elemento: {ElementPickerWindow.DisplayName(def)}");
        }
    }

    private void OnMarkNotElement(object sender, RoutedEventArgs e)
    {
        var shapes = _getSelectedShapes();
        if (shapes.Count == 0)
        {
            MessageBox.Show("Selecione na folha as primitivas antes de marcar como 'não é elemento'.",
                "Marcar elemento", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ElementTriageService.Default.MarkAsNotElement(shapes);
        RefreshSamples();
        ShowSuggestion("Amostra marcada como NÃO é elemento (negativa).");
    }

    private void OnSuggest(object sender, RoutedEventArgs e)
    {
        RefreshSelection();
        var shapes = _getSelectedShapes();
        if (shapes.Count == 0)
        {
            ShowSuggestion("Selecione um aglomerado de primitivas para sugerir um rótulo.");
            return;
        }
        if (ElementTriageService.Default.Suggest(shapes) is { } hit)
            ShowSuggestion($"Sugestão: {hit.Label}  (similaridade {hit.Score:0.##})");
        else
            ShowSuggestion("Não identificado — sem amostra parecida (ou parece 'não é elemento'). Marque mais amostras.");
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        ElementTriageService.Default.Clear();
        RefreshSamples();
        ShowSuggestion("Amostras limpas.");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
