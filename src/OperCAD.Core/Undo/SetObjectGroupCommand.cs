using OperCAD.Core.Documents;

namespace OperCAD.Core.Undo;

/// <summary>
/// Agrupa/desagrupa objetos da folha. Guarda o grupo ANTERIOR de cada objeto para que o
/// desfazer restaure exatamente o estado original (inclusive voltando a "sem grupo").
/// </summary>
public sealed class SetObjectGroupCommand : IUndoableCommand
{
    private readonly List<(DocumentObject Object, Guid? Before, Guid? After)> _entries;

    public string Description { get; }

    /// <param name="objects">Objetos a agrupar/desagrupar.</param>
    /// <param name="groupId">Grupo de destino; null desagrupa.</param>
    public SetObjectGroupCommand(IEnumerable<DocumentObject> objects, Guid? groupId, string description)
    {
        _entries = objects
            .Where(ObjectGroups.IsGroupable)
            .Select(obj => (obj, ObjectGroups.GroupIdOf(obj), groupId))
            .ToList();
        Description = description;
    }

    public void Undo()
    {
        foreach (var (obj, before, _) in _entries)
            ObjectGroups.SetGroupId(obj, before);
    }

    public void Redo()
    {
        foreach (var (obj, _, after) in _entries)
            ObjectGroups.SetGroupId(obj, after);
    }
}
