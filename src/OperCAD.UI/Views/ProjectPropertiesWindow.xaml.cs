using System.Windows;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>
/// Diálogo de propriedades do projeto — permite editar o nome e o título.
/// </summary>
public partial class ProjectPropertiesWindow : Window
{
    private readonly Project _project;

    public ProjectPropertiesWindow(Project project)
    {
        InitializeComponent();
        _project = project;

        TxtName.Text = project.Name;
        TxtTitle.Text = project.Title ?? string.Empty;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _project.Name = TxtName.Text.Trim();
        _project.Title = TxtTitle.Text.Trim();
        DialogResult = true;
    }
}
