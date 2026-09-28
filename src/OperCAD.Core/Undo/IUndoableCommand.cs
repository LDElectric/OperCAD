namespace OperCAD.Core.Undo;

/// <summary>Comando desfazível/refazível de documento (análogo ao comando de desfazer do padrão CAD).</summary>
public interface IUndoableCommand
{
    /// <summary>Descrição exibida no painel "Desfazer" (ex.: "Adicionar elemento").</summary>
    string Description { get; }

    void Undo();
    void Redo();
}
