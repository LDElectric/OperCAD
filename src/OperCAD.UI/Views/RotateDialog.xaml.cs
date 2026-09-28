using System.Globalization;
using System.Windows;

namespace OperCAD.UI.Views;

public partial class RotateDialog : Window
{
    public double AngleDegrees { get; private set; } = 90;

    public RotateDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(TxtAngle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var angle))
        {
            AngleDegrees = angle;
            DialogResult = true;
        }
        else
        {
            MessageBox.Show("Digite um valor numérico válido.", "Rotacionar seleção",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Rotação fixa (0°/90°/180°/270°) clicada: aplica imediatamente.</summary>
    private void OnFixedAngle(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string tag } &&
            double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var angle))
        {
            AngleDegrees = angle;
            DialogResult = true;
        }
    }
}
