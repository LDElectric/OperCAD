using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace OperCAD.UI.ViewModels;

/// <summary>Nó da árvore de categorias da biblioteca de elementos.</summary>
public sealed class ElementCategoryNode
{
    public string Name { get; }

    /// <summary>Caminho relativo completo (ex.: "10_electric/10_allpole").</summary>
    public string FullPath { get; }

    public ObservableCollection<ElementCategoryNode> Children { get; } = new();
    public ObservableCollection<ElementItemViewModel> Elements { get; } = new();

    /// <summary>Nós-filhos combinados (subcategorias + elementos) para exibição em árvore.</summary>
    public IEnumerable<object> Nodes
    {
        get
        {
            foreach (var c in Children)
                yield return c;
            foreach (var e in Elements)
                yield return e;
        }
    }

    public ElementCategoryNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }
}

/// <summary>Nó unificado da árvore da paleta (categoria ou elemento, para template único).</summary>
public sealed class PaletteTreeNode
{
    public string Name { get; init; } = "";
    public string? IconKey { get; init; }
    public BitmapSource? Preview { get; init; }
    public ElementItemViewModel? Element { get; init; }
    public ObservableCollection<PaletteTreeNode> Children { get; } = new();

    /// <summary>True para nós de domínio (pastas principais da árvore).</summary>
    public bool IsRoot { get; init; }

    /// <summary>True quando o nó representa um elemento arrastável (folha).</summary>
    public bool IsElement => Element is not null;
}
