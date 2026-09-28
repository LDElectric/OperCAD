using System.Windows;
using System.Windows.Threading;
using OperCAD.UI;
using OperCAD.UI.Views;

namespace OperCAD.App;

public partial class App : System.Windows.Application
{
    private string CrashLogPath
        => System.IO.Path.Combine(OperCAD.Application.PortablePath.ExecutableDirectory, "crash_log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        ResourceExtractor.ExtractIfNeeded();

        // Handler global para exceções não tratadas
        DispatcherUnhandledException += (_, args) =>
        {
            var detail = args.Exception.ToString();
            System.IO.File.WriteAllText(CrashLogPath, detail);
            System.Diagnostics.Debug.WriteLine($"[OperCAD] Exceção tratada: {detail}");
            MessageBox.Show(
                $"Erro interno:\n{detail}\n\nDetalhes completos salvos em:\n{CrashLogPath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                System.IO.File.WriteAllText(CrashLogPath, ex.ToString());
                System.Diagnostics.Debug.WriteLine($"[OperCAD] Exceção não manipulada: {ex}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[OperCAD] Task exception: {args.Exception.Message}");
            args.SetObserved();
        };

        if (!SingleInstanceHelper.TryAcquire())
        {
            MessageBox.Show("O OperCAD já está em execução.", "OperCAD", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var initialFile = e.Args.FirstOrDefault(a =>
            a.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            a.EndsWith(".opcad", StringComparison.OrdinalIgnoreCase) ||
            a.EndsWith(".titleblock", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(initialFile);
        MainWindow = window;
        window.Show();
        TrayIconHelper.Initialize(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { TrayIconHelper.Dispose(); } catch { }
        try { SingleInstanceHelper.Release(); } catch { }
        base.OnExit(e);
    }
}
