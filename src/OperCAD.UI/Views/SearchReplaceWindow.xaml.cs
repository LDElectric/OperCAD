using System.Windows;
using System.Windows.Controls;
using OperCAD.Application;
using OperCAD.Core.Documents;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Views;

/// <summary>Linha de resultado da busca para exibição na ListView.</summary>
public sealed class SearchResultRow
{
    public required string PageName { get; init; }
    public required string ObjectLabel { get; init; }
    public required string Property { get; init; }
    public required string Value { get; init; }
    public required SearchOccurrence Occurrence { get; init; }
}

/// <summary>
/// Janela de busca e substituição avançada com modo, filtro e lista de resultados.
/// </summary>
public partial class SearchReplaceWindow : Window
{
    private readonly Page _page;
    private List<SearchResultRow> _currentResults = new();

    public SearchReplaceWindow(Page page)
    {
        InitializeComponent();
        _page = page;
    }

    private SearchMode CurrentMode => CmbMode.SelectedIndex switch
    {
        1 => SearchMode.MatchExact,
        2 => SearchMode.MatchRegex,
        _ => SearchMode.Contains
    };

    private SearchFilter CurrentFilter => CmbFilter.SelectedIndex switch
    {
        1 => SearchFilter.ElementsOnly,
        2 => SearchFilter.ConductorsOnly,
        3 => SearchFilter.TextsOnly,
        _ => SearchFilter.All
    };

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        var searchText = TxtSearch.Text;
        if (string.IsNullOrEmpty(searchText))
            return;

        var caseSensitive = ChkCaseSensitive.IsChecked == true;
        var occurrences = DiagramSearch.Find(_page, searchText, caseSensitive, CurrentMode, CurrentFilter);

        _currentResults = occurrences.Select(o => new SearchResultRow
        {
            PageName = _page.Name,
            ObjectLabel = o.ObjectLabel,
            Property = o.Property,
            Value = o.Value,
            Occurrence = o
        }).ToList();

        ResultList.ItemsSource = _currentResults;
        TxtResultCount.Text = $"{_currentResults.Count} ocorrência(s) encontrada(s)";
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is not SearchResultRow selected)
        {
            MessageBox.Show("Selecione uma ocorrência para substituir.", "Substituir",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var replacement = TxtReplace.Text;
        var cmd = DiagramSearch.ReplaceSingle(selected.Occurrence, replacement);
        if (cmd is not null)
        {
            // Atualiza o valor exibido na linha
            var index = _currentResults.IndexOf(selected);
            if (index >= 0)
            {
                _currentResults[index] = new SearchResultRow
                {
                    PageName = selected.PageName,
                    ObjectLabel = selected.ObjectLabel,
                    Property = selected.Property,
                    Value = selected.Occurrence.Value,
                    Occurrence = selected.Occurrence
                };
                ResultList.ItemsSource = null;
                ResultList.ItemsSource = _currentResults;
            }
            ResultList.SelectedIndex = Math.Min(index + 1, _currentResults.Count - 1);
        }
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var searchText = TxtSearch.Text;
        var replacement = TxtReplace.Text;
        if (string.IsNullOrEmpty(searchText))
            return;

        var caseSensitive = ChkCaseSensitive.IsChecked == true;
        var cmd = DiagramSearch.ReplaceAll(_page, searchText, replacement, caseSensitive, CurrentMode, CurrentFilter);

        if (cmd is null)
        {
            MessageBox.Show("Nenhuma ocorrência encontrada.", "Substituir Todos",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Todas as ocorrências substituídas.", "Substituir Todos",
                MessageBoxButton.OK, MessageBoxImage.Information);
            // Limpa resultados após substituição
            _currentResults.Clear();
            ResultList.ItemsSource = null;
            TxtResultCount.Text = "0 ocorrência(s) encontrada(s)";
        }
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        // Pode ser usado para navegar até o objeto selecionado
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
