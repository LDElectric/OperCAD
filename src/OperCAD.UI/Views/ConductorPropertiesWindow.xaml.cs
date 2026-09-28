using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>
/// Propriedades do condutor (port do propriedades do condutor do padrão CAD):
/// número/fórmula, cor, espessura, cor do texto, tipo de fio, seção, cabo, barramento, função, tensão e exibição.
/// </summary>
public partial class ConductorPropertiesWindow : Window
{
    private readonly Conductor _conductor;

    private static readonly (string Name, string Hex)[] PredefinedColors = new[]
    {
        ("Preto", "#000000"),
        ("Vermelho", "#FF0000"),
        ("Azul", "#0000FF"),
        ("Verde", "#00AA00"),
        ("Amarelo", "#FFD700"),
        ("Laranja", "#FF8C00"),
        ("Roxo", "#800080"),
        ("Marrom", "#8B4513"),
        ("Cinza", "#808080"),
        ("Rosa", "#FF69B4")
    };

    private static readonly (string Label, double Mm)[] ThicknessOptions = new[]
    {
        ("Fino (1.2 mm)", 1.2),
        ("Médio (1.8 mm)", 1.8),
        ("Grosso (2.5 mm)", 2.5)
    };

    public ConductorPropertiesWindow(Conductor conductor)
    {
        InitializeComponent();
        _conductor = conductor;

        // Cores predefinidas
        CmbColor.ItemsSource = PredefinedColors;
        CmbColor.DisplayMemberPath = "Name";
        CmbColor.SelectedValuePath = "Hex";
        CmbColor.SelectedValue = PredefinedColors.FirstOrDefault(c =>
            string.Equals(c.Hex, ColorToHex(conductor.Color), StringComparison.OrdinalIgnoreCase)).Hex
            ?? "#000000";

        // Espessuras predefinidas
        CmbWidth.ItemsSource = ThicknessOptions;
        CmbWidth.DisplayMemberPath = "Label";
        CmbWidth.SelectedValuePath = "Mm";
        var closestThickness = ThicknessOptions
            .OrderBy(t => Math.Abs(t.Mm - conductor.Width))
            .First();
        CmbWidth.SelectedValue = closestThickness.Mm;

        TxtText.Text = conductor.Text;
        TxtFormula.Text = conductor.Formula;
        TxtTextColor.Text = conductor.TextColor ?? "#000000";
        TxtSection.Text = conductor.Section;
        TxtCable.Text = conductor.Cable;

        // Tipo de fio: seleciona o item correspondente
        CmbWireType.SelectedIndex = conductor.WireType switch
        {
            "flex" => 1,
            "rigid" => 2,
            "stranded" => 3,
            "coaxial" => 4,
            "fiber" => 5,
            _ => 0
        };

        TxtBus.Text = conductor.Bus;
        TxtFunction.Text = conductor.Function;
        TxtTension.Text = conductor.TensionProtocol;
        TxtNumSize.Text = conductor.NumSize.ToString(CultureInfo.InvariantCulture);
        ChkDisplayText.IsChecked = conductor.DisplayText;
    }

    private static string ColorToHex(string color)
    {
        if (string.IsNullOrEmpty(color))
            return "#000000";
        if (color.StartsWith("#"))
            return color;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(color);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch
        {
            return "#000000";
        }
    }

    private void OnPickTextColor(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = (Color)ColorConverter.ConvertFromString(TxtTextColor.Text);
            var dlg = new ColorPickerWindow(current);
            if (dlg.ShowDialog() == true)
                TxtTextColor.Text = $"#{dlg.SelectedColor.R:X2}{dlg.SelectedColor.G:X2}{dlg.SelectedColor.B:X2}";
        }
        catch
        {
            var dlg = new ColorPickerWindow();
            if (dlg.ShowDialog() == true)
                TxtTextColor.Text = $"#{dlg.SelectedColor.R:X2}{dlg.SelectedColor.G:X2}{dlg.SelectedColor.B:X2}";
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _conductor.Text = TxtText.Text ?? string.Empty;
        _conductor.Formula = TxtFormula.Text ?? string.Empty;
        if (CmbColor.SelectedValue is string hexColor)
            _conductor.Color = hexColor;
        if (CmbWidth.SelectedValue is double width)
            _conductor.Width = width;
        _conductor.TextColor = string.IsNullOrWhiteSpace(TxtTextColor.Text) ? "#000000" : TxtTextColor.Text.Trim();
        _conductor.Section = TxtSection.Text ?? string.Empty;
        _conductor.Cable = TxtCable.Text ?? string.Empty;
        _conductor.WireType = CmbWireType.SelectedIndex switch
        {
            1 => "flex",
            2 => "rigid",
            3 => "stranded",
            4 => "coaxial",
            5 => "fiber",
            _ => string.Empty
        };
        _conductor.Bus = TxtBus.Text ?? string.Empty;
        _conductor.Function = TxtFunction.Text ?? string.Empty;
        _conductor.TensionProtocol = TxtTension.Text ?? string.Empty;
        if (int.TryParse(TxtNumSize.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0)
            _conductor.NumSize = size;
        _conductor.DisplayText = ChkDisplayText.IsChecked == true;
        if (int.TryParse(System.Text.RegularExpressions.Regex.Match(_conductor.Text, @"(\d+)$").Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
            _conductor.SequenceNumber = seq;
        DialogResult = true;
    }
}
