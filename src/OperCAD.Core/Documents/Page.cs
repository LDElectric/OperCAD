using System.Collections.ObjectModel;
using OperCAD.Core.Conductors;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>Folha de desenho de um projeto. Cada folha tem um quadro/moldura.</summary>
public sealed class Page
{
    public string Name { get; set; } = "Folha 01";

    /// <summary>Nome exibido (evita mostrar o nome do tipo em abas/árvores).</summary>
    public override string ToString() => Name;
    public ObservableCollection<DocumentObject> Objects { get; } = new();
    public PageSettings Settings { get; } = new();
    public TitleBlock TitleBlock { get; } = new();

    /// <summary>
    /// Camadas da folha. Cada camada agrupa objetos para controle de visibilidade e bloqueio.
    /// Camadas são ordenadas por ZOrder (menor = mais atrás).
    /// </summary>
    public ObservableCollection<Layer> Layers { get; } = new();

    public void Add(DocumentObject obj) => Objects.Add(obj);
    public bool Remove(DocumentObject obj) => Objects.Remove(obj);

    private readonly Dictionary<string, int> _labelCounters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gera a próxima etiqueta para o prefixo (ex.: "K" → "K1", "K2"...).</summary>
    public string NextLabel(string prefix)
    {
        _labelCounters.TryGetValue(prefix, out var n);
        n++;
        _labelCounters[prefix] = n;
        return $"{prefix}{n}";
    }

    /// <summary>Posição de um ponto do mundo no quadro (ex.: "B3") — referência cruzada.</summary>
    public string ConvertPosition(OperCAD.Core.Geometry.Point2D world)
        => ElementLabels.Position(Settings, world);

    /// <summary>Adiciona um elemento posicionado à folha.</summary>
    public void AddElement(PlacedElement element) => Objects.Add(element);

    /// <summary>Remove todos os objetos (elementos, condutores...) da folha.</summary>
    public void Clear() => Objects.Clear();

    /// <summary>Busca um elemento posicionado pelo Id.</summary>
    public PlacedElement? FindElement(Guid id)
        => Objects.OfType<PlacedElement>().FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Remove um objeto da folha; se for um elemento, remove também os condutores
    /// que o referenciam (comportamento do padrão CAD).
    /// </summary>
    public void RemoveObject(DocumentObject obj)
    {
        if (obj is PlacedElement)
        {
            foreach (var conductor in Objects.OfType<Conductor>().ToList())
            {
                if (conductor.From.ElementId == obj.Id || conductor.To.ElementId == obj.Id)
                    Objects.Remove(conductor);
            }
        }
        Objects.Remove(obj);
    }

    /// <summary>Recalcula o caminho de todos os condutores (após mover/rotacionar).</summary>
    public void RecomputeConductors()
    {
        foreach (var conductor in Objects.OfType<Conductor>())
        {
            // Condutores desenhados à mão preservam a rota definida pelo usuário.
            if (conductor.PreservePath)
                continue;
            var fromEl = FindElement(conductor.From.ElementId);
            var toEl = FindElement(conductor.To.ElementId);
            if (fromEl is null || toEl is null)
                continue;
            conductor.Path = ConductorRouter.Generate(
                TerminalGeometry.WorldPosition(fromEl, fromEl.Definition.Terminals[conductor.From.TerminalIndex]),
                TerminalGeometry.WorldOrientation(fromEl, fromEl.Definition.Terminals[conductor.From.TerminalIndex]),
                TerminalGeometry.WorldPosition(toEl, toEl.Definition.Terminals[conductor.To.TerminalIndex]),
                TerminalGeometry.WorldOrientation(toEl, toEl.Definition.Terminals[conductor.To.TerminalIndex]));
        }
    }
}

/// <summary>Configuração da folha: tamanho de papel e margens da moldura.</summary>
public sealed class PageSettings
{
    public PaperSize Size { get; set; } = PaperSize.A4;

    /// <summary>Dimensões personalizadas (usadas quando Size == Custom), ex.: folio importado do padrão CAD.</summary>
    public double CustomWidth { get; set; } = 297;
    public double CustomHeight { get; set; } = 210;

    // Número do folio
    public string FolioNumber { get; set; } = string.Empty;

    /// <summary>Incrementa automaticamente o número do folio ao criar novas folhas.</summary>
    public bool AutoIncrementFolio { get; set; }

    // Metadados do folio
    public string FolioTitle { get; set; } = string.Empty;
    public string FolioSubtitle { get; set; } = string.Empty;
    public string FolioPlant { get; set; } = string.Empty;
    public string FolioSection { get; set; } = string.Empty;
    public string FolioLocation { get; set; } = string.Empty;

    public bool ShowFrame { get; set; } = true;
    public bool ShowGrid { get; set; } = true;

    // Moldura: margens
    public double MarginLeft { get; set; } = 20;
    public double MarginRight { get; set; } = 10;
    public double MarginTop { get; set; } = 10;
    public double MarginBottom { get; set; } = 10;

    // Cabeçalhos de colunas/linhas do quadro (estilo padrão CAD BorderProperties)
    public bool ShowColumns { get; set; } = true;
    public bool ShowRows { get; set; } = true;
    public int ColumnsCount { get; set; } = 17;
    public double ColumnsWidth { get; set; } = 32;
    public double ColumnsHeaderHeight { get; set; } = 8;
    public int RowsCount { get; set; } = 8;
    public double RowsHeight { get; set; } = 32;
    public double RowsHeaderWidth { get; set; } = 8;

    // Condutor padrão (aplicado a novos condutores)
    public string ConductorColor { get; set; } = "black";
    public double ConductorWidth { get; set; } = 1.0;

    /// <summary>
    /// Escala global dos elementos desta folha (configuração de projeto). Redimensiona os
    /// símbolos E o grid na mesma proporção, mantendo os terminais alinhados à malha de snap.
    /// Padrão 1.0 (folhas novas iniciam em 1.0; grid efetivo = 2,5 × 1,0 = 2,5 mm — elementos
    /// e grid sempre começam na escala 1).
    /// Valores típicos: 0.8, 1.0, 1.2.
    /// </summary>
    public double ElementScale { get; set; } = 1.0;

    // Cor de fundo da folha (hex, ex.: "#FFFFFF" para branco)
    public string BackgroundColor { get; set; } = "#FFFFFF";

    // Legenda (legenda) aplicado à folha: nome do arquivo .titleblock e posição
    public string? TitleBlockFile { get; set; }
    public TitleBlockPosition TitleBlockPosition { get; set; } = TitleBlockPosition.Bottom;

    /// <summary>Template de legenda RESOLVIDO (transitório; atribuído pelo catálogo ao abrir/editar).</summary>
    public TitleBlockTemplate? TitleBlock { get; set; }

    /// <summary>Template de MOLDURA da folha (nome do arquivo JSON, ex.: "template.json").</summary>
    public string? FolhaTemplateFile { get; set; }

    /// <summary>Template de MOLDURA RESOLVIDO (transitório; atribuído pelo loader ao abrir/editar).</summary>
    public FolhaTemplate? FolhaTemplate { get; set; }

    /// <summary>Recuo da moldura externa em relação à borda da folha (mm).</summary>
    public double FolhaFrameInset { get; set; } = 5;

    public double Width => Size == PaperSize.Custom ? CustomWidth : Size.Mm().Width;
    public double Height => Size == PaperSize.Custom ? CustomHeight : Size.Mm().Height;

    public Rect2D FullBounds => new(0, 0, Width, Height);
    public Rect2D DrawingBounds => new(MarginLeft, MarginBottom, Width - MarginLeft - MarginRight, Height - MarginBottom - MarginTop);
}

public enum PaperSize { Custom, A4, A3, A2, A1, A0 }

public static class PaperSizes
{
    public static (double Width, double Height) Mm(this PaperSize size) => size switch
    {
        PaperSize.A4 => (297, 210),
        PaperSize.A3 => (420, 297),
        PaperSize.A2 => (594, 420),
        PaperSize.A1 => (841, 594),
        PaperSize.A0 => (1189, 841),
        _ => (297, 210)
    };
}
