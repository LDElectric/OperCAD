using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace OperCAD.UI.Views;

/// <summary>
/// Diálogo "Sobre o OperCAD": exibe autores, licença e versão do assembly.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        TxtVersion.Text = version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build} (versão de testes)"
            : "0.1.0 (versão de testes)";
    }

    private void OnGitHubNavigate(object sender, RequestNavigateEventArgs e)
    {
        var uri = ((Hyperlink)sender).NavigateUri;
        if (uri is not null)
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }

        e.Handled = true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
