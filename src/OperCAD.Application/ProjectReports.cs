using System.Text;
using OperCAD.Core.Documents;

namespace OperCAD.Application;

/// <summary>Tipos de partes para autonumeração com fórmulas (análogos aos do padrão CAD).</summary>
public enum LabelPartType
{
    /// <summary>Prefixo fixo (ex.: "K").</summary>
    Prefix,
    /// <summary>Contador sequencial (ex.: 1, 2, 3).</summary>
    Counter,
    /// <summary>Categoria do elemento.</summary>
    Category,
    /// <summary>Função do elemento.</summary>
    Function,
    /// <summary>Coluna da folha (A, B, C...).</summary>
    Columns,
    /// <summary>Linha da folha (1, 2, 3...).</summary>
    Rows,
    /// <summary>Ano (2 dígitos).</summary>
    Year,
    /// <summary>Mês (2 dígitos).</summary>
    Month,
    /// <summary>Dia (2 dígitos).</summary>
    Day,
    /// <summary>Número da folha.</summary>
    PageNumber,
    /// <summary>Total de folhas.</summary>
    TotalPages,
    /// <summary>Nome do projeto.</summary>
    ProjectName,
    /// <summary>Sequencial global (ignora prefixo,计数ador único para todo o projeto).</summary>
    SequentialGlobal,
    /// <summary>Texto customizado.</summary>
    Custom
}

/// <summary>Parte de uma fórmula de autonumeração.</summary>
public sealed class LabelFormulaPart
{
    /// <summary>Tipo desta parte da fórmula.</summary>
    public LabelPartType Type { get; set; }

    /// <summary>Texto fixo (usado quando Type é Prefix ou Custom).</summary>
    public string FixedText { get; set; } = string.Empty;

    /// <summary>Formato para números (ex.: "000" gera 001, 002...).</summary>
    public string Format { get; set; } = "000";
}

/// <summary>Fórmula de autonumeração de elementos.</summary>
public sealed class LabelFormula
{
    /// <summary>Partes da fórmula (concatenadas na ordem).</summary>
    public List<LabelFormulaPart> Parts { get; } = new();
}

/// <summary>
/// Relatórios do projeto: renumerar elementos e gerar lista de fiação /
/// nomenclatura (BOM) em CSV (PT-BR).
/// </summary>
public static class ProjectReports
{
    /// <summary>Renumera os elementos do projeto (K1, K2... por prefixo, em ordem).</summary>
    public static void Renumber(Project project)
    {
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in project.Pages)
        {
            foreach (var obj in page.Objects)
            {
                if (obj is PlacedElement el && el.Definition.HasLabelField && !el.IsLabelFrozen)
                {
                    var prefix = ElementLabels.Prefix(el.Definition);
                    counters.TryGetValue(prefix, out var n);
                    n++;
                    counters[prefix] = n;
                    el.Label = $"{prefix}{n}";
                }
            }
        }
    }

    /// <summary>
    /// Autonumeração de condutores (como o autonumeração de condutores do padrão CAD):
    /// numera sequencialmente todos os condutores do projeto (na ordem das folhas
    /// e de criação), aplicando prefixo/passo do projeto. As alterações são aplicadas
    /// direto (o chamador registra o undo com as snapshots). Retorna os condutores
    /// renumerados.
    /// </summary>
    public static IReadOnlyList<Conductor> RenumberConductors(Project project)
    {
        var changed = new List<Conductor>();
        var prefix = project.ConductorAutoNumPrefix ?? string.Empty;
        var step = Math.Max(1, project.ConductorAutoNumStep);
        var number = step;
        foreach (var page in project.Pages)
        {
            foreach (var cond in page.Objects.OfType<Conductor>())
            {
                cond.Text = prefix + number;
                cond.SequenceNumber = number;
                changed.Add(cond);
                number += step;
            }
        }
        return changed;
    }

    /// <summary>
    /// Renumera elementos usando fórmula personalizada.
    /// Cada parte da fórmula é avaliada para cada elemento e concatenada para gerar a etiqueta.
    /// </summary>
    /// <param name="project">Projeto a renumerar.</param>
    /// <param name="formula">Fórmula com as partes de numeração.</param>
    public static void RenumberWithFormula(Project project, LabelFormula formula)
    {
        if (formula.Parts.Count == 0)
            return;

        // Contadores por prefixo (para a parte Counter)
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Contador global (para SequentialGlobal)
        var globalCounter = 0;
        // Índice da folha (para PageNumber)
        var pageIndex = 0;

        foreach (var page in project.Pages)
        {
            pageIndex++;

            // Determina posição da folha (para Column/Row)
            foreach (var obj in page.Objects)
            {
                if (obj is not PlacedElement el || !el.Definition.HasLabelField)
                    continue;

                globalCounter++;
                var prefix = ElementLabels.Prefix(el.Definition);
                counters.TryGetValue(prefix, out var counter);
                counter++;
                counters[prefix] = counter;

                // Determina posição no quadro
                var worldPos = new Core.Geometry.Point2D(el.X, el.Y);
                var position = page.ConvertPosition(worldPos);
                var columnStr = new string(position.Where(char.IsLetter).ToArray());
                var rowStr = new string(position.Where(char.IsDigit).ToArray());
                int.TryParse(rowStr, out var rowNum);

                // Avalia cada parte da fórmula
                var sb = new StringBuilder();
                foreach (var part in formula.Parts)
                {
                    var value = part.Type switch
                    {
                        LabelPartType.Prefix => part.FixedText,
                        LabelPartType.Counter => counter.ToString(part.Format),
                        LabelPartType.Category => el.Definition.Category,
                        LabelPartType.Function => el.Function,
                        LabelPartType.Columns => columnStr,
                        LabelPartType.Rows => rowNum.ToString(part.Format),
                        LabelPartType.Year => DateTime.Now.ToString("yy"),
                        LabelPartType.Month => DateTime.Now.ToString("MM"),
                        LabelPartType.Day => DateTime.Now.ToString("dd"),
                        LabelPartType.PageNumber => pageIndex.ToString(part.Format),
                        LabelPartType.TotalPages => project.Pages.Count.ToString(part.Format),
                        LabelPartType.ProjectName => project.Name,
                        LabelPartType.SequentialGlobal => globalCounter.ToString(part.Format),
                        LabelPartType.Custom => part.FixedText,
                        _ => string.Empty
                    };
                    sb.Append(value);
                }

                el.Label = sb.ToString();
            }
        }
    }

    /// <summary>Lista de fiação (condutores) em CSV: Folha;De;De terminal;Para;Para terminal;Cor.</summary>
    public static string WiringListCsv(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Folha;De;De terminal;Para;Para terminal;Cor");
        foreach (var page in project.Pages)
        {
            foreach (var obj in page.Objects)
            {
                if (obj is not Conductor c)
                    continue;
                var fromEl = page.FindElement(c.From.ElementId);
                var toEl = page.FindElement(c.To.ElementId);
                var fromLabel = fromEl is null ? "?" : Pick(fromEl.Label, fromEl.Name);
                var toLabel = toEl is null ? "?" : Pick(toEl.Label, toEl.Name);
                var fromTerm = fromEl is null ? "" : fromEl.Definition.Terminals[c.From.TerminalIndex].Name;
                var toTerm = toEl is null ? "" : toEl.Definition.Terminals[c.To.TerminalIndex].Name;
                sb.AppendLine($"{Csv(page.Name)};{Csv(fromLabel)};{Csv(fromTerm)};{Csv(toLabel)};{Csv(toTerm)};{Csv(c.Color)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Nomenclatura (BOM) dos elementos em CSV: Folha;Etiqueta;Nome;Categoria;Fabricante;Designação;Descrição;Função.</summary>
    public static string BillOfMaterialsCsv(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Folha;Etiqueta;Nome;Categoria;Fabricante;Designação;Descrição;Função");
        foreach (var page in project.Pages)
        {
            foreach (var obj in page.Objects)
            {
                if (obj is not PlacedElement el)
                    continue;
                sb.AppendLine($"{Csv(page.Name)};{Csv(el.Label)};{Csv(el.Name)};{Csv(el.Definition.Category)};" +
                              $"{Csv(el.Manufacturer)};{Csv(el.Designation)};{Csv(el.Description)};{Csv(el.Function)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Exporta a numeração dos condutores para CSV: Folha;Número;Sequência;Cor;Espessura;Fórmula.</summary>
    public static string ConductorNumberingCsv(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Folha;Número;Sequência;Cor;Espessura;Fórmula");
        foreach (var page in project.Pages)
        {
            foreach (var obj in page.Objects)
            {
                if (obj is not Conductor c)
                    continue;
                sb.AppendLine($"{Csv(page.Name)};{Csv(c.Text)};{c.SequenceNumber};{Csv(c.Color)};{c.Width:0.0};{Csv(c.Formula)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Remove folios vazios (sem objetos) do projeto. Retorna a quantidade removida.</summary>
    public static int CleanProject(Project project)
    {
        var empty = project.Pages.Where(p => p.Objects.Count == 0).ToList();
        foreach (var page in empty)
            project.Pages.Remove(page);
        return empty.Count;
    }

    private static string Pick(string primary, string fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    private static string Csv(string? s)
    {
        s ??= string.Empty;
        if (s.Contains(';') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    /// <summary>Gera um resumo de todas as folhas do projeto (Sommaire/Nomenclatura).</summary>
    public static string ProjectSummaryCsv(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Folha;Nome;Elementos;Condutores;Largura;Altura");
        foreach (var page in project.Pages)
        {
            var elements = page.Objects.OfType<PlacedElement>().Count();
            var conductors = page.Objects.OfType<Conductor>().Count();
            sb.AppendLine($"{Csv(page.Name)};{Csv(page.Name)};{elements};{conductors};{page.Settings.Width};{page.Settings.Height}");
        }
        return sb.ToString();
    }
}
