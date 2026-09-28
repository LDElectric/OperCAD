using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OperCAD.UI;

/// <summary>Gerencia o ícone na bandeja do sistema via Win32 P/Invoke (sem Forms).</summary>
public static class TrayIconHelper
{
    private const int WM_TRAYICON = 0x800;
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_DBLCLICK = 0x08;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    private static NOTIFYICONDATA _nid;
    private static HwndSource? _hwndSource;
    private static Window? _mainWindow;
    private static bool _windowClosing;

    public static void Initialize(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _windowClosing = false;

        // Garante que o handle da janela esteja criado antes de usar
        var helper = new WindowInteropHelper(mainWindow);
        if (helper.Handle == IntPtr.Zero)
            mainWindow.SourceInitialized += OnMainWindowSourceInitialized;
        else
            SetupTrayIcon(helper);
    }

    private static void OnMainWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (_mainWindow is null) return;
        _mainWindow.SourceInitialized -= OnMainWindowSourceInitialized;
        SetupTrayIcon(new WindowInteropHelper(_mainWindow));
    }

    private static void SetupTrayIcon(WindowInteropHelper helper)
    {
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = helper.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_DBLCLICK,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadAppIcon(),
            szTip = "OperCAD - Diagramas Técnicos"
        };

        Shell_NotifyIcon(NIM_ADD, ref _nid);

        if (_mainWindow is not null)
        {
            _mainWindow.StateChanged += (_, _) =>
            {
                if (_mainWindow.WindowState == WindowState.Minimized)
                    _mainWindow.Hide();
            };
        }
    }

    /// <summary>Carrega o ícone do aplicativo (app.ico) para a bandeja; cai no ícone
    /// genérico do Windows se o arquivo não estiver disponível.</summary>
    private static IntPtr LoadAppIcon()
    {
        // IMAGE_ICON = 1; LR_LOADFROMFILE = 0x10
        const uint IMAGE_ICON = 1;
        const uint LR_LOADFROMFILE = 0x10;

        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "OperCAD_app.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/OperCAD.UI;component/Resources/app.ico"));
            if (streamInfo != null)
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    streamInfo.Stream.CopyTo(fs);
                }
                return LoadImage(IntPtr.Zero, tempPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            }
        }
        catch { }

        return LoadIcon(IntPtr.Zero, (IntPtr)0x7F00); // IDI_APPLICATION
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32() & 0xFFFF;
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                // O callback da bandeja pode chegar durante OnClosing. Nesse intervalo o WPF
                // proíbe Show/Activate/EnsureHandle e lança InvalidOperationException.
                if (_windowClosing || _mainWindow is null ||
                    _mainWindow.Dispatcher.HasShutdownStarted || !_mainWindow.IsLoaded)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                _mainWindow?.Show();
                if (_mainWindow != null)
                    _mainWindow.WindowState = WindowState.Normal;
                _mainWindow?.Activate();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Bloqueia reabertura pela bandeja a partir do início do fechamento da janela.</summary>
    public static void NotifyWindowClosing()
    {
        _windowClosing = true;
    }

    public static void Dispose()
    {
        _windowClosing = true;
        Shell_NotifyIcon(NIM_DELETE, ref _nid);
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _mainWindow = null;
    }
}
