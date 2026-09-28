using System.Windows;
using OperCAD.Core.Elements;

namespace OperCAD.UI.Views;

/// <summary>Diálogo de propriedades de um terminal do editor de elementos.</summary>
public partial class TerminalEditorWindow : Window
{
    public string TerminalName { get; private set; } = "1";
    public char Orientation { get; private set; } = 'e';
    public string Type { get; private set; } = "Generic";

    public TerminalEditorWindow(ElementTerminal? existing = null)
    {
        InitializeComponent();

        var orientations = new[]
        {
            new { Label = "Norte (n)", Value = 'n' },
            new { Label = "Leste (e)", Value = 'e' },
            new { Label = "Sul (s)", Value = 's' },
            new { Label = "Oeste (w)", Value = 'w' }
        };
        CmbOrientation.ItemsSource = orientations;
        CmbOrientation.DisplayMemberPath = "Label";
        CmbType.ItemsSource = new[] { "Generic", "Power", "Neutral", "Ground", "ProtectiveEarth", "FunctionalEarth" };

        if (existing is not null)
        {
            TxtName.Text = existing.Name;
            CmbOrientation.SelectedItem = orientations.FirstOrDefault(o => o.Value == existing.Orientation) ?? orientations[1];
            CmbType.SelectedItem = existing.Type;
        }
        else
        {
            CmbOrientation.SelectedIndex = 1;
            CmbType.SelectedIndex = 0;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        TerminalName = string.IsNullOrWhiteSpace(TxtName.Text) ? "1" : TxtName.Text.Trim();
        if (CmbOrientation.SelectedItem is not null)
            Orientation = (char)CmbOrientation.SelectedItem.GetType().GetProperty("Value")!.GetValue(CmbOrientation.SelectedItem)!;
        if (CmbType.SelectedItem is string type)
            Type = type;
        DialogResult = true;
    }
}
