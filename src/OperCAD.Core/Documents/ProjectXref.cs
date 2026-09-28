using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Referência cruzada entre elementos (mestre↔escravo) em todo o projeto, estilo padrão CAD.
/// Um mestre (ex.: bobina K1) é vinculado aos seus escravos (contatos K2, K3...),
/// possivelmente em folhas diferentes. Cada elemento guarda os Ids dos parceiros
/// (<see cref="PlacedElement.LinkedUuids"/>) e este serviço resolve essas ligações.
/// </summary>
public static class ProjectXref
{
    /// <summary>Retorna todos os elementos vinculados a um elemento, em qualquer folha do projeto.</summary>
    public static List<PlacedElement> Partners(IReadOnlyList<Page> pages, PlacedElement el)
    {
        var result = new List<PlacedElement>();
        if (pages is null)
            return result;
        foreach (var page in pages)
        {
            foreach (var obj in page.Objects)
            {
                if (obj is PlacedElement other && other != el && el.LinkedUuids.Contains(other.Id))
                    result.Add(other);
            }
        }
        return result;
    }

    /// <summary>Número da folha (1-based) onde o elemento está, ou 0 se não encontrado.</summary>
    public static int FolhaOf(IReadOnlyList<Page> pages, PlacedElement el)
    {
        if (pages is null)
            return 0;
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].Objects.Contains(el))
                return i + 1;
        }
        return 0;
    }

    /// <summary>
    /// Posição no quadro do elemento usando as configurações da própria folha onde
    /// ele está (ex.: "B4"). Vazio se não encontrado.
    /// </summary>
    public static string PositionOf(IReadOnlyList<Page> pages, PlacedElement el)
    {
        if (pages is null)
            return string.Empty;
        foreach (var page in pages)
        {
            if (page.Objects.Contains(el))
                return page.ConvertPosition(new Point2D(el.X, el.Y));
        }
        return string.Empty;
    }

    /// <summary>
    /// Texto de referência cruzada exibido sob um elemento:
    /// — escravo: mostra o mestre ("K1 2/B4");
    /// — mestre: mostra cada escravo vinculado, um por linha ("K2 1/C3").
    /// Se o parceiro está na mesma folha, mostra só a posição ("B4"); em folha
    /// diferente, mostra "folha/posição" ("2/B4").
    /// </summary>
    public static string ReferenceText(IReadOnlyList<Page> pages, PlacedElement el)
    {
        if (pages is null)
            return string.Empty;
        var currentFolio = FolhaOf(pages, el);
        var lines = new List<string>();
        foreach (var p in Partners(pages, el))
        {
            var label = string.IsNullOrWhiteSpace(p.Label) ? p.Definition.Name : p.Label;
            var folio = FolhaOf(pages, p);
            var pos = PositionOf(pages, p);
            var loc = folio > 0 && folio != currentFolio && pos.Length > 0 ? $"{folio}/{pos}" : pos;
            lines.Add($"{label} {loc}".Trim());
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Verifica se dois elementos podem ser vinculados (um mestre e um escravo).
    /// </summary>
    public static bool CanLink(PlacedElement a, PlacedElement b)
    {
        if (a is null || b is null || a == b)
            return false;
        return (a.IsMaster && b.IsSlave) || (a.IsSlave && b.IsMaster);
    }
}
