using System.Text.Json;

namespace OperCAD.Application;

/// <summary>Um arquivo recente para exibição no menu (nome amigável + caminho completo).</summary>
public sealed record RecentFileItem(string Path, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Gerencia a lista de arquivos recentes do OperCAD (análogo ao
/// "Récemment ouverts" do padrão CAD). Persiste em
/// %LocalAppData%\OperCAD\recentfiles.json (ou no diretório configurado).
/// </summary>
public sealed class RecentFilesService
{
    private const int MaxItems = 10;
    private readonly string _storePath;

    public RecentFilesService(string? storeDirectory = null)
    {
        var dir = storeDirectory ?? PortablePath.DataDirectory;
        _storePath = Path.Combine(dir, "recentfiles.json");
    }

    public string StorePath => _storePath;

    /// <summary>Carrega a lista persistida, mantendo apenas arquivos que ainda existem.</summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_storePath))
                return Array.Empty<string>();
            var json = File.ReadAllText(_storePath);
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return list.Where(File.Exists).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Persiste a lista (recorta para MaxItems).</summary>
    public void Save(IEnumerable<string> paths)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(MaxItems).ToList();
            File.WriteAllText(_storePath, JsonSerializer.Serialize(list));
        }
        catch
        {
            // falha silenciosa: recentes é recurso não-crítico
        }
    }

    /// <summary>Adiciona um caminho ao topo da lista (deduplicando) e retorna a nova lista.</summary>
    public static List<string> Add(IEnumerable<string> current, string path)
    {
        var list = new List<string> { path };
        foreach (var p in current)
        {
            if (!string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                list.Add(p);
        }
        return list.Take(MaxItems).ToList();
    }

    public static IReadOnlyList<RecentFileItem> ToItems(IEnumerable<string> paths)
        => paths.Select(p => new RecentFileItem(p, Path.GetFileName(p))).ToList();
}
