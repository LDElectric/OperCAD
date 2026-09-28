using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OperCAD.UI.Views;

/// <summary>Diálogo simples de seleção de cor (cores padrão + cor customizada).</summary>
public partial class ColorPickerWindow : Window
{
    private static readonly string[] PresetColors = new[]
    {
        "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF",
        "#FFFF00", "#FF00FF", "#00FFFF", "#808080", "#C0C0C0",
        "#800000", "#008000", "#000080", "#808000", "#800080",
        "#008080", "#FF6600", "#6600FF", "#00FF66", "#FF0066",
        "#990000", "#009900", "#000099", "#FF9900", "#9900FF",
        "#00FF99", "#FF3366", "#3366FF", "#66FF33", "#FF6633"
    };

    public System.Windows.Media.Color SelectedColor { get; private set; } = System.Windows.Media.Colors.Black;

    public ColorPickerWindow(System.Windows.Media.Color? initialColor = null)
    {
        InitializeComponent();
        if (initialColor.HasValue)
            SelectedColor = initialColor.Value;
        BuildPalette();
        UpdatePreview();
    }

    private void BuildPalette()
    {
        PalettePanel.Children.Clear();

        foreach (var hex in PresetColors)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            var border = new Border
            {
                Width = 28,
                Height = 28,
                Background = brush,
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                ToolTip = hex,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var color = brush.Color;
            border.MouseLeftButtonDown += (_, _) =>
            {
                SelectedColor = color;
                HexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                UpdatePreview();
            };

            PalettePanel.Children.Add(border);
        }
    }

    private void UpdatePreview()
    {
        PreviewBorder.Background = new SolidColorBrush(SelectedColor);
        if (!HexTextBox.Text.StartsWith("#"))
            HexTextBox.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            var text = HexTextBox.Text.Trim();
            if (!text.StartsWith("#"))
                text = "#" + text;
            SelectedColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(text);
            UpdatePreview();
        }
        catch { }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
