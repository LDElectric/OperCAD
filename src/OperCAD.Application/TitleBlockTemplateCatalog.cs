using OperCAD.Core.Documents;

namespace OperCAD.Application;

/// <summary>
/// Catálogo dos modelos de legenda do OperCAD (pasta "titleblocks" ao lado do executável).
/// Os modelos acompanham o aplicativo, sem dependência de arquivos externos.
/// </summary>
public sealed class TitleBlockTemplateCatalog
{
    private static readonly Lazy<TitleBlockTemplateCatalog> DefaultInstance =
        new(() => new TitleBlockTemplateCatalog());

    /// <summary>Catálogo compartilhado da instalação padrão do OperCAD (com cache).</summary>
    public static TitleBlockTemplateCatalog Default => DefaultInstance.Value;

    private readonly string _rootPath;
    private IReadOnlyList<TitleBlockTemplate>? _cache;

    public TitleBlockTemplateCatalog(string? rootPath = null)
    {
        _rootPath = rootPath ?? DefaultPath();
    }

    /// <summary>Pasta padrão dos templates de legenda (embutidos ao lado do exe).</summary>
    public static string DefaultPath()
        => Path.Combine(PortablePath.ExecutableDirectory, "titleblocks");

    /// <summary>Limpa o cache (após editar/salvar um template no editor de legendas).</summary>
    public void Invalidate() => _cache = null;

    public string RootPath => _rootPath;

    public bool IsAvailable => !string.IsNullOrEmpty(_rootPath) && Directory.Exists(_rootPath);

    /// <summary>Carrega todos os templates disponíveis (tolerante a arquivos malformados).</summary>
    public IReadOnlyList<TitleBlockTemplate> LoadAll()
    {
        if (_cache is not null)
            return _cache;

        var list = new List<TitleBlockTemplate>();
        if (IsAvailable)
        {
            foreach (var file in Directory.EnumerateFiles(_rootPath, "*.titleblock"))
            {
                var tpl = TitleBlockTemplateImporter.Parse(file);
                if (tpl is not null)
                    list.Add(tpl);
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _cache = list;
        return list;
    }

    /// <summary>Busca um template pelo nome do arquivo.</summary>
    public TitleBlockTemplate? Get(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        return LoadAll().FirstOrDefault(t =>
            string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }
}
