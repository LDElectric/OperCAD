using System.Windows;

namespace OperCAD.UI.Views;

public partial class NewProjectNameWindow : Window
{
    public string? ProjectName { get; private set; }

    public NewProjectNameWindow()
    {
        InitializeComponent();
        TxtProjectName.Focus();
        TxtProjectName.SelectAll();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = TxtProjectName.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Digite um nome para o projeto.", "OperCAD",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Valida caracteres inválidos
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) >= 0)
        {
            MessageBox.Show("O nome contém caracteres inválidos.", "OperCAD",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProjectName = name;
        DialogResult = true;
    }
}
