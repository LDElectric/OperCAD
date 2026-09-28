namespace OperCAD.Core.Undo;

/// <summary>
/// Pilha de comandos desfazíveis/refazíveis (análoga ao pilha de desfazer do padrão CAD).
/// Mantém a lista de descrições para exibir no painel "Desfazer" (como o QUndoView).
/// </summary>
public sealed class UndoStack
{
    private readonly List<IUndoableCommand> _commands = new();
    private int _index; // nº de comandos aplicados (próximo desfazer)

    /// <summary>Disparado quando o estado da pilha muda (push/undo/redo/clear).</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index < _commands.Count;

    /// <summary>Descrição do próximo comando a desfazer (para o menu Desfazer).</summary>
    public string UndoText => CanUndo ? _commands[_index - 1].Description : string.Empty;

    /// <summary>Descrição do próximo comando a refazer (para o menu Refazer).</summary>
    public string RedoText => CanRedo ? _commands[_index].Description : string.Empty;

    /// <summary>Descrições dos comandos aplicados, do mais antigo ao mais recente.</summary>
    public IReadOnlyList<string> Descriptions
        => _commands.Take(_index).Select(c => c.Description).ToList();

    /// <summary>Número de comandos aplicados (índice do próximo desfazer).</summary>
    public int NextUndoIndex => _index;

    /// <summary>Aplica um novo comando (limpa a pilha de refazer).</summary>
    public void Push(IUndoableCommand command)
    {
        if (command is null)
            return;
        if (_index < _commands.Count)
            _commands.RemoveRange(_index, _commands.Count - _index);
        _commands.Add(command);
        _index = _commands.Count;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Desfaz o último comando aplicado.</summary>
    public void Undo()
    {
        if (!CanUndo)
            return;
        _index--;
        _commands[_index].Undo();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refaz o próximo comando desfeito.</summary>
    public void Redo()
    {
        if (!CanRedo)
            return;
        _commands[_index].Redo();
        _index++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Desfaz até que apenas <paramref name="count"/> comandos permaneçam aplicados
    /// (clique no painel Desfazer, como o painel de desfazer do padrão CAD).
    /// </summary>
    public void UndoTo(int count)
    {
        if (count < 0 || count > _index)
            return;
        while (_index > count)
            Undo();
    }

    /// <summary>Limpa a pilha (ex.: novo projeto).</summary>
    public void Clear()
    {
        _commands.Clear();
        _index = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
