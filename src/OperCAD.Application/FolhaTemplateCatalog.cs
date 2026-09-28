using OperCAD.Core.Documents;

namespace OperCAD.Application;

/// <summary>
/// Catálogo de molduras de folha dirigidas por dados (JSON). O OperCAD é autônomo:
/// procura os arquivos na pasta ao lado do executável (ex.: "template.json") e
/// oferece também uma moldura padrão embutida.
/// </summary>
public sealed class FolhaTemplateCatalog
{
    private static readonly Lazy<FolhaTemplateCatalog> DefaultInstance =
        new(() => new FolhaTemplateCatalog());

    /// <summary>Catálogo compartilhado da instalação padrão do OperCAD.</summary>
    public static FolhaTemplateCatalog Default => DefaultInstance.Value;

    private readonly string _rootPath;

    public FolhaTemplateCatalog(string? rootPath = null)
    {
        _rootPath = rootPath ?? DefaultPath();
    }

    /// <summary>Pasta padrão das molduras (ao lado do executável).</summary>
    public static string DefaultPath() => PortablePath.ExecutableDirectory;

    /// <summary>
    /// Moldura padrão do OperCAD — SEMPRE a folha ABNT (template.json / conteúdo embutido).
    /// Nunca retorna uma moldura genérica concorrente: se o arquivo não existir no disco,
    /// usa o JSON ABNT embutido (compilado), que é o mesmo conteúdo.
    /// </summary>
    public static FolhaTemplate Builtin()
    {
        var file = Path.Combine(DefaultPath(), "template.json");
        var fromDisk = FolhaTemplateLoader.ParseFile(file);
        if (fromDisk is not null)
            return fromDisk;
        // A folha embutida é garantida pelo compilador; nunca cai em moldura vazia.
        return FolhaTemplateLoader.Parse(DefaultJson, "template.json")!;
    }

    private const string DefaultJson =
        """
        {
          "version": 1,
          "name": "Padrão ABNT (OBRA/CAD)",
          "fileName": "template.json",
          "size": "A4",
          "frameInset": 5.0,
          "bindingLines": [0.0129, 0.0244, 0.0366],
          "columnsCount": 8,
          "columnStripHeight": 0.0176,
          "rowsCount": 6,
          "rowStripWidth": 0.0115,
          "showRightStrip": true,
          "rowsStripStart": 0.0176,
          "rowsStripEnd": 0.9167,
          "titleBlockTop": 0.9167,
          "titleBlockBottom": 1.0,
          "titleBlockLeft": 0.0366,
          "titleBlockCells": [
          { "x": 0.0366, "y": 0.9167, "w": 0.1564, "h": 0.0422 },
          { "x": 0.0366, "y": 0.9589, "w": 0.1564, "h": 0.0411 },
          { "x": 0.193, "y": 0.9167, "w": 0.0301, "h": 0.0246 },
          { "x": 0.2231, "y": 0.9167, "w": 0.0481, "h": 0.0246 },
          { "x": 0.2712, "y": 0.9167, "w": 0.0409, "h": 0.0246, "isNorth": true },
          { "x": 0.3121, "y": 0.9167, "w": 0.0431, "h": 0.0246, "label": "ESCALA", "field": "scale", "align": "center" },
          { "x": 0.193, "y": 0.9413, "w": 0.0301, "h": 0.02, "label": "AUTOR", "align": "center" },
          { "x": 0.2231, "y": 0.9413, "w": 0.1321, "h": 0.02, "field": "author", "align": "center" },
          { "x": 0.193, "y": 0.9613, "w": 0.0301, "h": 0.0187, "label": "REV. POR", "align": "center" },
          { "x": 0.2231, "y": 0.9613, "w": 0.1321, "h": 0.0187, "field": "revpor", "align": "center" },
          { "x": 0.193, "y": 0.98, "w": 0.0301, "h": 0.02, "label": "APROV. POR", "align": "center" },
          { "x": 0.2231, "y": 0.98, "w": 0.1321, "h": 0.02, "field": "aprovpor", "align": "center" },
          { "x": 0.3552, "y": 0.9167, "w": 0.2302, "h": 0.0833, "label": "EMPREENDIMENTO", "field": "company", "align": "center", "valign": "top" },
          { "x": 0.5854, "y": 0.9167, "w": 0.2324, "h": 0.0833, "label": "DESCRIÇÃO DO DESENHO", "field": "title", "align": "center", "valign": "top" },
          { "x": 0.8178, "y": 0.9167, "w": 0.1822, "h": 0.0211 },
          { "x": 0.8178, "y": 0.9378, "w": 0.0725, "h": 0.0211, "label": "OBRA/CAD", "field": "obra", "align": "center" },
          { "x": 0.8903, "y": 0.9378, "w": 0.0523, "h": 0.0211, "label": "PÁGINA", "field": "sheet", "align": "center" },
          { "x": 0.9426, "y": 0.9378, "w": 0.0574, "h": 0.0211, "label": "DE TOTAL DE PÁGINAS", "field": "totalsheets", "align": "center" },
          { "x": 0.8178, "y": 0.9589, "w": 0.1327, "h": 0.0411, "label": "REGISTRO - N° DO DESENHO", "field": "drawingnumber", "align": "center" },
          { "x": 0.9505, "y": 0.9589, "w": 0.0495, "h": 0.0411, "label": "REV", "field": "revision", "align": "center" },
        ]
        }
        """;

    /// <summary>Carrega as molduras disponíveis (arquivos .json na pasta, tolerante).</summary>
    public IReadOnlyList<FolhaTemplate> LoadAll()
    {
        var list = new List<FolhaTemplate>();
        if (Directory.Exists(_rootPath))
        {
            foreach (var file in Directory.EnumerateFiles(_rootPath, "*.json")
                         .Where(f => !f.EndsWith(".opcad", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(Path.GetFileName(file), "appsettings.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                var tpl = FolhaTemplateLoader.ParseFile(file);
                if (tpl is not null && tpl.TitleBlockCells.Count > 0)
                    list.Add(tpl);
            }
        }
        return list;
    }

    /// <summary>Busca uma moldura pelo nome do arquivo.</summary>
    public FolhaTemplate? Get(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        return LoadAll().FirstOrDefault(t =>
            string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }
}
