using System.Threading;

namespace OperCAD.UI;

/// <summary>Garante que apenas uma instância da aplicação esteja em execução.</summary>
public static class SingleInstanceHelper
{
    private static Mutex? _mutex;

    public static bool IsFirstInstance => _mutex is not null;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, "OperCAD_SingleInstance", out bool created);
        return created;
    }

    public static void Release()
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }
}
