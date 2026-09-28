using System.Windows;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>
/// Diálogo de seleção de potencial: permite mesclar dois potenciais de condutores.
/// O usuário seleciona um potencial de origem e um de destino para mesclar.
/// </summary>
public partial class PotentialSelectorWindow : Window
{
    /// <summary>Origem selecionada para a mesclagem (nome/cor do potencial).</summary>
    public string? SelectedSource { get; private set; }

    /// <summary>Destino selecionado para a mesclagem (nome/cor do potencial).</summary>
    public string? SelectedTarget { get; private set; }

    /// <summary>Cores de condutor disponíveis no projeto.</summary>
    public IReadOnlyList<string> ConductorColors { get; }

    public PotentialSelectorWindow(IReadOnlyList<string> conductorColors)
    {
        InitializeComponent();
        ConductorColors = conductorColors;

        LstSourceColors.ItemsSource = conductorColors;
        LstTargetColors.ItemsSource = conductorColors;
    }

    /// <summary>Construtor para design-time.</summary>
    public PotentialSelectorWindow()
    {
        InitializeComponent();
        ConductorColors = new List<string> { "black", "red", "blue", "green", "yellow", "gray", "white" };
        LstSourceColors.ItemsSource = ConductorColors;
        LstTargetColors.ItemsSource = ConductorColors;
    }

    private void OnMergeClick(object sender, RoutedEventArgs e)
    {
        if (LstSourceColors.SelectedItem is not string source)
        {
            MessageBox.Show("Selecione um potencial de origem.", "Mesclar potencial",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (LstTargetColors.SelectedItem is not string target)
        {
            MessageBox.Show("Selecione um potencial de destino.", "Mesclar potencial",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (source == target)
        {
            MessageBox.Show("Selecione dois potenciais diferentes para mesclar.", "Mesclar potencial",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedSource = source;
        SelectedTarget = target;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
