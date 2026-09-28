using System.Collections.ObjectModel;

namespace OperCAD.Core.TerminalStrips;

/// <summary>
/// Bornes (terminal strip) — port do TerminalStrip do padrão CAD.
/// Um borne reúne terminais "livres" de elementos (link_type=terminal) em níveis
/// numerados, com possibilidade de pontes entre níveis.
/// </summary>
public sealed class TerminalStrip
{
    public Guid Uuid { get; set; } = Guid.NewGuid();

    /// <summary>Nome do borne (ex.: "X1").</summary>
    public string Name { get; set; } = "X";

    /// <summary>Comentário livre.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Níveis (bornes físicos) — um nível pode reunir vários terminais reais.</summary>
    public ObservableCollection<PhysicalTerminal> PhysicalTerminals { get; } = new();

    /// <summary>Pontes entre terminais reais do borne.</summary>
    public ObservableCollection<TerminalStripBridge> Bridges { get; } = new();

    /// <summary>Ids dos elementos já usados neste borne (para cálculo de terminais livres).</summary>
    public IEnumerable<Guid> UsedElementIds => PhysicalTerminals.SelectMany(p => p.ElementUuids);

    /// <summary>Número exibido do nível (1-based, conforme a ordem atual).</summary>
    public int NumberOf(PhysicalTerminal pt)
    {
        var idx = PhysicalTerminals.IndexOf(pt);
        return idx < 0 ? 0 : idx + 1;
    }
}

/// <summary>Borne físico (um nível do terminal strip) — reúne 1+ terminais reais de elementos.</summary>
public sealed class PhysicalTerminal
{
    /// <summary>Ids dos elementos (terminais reais) agrupados neste nível.</summary>
    public ObservableCollection<Guid> ElementUuids { get; } = new();
}

/// <summary>Ponte entre dois ou mais terminais reais do borne (como o ponte do borne do padrão CAD).</summary>
public sealed class TerminalStripBridge
{
    public Guid Uuid { get; set; } = Guid.NewGuid();

    /// <summary>Cor da ponte (hex, ex.: "#e60000").</summary>
    public string Color { get; set; } = "#e60000";

    /// <summary>Ids dos elementos conectados pela ponte.</summary>
    public ObservableCollection<Guid> ElementUuids { get; } = new();
}
