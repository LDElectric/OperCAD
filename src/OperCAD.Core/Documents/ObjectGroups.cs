namespace OperCAD.Core.Documents;

/// <summary>
/// Acesso unificado à identidade de grupo de um objeto da folha. Um grupo representa uma
/// unidade editável (ex.: um símbolo importado de PDF como o "K7.1" = 5 contatos). Centraliza
/// o switch que antes estava duplicado no SheetViewport.
/// </summary>
public static class ObjectGroups
{
    /// <summary>Id do grupo do objeto, ou null se não pertence a nenhum.</summary>
    public static Guid? GroupIdOf(DocumentObject? obj) => obj switch
    {
        ShapeItem shape => shape.ImportGroupId,
        TextItem text => text.ImportGroupId,
        _ => null
    };

    /// <summary>Define (ou limpa) o grupo do objeto, quando o tipo suporta agrupamento.</summary>
    public static void SetGroupId(DocumentObject obj, Guid? groupId)
    {
        switch (obj)
        {
            case ShapeItem shape:
                shape.ImportGroupId = groupId;
                break;
            case TextItem text:
                text.ImportGroupId = groupId;
                break;
        }
    }

    /// <summary>Indica se o tipo do objeto pode participar de um grupo (formas e textos).</summary>
    public static bool IsGroupable(DocumentObject obj) => obj is ShapeItem or TextItem;

    public static bool IsLockedOf(DocumentObject obj) => obj switch
    {
        ShapeItem shape => shape.IsLocked,
        TextItem text => text.IsLocked,
        _ => false
    };

    public static void SetLockedOf(DocumentObject obj, bool value)
    {
        switch (obj)
        {
            case ShapeItem shape:
                shape.IsLocked = value;
                break;
            case TextItem text:
                text.IsLocked = value;
                break;
        }
    }

    /// <summary>Todos os objetos da folha que pertencem ao grupo informado.</summary>
    public static List<DocumentObject> Members(Page page, Guid groupId)
        => page.Objects.Where(obj => GroupIdOf(obj) == groupId).ToList();
}
