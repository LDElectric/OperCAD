using System.Globalization;
using System.Windows;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>Editor de uma célula do legenda (linha/coluna/spans, textos e alinhamento).</summary>
public partial class TitleBlockCellWindow : Window
{
    private readonly TitleBlockCell _cell;

    public TitleBlockCellWindow(TitleBlockCell cell)
    {
        InitializeComponent();
        _cell = cell;

        CmbAlign.ItemsSource = new[] { "left", "center", "right" };
        CmbValign.ItemsSource = new[] { "top", "center", "bottom" };

        TxtRow.Text = cell.Row.ToString(CultureInfo.InvariantCulture);
        TxtCol.Text = cell.Col.ToString(CultureInfo.InvariantCulture);
        TxtRowSpan.Text = cell.RowSpan.ToString(CultureInfo.InvariantCulture);
        TxtColSpan.Text = cell.ColSpan.ToString(CultureInfo.InvariantCulture);
        TxtName.Text = cell.Name;
        TxtValue.Text = cell.Value;
        TxtLabel.Text = cell.Label;
        CmbAlign.SelectedItem = cell.Align;
        CmbValign.SelectedItem = cell.Valign;
        ChkDisplayLabel.IsChecked = cell.DisplayLabel;
        ChkHadjust.IsChecked = cell.Hadjust;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtRow.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) ||
            !int.TryParse(TxtCol.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var col))
        {
            MessageBox.Show(this, "Informe números válidos para linha e coluna.", "Célula",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _cell.Row = row;
        _cell.Col = col;
        _cell.RowSpan = ParseInt(TxtRowSpan.Text, _cell.RowSpan);
        _cell.ColSpan = ParseInt(TxtColSpan.Text, _cell.ColSpan);
        _cell.Name = TxtName.Text ?? string.Empty;
        _cell.Value = TxtValue.Text ?? string.Empty;
        _cell.Label = TxtLabel.Text ?? string.Empty;
        _cell.Align = (string)CmbAlign.SelectedItem ?? "center";
        _cell.Valign = (string)CmbValign.SelectedItem ?? "center";
        _cell.DisplayLabel = ChkDisplayLabel.IsChecked == true;
        _cell.Hadjust = ChkHadjust.IsChecked == true;
        DialogResult = true;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
