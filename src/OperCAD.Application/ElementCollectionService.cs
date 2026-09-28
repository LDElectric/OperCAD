using OperCAD.Core.Elements;

namespace OperCAD.Application;

/// <summary>
/// Varre a coleção de elementos embutida no OperCAD (pastas "elements_opcad" e "elements_user"
/// ao lado do executável) e produz as definições de elementos. Suporta arquivos .opcad (JSON)
/// e .xml (formato XML, extensão alternativa para elementos).
/// </summary>
public sealed class ElementCollectionService
{
    private readonly string _opcadPath;
    private readonly string _userPath;

    /// <summary>
    /// Cache da coleção completa por chave (raiz|usuário). A biblioteca é estática durante
    /// uma sessão; parsear milhares de arquivos a cada chamada era muito lento nos testes.
    /// Usar <see cref="InvalidateLibraryCache"/> quando a coleção mudar (editor de elementos).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ElementDefinition>> Cache =
        new();

    /// <summary>Pasta padrão da coleção de elementos .opcad (biblioteca embutida ao lado do exe).</summary>
    public static string DefaultOpCadPath()
        => Path.Combine(PortablePath.ExecutableDirectory, "elements_opcad");

    /// <summary>Pasta padrão da coleção de elementos (alias para compatibilidade).</summary>
    public static string DefaultPath() => DefaultOpCadPath();

    /// <summary>Pasta padrão da coleção de elementos do usuário (portátil: ao lado do .exe).</summary>
    public static string DefaultUserPath()
        => Path.Combine(PortablePath.DataDirectory, "elements_user");

    /// <summary>Categoria raiz dos elementos do usuário (traduzida para "Usuário" na paleta).</summary>
    public const string UserCategoryRoot = "user";

    public ElementCollectionService(string? opcadPath = null, string? userPath = null)
    {
        _opcadPath = opcadPath ?? DefaultOpCadPath();
        _userPath = userPath ?? DefaultUserPath();
    }

    public string RootPath => _opcadPath;

    /// <summary>Pasta de elementos do usuário (criada sob demanda).</summary>
    public string UserPath => _userPath;

    /// <summary>Indica se a coleção .opcad foi encontrada no disco.</summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_opcadPath) && Directory.Exists(_opcadPath);

    /// <summary>
    /// Escaneia apenas os diretórios e retorna os caminhos dos arquivos, sem parsear.
    /// Usado para montar a árvore de categorias rapidamente antes do UI aparecer.
    /// </summary>
    public ScanResult ScanCategories()
    {
        var opcadPaths = new List<string>();
        var userPaths = new List<string>();

        if (IsAvailable)
            opcadPaths.AddRange(EnumerateElementFiles(_opcadPath));

        try
        {
            if (Directory.Exists(_userPath))
                userPaths.AddRange(EnumerateElementFiles(_userPath));
        }
        catch { }

        return new ScanResult(opcadPaths, userPaths);
    }

    /// <summary>Parseia um lote de arquivos e retorna definições (tolerante a erros).</summary>
    public IReadOnlyList<ElementDefinition> ParseBatch(IEnumerable<string> files, string rootPath)
    {
        var result = new List<ElementDefinition>();
        foreach (var file in files)
        {
            try
            {
                var dir = Path.GetDirectoryName(file);
                var category = dir is null ? string.Empty : Path.GetRelativePath(rootPath, dir);
                var def = ParseElementFile(file, category);
                if (def is not null)
                    result.Add(def);
            }
            catch { }
        }
        return result;
    }

    /// <summary>
    /// Carrega todos os elementos da coleção (tolerante a arquivos malformados).
    /// O resultado é cacheado por chave — a biblioteca não muda durante a sessão.
    /// </summary>
    public IReadOnlyList<ElementDefinition> LoadAll()
    {
        var key = _opcadPath + "|" + _userPath;
        return Cache.GetOrAdd(key, _ => LoadAllCore());
    }

    /// <summary>Invalida o cache da biblioteca (após o editor de elementos criar/editar arquivos).</summary>
    public static void InvalidateLibraryCache() => Cache.Clear();

    private IReadOnlyList<ElementDefinition> LoadAllCore()
    {
        var result = new List<ElementDefinition>();

        // Biblioteca .opcad / .json / .xml (elementos embutidos ao lado do exe)
        if (IsAvailable)
        {
            foreach (var file in EnumerateElementFiles(_opcadPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(file);
                    var category = dir is null ? string.Empty : Path.GetRelativePath(_opcadPath, dir);
                    var def = ParseElementFile(file, category);
                    if (def is not null)
                        result.Add(def);
                }
                catch
                {
                    // arquivo inválido/indisponível: ignora sem abortar a biblioteca
                }
            }
        }

        // Coleção do usuário (.opcad / .json / .xml)
        try
        {
            Directory.CreateDirectory(_userPath);
            foreach (var file in EnumerateElementFiles(_userPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(file);
                    var sub = dir is null ? string.Empty : Path.GetRelativePath(_userPath, dir);
                    if (sub == ".")
                        sub = string.Empty;
                    var category = string.IsNullOrEmpty(sub) ? UserCategoryRoot : UserCategoryRoot + "/" + sub;
                    var def = ParseElementFile(file, category);
                    if (def is not null)
                        result.Add(def);
                }
                catch
                {
                    // arquivo inválido/indisponível: ignora sem abortar a biblioteca
                }
            }
        }
        catch
        {
            // pasta do usuário indisponível: segue só com a coleção instalada
        }
        return result;
    }

    /// <summary>
    /// Parseia um arquivo de elemento JSON nativo do OperCAD.
    /// </summary>
    private static ElementDefinition? ParseElementFile(string filePath, string category)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".opcad" => OpcadParser.Parse(filePath, category),
            ".json" => OpcadParser.Parse(filePath, category),
            _ => null
        };
    }

    /// <summary>
    /// Enumera arquivos de elementos JSON (.opcad e .json) recursivamente.
    /// </summary>
    private static IEnumerable<string> EnumerateElementFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs;
            IEnumerable<string> files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".opcad" || ext == ".json";
                    });
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var d in subdirs)
                stack.Push(d);
            foreach (var f in files)
                yield return f;
        }
    }
}

/// <summary>Resultado do escaneamento rápido de diretórios (sem parsing).</summary>
public sealed class ScanResult
{
    public IReadOnlyList<string> OpCadPaths { get; }
    public IReadOnlyList<string> UserPaths { get; }

    public ScanResult(IReadOnlyList<string> opcad, IReadOnlyList<string> user)
    {
        OpCadPaths = opcad;
        UserPaths = user;
    }
}
