namespace OperCAD.Core.Documents;

/// <summary>Referência a um terminal específico de um elemento posicionado.</summary>
public sealed class ElementTerminalRef
{
    /// <summary>Id do <see cref="PlacedElement"/> na folha.</summary>
    public Guid ElementId { get; }

    /// <summary>Índice do terminal na lista <c>Definition.Terminals</c>.</summary>
    public int TerminalIndex { get; }

    public ElementTerminalRef(Guid elementId, int terminalIndex)
    {
        ElementId = elementId;
        TerminalIndex = terminalIndex;
    }

    /// <summary>Referência "vazia" usada quando o condutor tem uma ponta livre (sem terminal).</summary>
    public static ElementTerminalRef Empty { get; } = new(Guid.Empty, -1);

    /// <summary>True quando esta referência aponta para um terminal real de um elemento.</summary>
    public bool IsConnected => ElementId != Guid.Empty && TerminalIndex >= 0;
}
