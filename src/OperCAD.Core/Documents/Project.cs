using System.Collections.ObjectModel;
using OperCAD.Core.TerminalStrips;
using OperCAD.Core.Undo;

namespace OperCAD.Core.Documents;

/// <summary>Projeto (documento) do OperCAD: reúne as folhas e as configurações gerais.</summary>
public sealed class Project
{
    public string Name { get; set; } = "Novo projeto";

    /// <summary>Título do projeto (como o "project_title" do padrão CAD).</summary>
    public string? Title { get; set; }

    /// <summary>Variáveis do projeto (key-value, como o "DiagramContext" do padrão CAD).</summary>
    public Dictionary<string, string> Variables { get; } = new();

    public ObservableCollection<Page> Pages { get; } = new();
    public Page? ActivePage { get; set; }

    /// <summary>Bornes (terminal strips) do projeto — como o painel "Bornes" do padrão CAD.</summary>
    public ObservableCollection<TerminalStrip> TerminalStrips { get; } = new();

    /// <summary>Autonumeração de condutores: prefixo do número (ex.: "W").</summary>
    public string ConductorAutoNumPrefix { get; set; } = string.Empty;

    /// <summary>Autonumeração de condutores: ativa para novos condutores?</summary>
    public bool ConductorAutoNumEnabled { get; set; } = true;

    /// <summary>Autonumeração de condutores: passo entre números consecutivos.</summary>
    public int ConductorAutoNumStep { get; set; } = 1;

    /// <summary>Autonumeração de elementos: ativa para novos elementos?</summary>
    public bool ElementAutoNumEnabled { get; set; } = true;

    /// <summary>
    /// Fórmula da autonumeração de elementos (como o element_autonums do padrão CAD):
    /// %prefix% = prefixo do tipo (K, Q...), %autonum% = número sequencial.
    /// </summary>
    public string ElementAutoNumFormula { get; set; } = "%prefix%%autonum%";

    /// <summary>Fórmula da autonumeração de folhas (como a folha_autonums do padrão CAD).</summary>
    public string FolhaAutoNumFormula { get; set; } = "%id/%total";

    /// <summary>Pilha de desfazer/refazer do projeto (como o undo group do padrão CAD).</summary>
    public UndoStack UndoStack { get; } = new();

    /// <summary>Próximo número de condutor livre (máximo atual + passo), considerando todo o projeto.</summary>
    public int NextConductorNumber()
    {
        var max = 0;
        foreach (var page in Pages)
        {
            foreach (var cond in page.Objects.OfType<Conductor>())
                max = Math.Max(max, cond.SequenceNumber);
        }
        return max + Math.Max(1, ConductorAutoNumStep);
    }

    /// <summary>Formata o próximo número de condutor conforme prefixo/sequência configurados.</summary>
    public string NextConductorText() => ConductorAutoNumPrefix + NextConductorNumber();

    public Page AddPage(string? name = null)
    {
        var page = new Page { Name = name ?? $"Folha {Pages.Count + 1:00}" };
        Pages.Add(page);
        return page;
    }}
