using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OperCAD.Application;
using OperCAD.Core.Elements;

namespace OperCAD.UI.ViewModels;

/// <summary>
/// Paleta de elementos: carrega a coleção .opcad e .xml, organiza em árvore
/// hierárquica (domínio → classe → elemento) e dispara o pedido de inserção
/// ao selecionar ou arrastar um elemento.
/// </summary>
public sealed partial class ElementPaletteViewModel : ObservableObject
{
    private readonly ElementCollectionService _service;

    // ── Coleções ──────────────────────────────────────────────────────────

    /// <summary>Categorias da aba de domínio selecionada (para busca).</summary>
    public ObservableCollection<ElementCategoryNode> Categories { get; } = new();

    /// <summary>Árvore hierárquica: domínio → classe → elemento.</summary>
    public ObservableCollection<PaletteTreeNode> PaletteTree { get; } = new();

    /// <summary>Elementos visíveis na lista (filtrados pela busca).</summary>
    public ObservableCollection<ElementItemViewModel> VisibleElements { get; } = new();

    // ── Propriedades observáveis ──────────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadMessage = "Carregando biblioteca…";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ElementCategoryNode? _selectedCategory;

    [ObservableProperty]
    private ElementItemViewModel? _selectedElement;

    // ── Todos os elementos carregados (para busca global e toolbar) ──────

    private List<ElementDefinition> _allElements = new();

    /// <summary>Todos os elementos carregados (paleta + .opcad).</summary>
    public IReadOnlyList<ElementDefinition> AllElements => _allElements;

    // ── Eventos ───────────────────────────────────────────────────────────

    /// <summary>Disparado quando o usuário clica/arrasta um elemento → pedido de inserção.</summary>
    public event Action<ElementDefinition>? InsertRequested;

    // ── Construtores ──────────────────────────────────────────────────────

    public ElementPaletteViewModel()
        : this(new ElementCollectionService())
    {
    }

    public ElementPaletteViewModel(ElementCollectionService service)
    {
        _service = service;
    }

    // ── Carregamento ──────────────────────────────────────────────────────

    /// <summary>
    /// Carrega a paleta em três fases otimizadas para UX:
    /// 1. Escaneia diretórios → nós de domínio vazios visíveis imediatamente
    /// 2. Parseia o domínio elétrico → elementos visíveis em ~2s
    /// 3. Parseia demais domínios em background (não bloqueia)
    /// </summary>
    public async Task LoadAsync()
    {
        if (IsLoading)
            return;
        IsLoading = true;
        LoadMessage = "Escaneando biblioteca de elementos…";

        try
        {
            // Fase 1: escaneia diretórios (rápido — sem parsing)
            var scan = await Task.Run(() => _service.ScanCategories());
            var opcadRoot = ElementCollectionService.DefaultOpCadPath();
            var byDomain = GroupByDomain(scan.OpCadPaths, opcadRoot);
            var domainOrder = new[] { "Eletrico", "Logica", "Pneumatico", "Hidraulico" };

            // Cria nós de domínio vazios na árvore (visível imediatamente)
            PaletteTree.Clear();
            var domainNodes = new Dictionary<string, PaletteTreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in domainOrder)
            {
                if (!byDomain.ContainsKey(d)) continue;
                var domainNode = new PaletteTreeNode
                {
                    Name = CategoryMapper.GetDomainName(d),
                    IconKey = GetDomainIconKey(d),
                    IsRoot = true
                };
                PaletteTree.Add(domainNode);
                domainNodes[d] = domainNode;
            }

            var total = scan.OpCadPaths.Count + scan.UserPaths.Count;
            LoadMessage = $"{total} categorias prontas. Carregando…";

            // Fase 2: parseia o primeiro domínio (elétrico) e mostra imediatamente
            var firstDomain = true;
            foreach (var d in domainOrder)
            {
                if (!byDomain.TryGetValue(d, out var files)) continue;
                if (!domainNodes.TryGetValue(d, out var domainNode)) continue;

                var defs = await Task.Run(() => _service.ParseBatch(files, opcadRoot));
                _allElements.AddRange(defs);

                // Popula as categorias para busca e monta os nós filhos
                var categories = BuildCategoryTreeFromPaths(files, opcadRoot, d);
                PopulateCategoryTree(categories, defs, d);
                foreach (var cat in categories)
                    Categories.Add(cat);

                // Adiciona classes e elementos como filhos do nó de domínio
                BuildDomainChildren(domainNode, categories, d);

                if (firstDomain)
                {
                    firstDomain = false;
                    LoadMessage = $"{_allElements.Count} elementos carregados. Carregando outros domínios…";
                }
                break;
            }

            // Fase 3: parseia os demais domínios em background (não bloqueia a UI)
            var syncCtx = SynchronizationContext.Current;
            _ = Task.Run(async () =>
            {
                var first = true;
                foreach (var d in domainOrder)
                {
                    if (first) { first = false; continue; }
                    if (!byDomain.TryGetValue(d, out var files)) continue;
                    if (!domainNodes.TryGetValue(d, out var domainNode)) continue;

                    var defs = await Task.Run(() => _service.ParseBatch(files, opcadRoot));
                    syncCtx?.Post(_ =>
                    {
                        _allElements.AddRange(defs);

                        var categories = BuildCategoryTreeFromPaths(files, opcadRoot, d);
                        PopulateCategoryTree(categories, defs, d);
                        foreach (var cat in categories)
                            Categories.Add(cat);

                        BuildDomainChildren(domainNode, categories, d);
                    }, null);
                }

                syncCtx?.Post(_ =>
                    LoadMessage = $"{_allElements.Count} elementos carregados.", null);
            });
        }
        catch (Exception ex)
        {
            LoadMessage = "Erro ao carregar a biblioteca: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static Dictionary<string, List<string>> GroupByDomain(
        IReadOnlyList<string> opcadPaths, string opcadRoot)
    {
        var byDomain = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in opcadPaths)
        {
            var relPath = Path.GetRelativePath(opcadRoot, file);
            var domain = Path.GetDirectoryName(relPath)
                ?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(domain)) domain = "outros";
            if (!byDomain.ContainsKey(domain)) byDomain[domain] = new List<string>();
            byDomain[domain].Add(file);
        }
        return byDomain;
    }

    /// <summary>Popula as categorias já existentes com definições parseadas.</summary>
    private static void PopulateCategoryTree(
        ObservableCollection<ElementCategoryNode> categories,
        IReadOnlyList<ElementDefinition> elements, string domain)
    {
        foreach (var element in elements)
        {
            var relPath = element.Category.Replace('\\', '/');
            var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var subcatFolder = parts[1];

            var node = categories.FirstOrDefault(c =>
                string.Equals(c.FullPath, subcatFolder, StringComparison.OrdinalIgnoreCase));
            if (node is not null)
                node.Elements.Add(new ElementItemViewModel(element));
        }

        foreach (var node in categories)
        {
            var sorted = node.Elements.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            node.Elements.Clear();
            foreach (var e in sorted)
                node.Elements.Add(e);
        }
    }

    /// <summary>Monta a estrutura de categorias a partir dos caminhos (sem parsear .opcad).</summary>
    private static ObservableCollection<ElementCategoryNode> BuildCategoryTreeFromPaths(
        IReadOnlyList<string> files, string opcadRoot, string domain)
    {
        var categories = new ObservableCollection<ElementCategoryNode>();
        var catByName = new Dictionary<string, ElementCategoryNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relPath = Path.GetRelativePath(opcadRoot, file).Replace('\\', '/');
            var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var subcatFolder = parts[1];
            if (!catByName.TryGetValue(subcatFolder, out var node))
            {
                var catName = CategoryMapper.GetCategoryName(domain, subcatFolder);
                node = new ElementCategoryNode(catName, subcatFolder);
                categories.Add(node);
                catByName[subcatFolder] = node;
            }
        }

        var sorted = categories.OrderBy(c => c.Name).ToList();
        categories.Clear();
        foreach (var c in sorted)
            categories.Add(c);

        return categories;
    }

    /// <summary>Monta os nós-filho (classes + elementos) de um domínio na árvore.</summary>
    private static void BuildDomainChildren(
        PaletteTreeNode domainNode,
        IEnumerable<ElementCategoryNode> categories,
        string domain)
    {
        domainNode.Children.Clear();
        foreach (var cat in categories)
        {
            var iconKey = GetClassIconKey(cat.FullPath, domain);
            var catNode = new PaletteTreeNode { Name = cat.Name, IconKey = iconKey };
            foreach (var el in cat.Elements)
                catNode.Children.Add(new PaletteTreeNode { Name = el.Name, Preview = el.Preview, Element = el });
            domainNode.Children.Add(catNode);
        }
    }

    /// <summary>Ícone do domínio (para o nó-pai na árvore).</summary>
    private static string? GetDomainIconKey(string domain) => domain.ToLowerInvariant() switch
    {
        "eletrico" => "Icon.Class.PowerFeedings",
        "pneumatico" => "Icon.Class.PneumaticValves",
        "logica" => "Icon.Class.LogicGates",
        "hidraulico" => "Icon.Class.HydraulicValves",
        _ => null
    };

    /// <summary>Mapeia o nome da subpasta (PT-BR) para a chave de ícone da classe correspondente.</summary>
    private static string? GetClassIconKey(string subcategoryFolder, string domain) => subcategoryFolder.ToLowerInvariant() switch
    {
        "alimentadores e fontes" => "Icon.Class.PowerFeedings",
        "fusiveis e disjuntores" => "Icon.Class.FusesDisconnects",
        "contatores, contatos e bobinas" => "Icon.Class.ContactorsSwitches",
        "chaves e botoeiras" => "Icon.Class.Controls",
        "motores" => "Icon.Class.Motors",
        "sensores" => domain.Equals("eletrico", StringComparison.OrdinalIgnoreCase) ? "Icon.Class.Detectors" : "Icon.Class.PneumaticSensors",
        "electronica" => "Icon.Class.CoilsSignaling",
        "conectores" => "Icon.Class.Connectors",
        "transformadores" => "Icon.Class.Transformers",
        "utilidades" => "Icon.Class.Power",
        "portas logicas" => "Icon.Class.LogicGates",
        "fluxograma" => "Icon.Class.LogicFlowchart",
        "valvulas" => domain.Equals("pneumatico", StringComparison.OrdinalIgnoreCase) ? "Icon.Class.PneumaticValves" : "Icon.Class.HydraulicValves",
        "atuadores" => domain.Equals("pneumatico", StringComparison.OrdinalIgnoreCase) ? "Icon.Class.PneumaticActuators" : "Icon.Class.HydraulicActuators",
        "bombas" => "Icon.Class.HydraulicPumps",
        "utilizaveis" => "Icon.Class.PneumaticOther",
        _ => null
    };

    // ── Busca e filtro ────────────────────────────────────────────────────

    partial void OnSelectedCategoryChanged(ElementCategoryNode? value)
        => RefreshVisibleElements();

    /// <summary>Verdadeiro quando não há texto de busca (exibe a árvore).</summary>
    public bool IsBrowsing => string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Verdadeiro quando há texto de busca (exibe a lista de resultados).</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsBrowsing));
        OnPropertyChanged(nameof(IsSearching));
        RefreshVisibleElements();
    }

    /// <summary>Localiza um elemento na paleta a partir do nome/etiqueta selecionado.</summary>
    public void LocateInPalette(string elementName)
    {
        SearchText = elementName;
        RefreshVisibleElements();
    }

    private void RefreshVisibleElements()
    {
        VisibleElements.Clear();
        var filter = SearchText?.Trim();

        if (!string.IsNullOrEmpty(filter))
        {
            foreach (var cat in Categories)
                AddMatching(cat, filter);
            return;
        }

        if (SelectedCategory is null)
            return;
        foreach (var e in SelectedCategory.Elements)
            VisibleElements.Add(e);
    }

    private void AddMatching(ElementCategoryNode node, string filter)
    {
        foreach (var e in node.Elements)
        {
            if (e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Category.Contains(filter, StringComparison.OrdinalIgnoreCase))
                VisibleElements.Add(e);
        }
        foreach (var c in node.Children)
            AddMatching(c, filter);
    }

    // ── Seleção de elemento ───────────────────────────────────────────────

    partial void OnSelectedElementChanged(ElementItemViewModel? value)
    {
        foreach (var cat in Categories)
            ClearSelection(cat);
        if (value is not null)
        {
            value.IsSelected = true;
            InsertRequested?.Invoke(value.Definition);
        }
    }

    private static void ClearSelection(ElementCategoryNode node)
    {
        foreach (var e in node.Elements)
            e.IsSelected = false;
        foreach (var c in node.Children)
            ClearSelection(c);
    }

    // ── Reload ────────────────────────────────────────────────────────────

    public void ReloadElement(string path)
    {
        var item = FindByPath(path);
        if (item is null)
            return;
        try
        {
            ElementDefinition? def = OpcadParser.Parse(path, item.Definition.Category);
            if (def is not null)
                item.Reload(def);
        }
        catch
        {
            // mantém o item atual se o arquivo editado ficou inválido
        }
    }

    private ElementItemViewModel? FindByPath(string path)
    {
        foreach (var cat in Categories)
        {
            var found = FindByPath(cat, path);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static ElementItemViewModel? FindByPath(ElementCategoryNode node, string path)
    {
        foreach (var e in node.Elements)
        {
            if (string.Equals(e.Definition.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        foreach (var c in node.Children)
        {
            var found = FindByPath(c, path);
            if (found is not null)
                return found;
        }
        return null;
    }
}
