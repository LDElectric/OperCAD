using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OperCAD.Application;
using OperCAD.Core.Elements;

namespace OperCAD.UI.Views;

/// <summary>Item exibível do seletor de elementos.</summary>
public sealed record ElementPickItem(string Display, string Category, ElementDefinition Definition);

/// <summary>Janela para escolher um elemento da biblioteca (usada na marcação de amostras).</summary>
public partial class ElementPickerWindow : Window
{
    private readonly IReadOnlyList<ElementPickItem> _all;
    private readonly ObservableCollection<ElementPickItem> _filtered = new();

    public ElementDefinition? SelectedElement { get; private set; }

    public ElementPickerWindow(IReadOnlyList<ElementDefinition>? elements = null)
    {
        InitializeComponent();

        var lib = elements
            ?? new ElementCollectionService().LoadAll();

        _all = lib
            .Where(e => !e.IsConductor)
            .OrderBy(e => e.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new ElementPickItem(DisplayName(e), e.Category, e))
            .ToList();

        ListElements.ItemsSource = _filtered;
        ApplyFilter(string.Empty);
    }

    public static string DisplayName(ElementDefinition def)
    {
        if (def.Names.TryGetValue("pt", out var pt) && !string.IsNullOrEmpty(pt))
            return pt;
        return def.Name;
    }

    private void ApplyFilter(string text)
    {
        _filtered.Clear();
        var t = text?.Trim() ?? string.Empty;
        foreach (var item in _all)
        {
            if (t.Length == 0
                || item.Display.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                || item.Category.Contains(t, StringComparison.CurrentCultureIgnoreCase))
                _filtered.Add(item);
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter(TxtFilter.Text);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ListElements.SelectedItem is ElementPickItem item)
        {
            SelectedElement = item.Definition;
            DialogResult = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (ListElements.SelectedItem is ElementPickItem item)
        {
            SelectedElement = item.Definition;
            DialogResult = true;
        }
        else
        {
            MessageBox.Show("Selecione um elemento da lista.", "Marcar elemento",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
