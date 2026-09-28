using System.Text.RegularExpressions;
using OperCAD.Application;
using OperCAD.Core.Documents;

namespace OperCAD.Application;

/// <summary>Modo de busca de texto.</summary>
public enum SearchMode
{
    /// <summary>Contém o texto (padrão).</summary>
    Contains,
    /// <summary>Correspondência exata (o campo inteiro deve coincidir).</summary>
    MatchExact,
    /// <summary>Expressão regular.</summary>
    MatchRegex
}

/// <summary>Filtro de busca por tipo de objeto.</summary>
public enum SearchFilter
{
    /// <summary>Todos os objetos.</summary>
    All,
    /// <summary>Apenas elementos posicionados.</summary>
    ElementsOnly,
    /// <summary>Apenas condutores.</summary>
    ConductorsOnly,
    /// <summary>Apenas textos independentes.</summary>
    TextsOnly
}

/// <summary>Ocorrência de um texto de busca encontrada em um objeto da folha.</summary>
public sealed record SearchOccurrence(
    DocumentObject Object,
    string Property,
    int Start,
    int Length,
    string Value)
{
    /// <summary>Rótulo exibível do objeto (ex.: "K1 — Bobina", "Texto", "Condutor 1").</summary>
    public string ObjectLabel => Object switch
    {
        PlacedElement el => string.IsNullOrWhiteSpace(el.Label)
            ? ElementNameTranslator.Translate(el.Definition.Name)
            : $"{el.Label} — {ElementNameTranslator.Translate(el.Definition.Name)}",
        TextItem => "Texto",
        Conductor c => string.IsNullOrWhiteSpace(c.Text) ? "Condutor" : $"Condutor {c.Text}",
        _ => Object.Name
    };
}

/// <summary>
/// Busca e substituição de textos na folha (port do busca e substituição do padrão CAD):
/// procura em etiquetas, comentários, funções, descrições, fabricante, designação,
/// textos independentes e no número do condutor.
/// </summary>
public static class DiagramSearch
{
    /// <summary>Campos pesquisáveis de um objeto (nome da propriedade + valor atual).</summary>
    private static IEnumerable<(string Property, string Value)> FieldsOf(DocumentObject obj) => obj switch
    {
        PlacedElement el => new[]
        {
            ("Etiqueta", el.Label ?? string.Empty),
            ("Comentário", el.Comment ?? string.Empty),
            ("Função", el.Function ?? string.Empty),
            ("Descrição", el.Description ?? string.Empty),
            ("Fabricante", el.Manufacturer ?? string.Empty),
            ("Designação", el.Designation ?? string.Empty)
        },
        TextItem t => new[] { ("Texto", t.Text ?? string.Empty) },
        Conductor c => new[] { ("Número", c.Text ?? string.Empty) },
        _ => Array.Empty<(string, string)>()
    };

    /// <summary>Encontra todas as ocorrências de <paramref name="text"/> na página.</summary>
    public static List<SearchOccurrence> Find(Page page, string text, bool caseSensitive)
    {
        var results = new List<SearchOccurrence>();
        if (string.IsNullOrEmpty(text))
            return results;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var obj in page.Objects)
        {
            foreach (var (property, value) in FieldsOf(obj))
            {
                if (string.IsNullOrEmpty(value))
                    continue;
                var start = 0;
                while ((start = value.IndexOf(text, start, comparison)) >= 0)
                {
                    results.Add(new SearchOccurrence(obj, property, start, text.Length, value));
                    start += text.Length;
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Encontra ocorrências com modo de busca e filtro avançados.
    /// </summary>
    /// <param name="page">Folha a pesquisar.</param>
    /// <param name="text">Texto de busca.</param>
    /// <param name="caseSensitive">Diferencia maiúsculas de minúsculas.</param>
    /// <param name="mode">Modo de correspondência.</param>
    /// <param name="filter">Filtro por tipo de objeto.</param>
    public static List<SearchOccurrence> Find(
        Page page, string text, bool caseSensitive,
        SearchMode mode, SearchFilter filter)
    {
        var results = new List<SearchOccurrence>();
        if (string.IsNullOrEmpty(text))
            return results;

        // Filtrar objetos conforme o filtro selecionado
        IEnumerable<DocumentObject> objects = page.Objects;
        switch (filter)
        {
            case SearchFilter.ElementsOnly:
                objects = objects.OfType<PlacedElement>();
                break;
            case SearchFilter.ConductorsOnly:
                objects = objects.OfType<Conductor>();
                break;
            case SearchFilter.TextsOnly:
                objects = objects.OfType<TextItem>();
                break;
        }

        foreach (var obj in objects)
        {
            foreach (var (property, value) in FieldsOf(obj))
            {
                if (string.IsNullOrEmpty(value))
                    continue;

                switch (mode)
                {
                    case SearchMode.Contains:
                    {
                        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        var start = 0;
                        while ((start = value.IndexOf(text, start, comparison)) >= 0)
                        {
                            results.Add(new SearchOccurrence(obj, property, start, text.Length, value));
                            start += text.Length;
                        }
                        break;
                    }
                    case SearchMode.MatchExact:
                    {
                        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        if (string.Equals(value, text, comparison))
                            results.Add(new SearchOccurrence(obj, property, 0, value.Length, value));
                        break;
                    }
                    case SearchMode.MatchRegex:
                    {
                        try
                        {
                            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                            var regex = new Regex(text, options);
                            var matches = regex.Matches(value);
                            foreach (Match match in matches)
                            {
                                results.Add(new SearchOccurrence(obj, property, match.Index, match.Length, value));
                            }
                        }
                        catch (ArgumentException)
                        {
                            // Regex inválida — ignora silenciosamente
                        }
                        break;
                    }
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Substitui todas as ocorrências na página. Devolve um comando de undo composto
    /// (CompositeCommand) com as alterações JÁ APLICADAS — o chamador empilha na pilha
    /// do projeto. Se nada foi alterado, devolve null.
    /// </summary>
    public static OperCAD.Core.Undo.CompositeCommand? ReplaceAll(
        Page page,
        string search,
        string replacement,
        bool caseSensitive)
    {
        var occurrences = Find(page, search, caseSensitive);
        if (occurrences.Count == 0)
            return null;

        var commands = new List<OperCAD.Core.Undo.IUndoableCommand>();

        // Agrupa por objeto para gerar um comando por objeto alterado.
        foreach (var group in occurrences.GroupBy(o => o.Object))
        {
            var obj = group.Key;
            switch (obj)
            {
                case PlacedElement el:
                {
                    var before = OperCAD.Core.Undo.ElementInfoSnapshot.Of(el);
                    foreach (var occ in group)
                    {
                        var next = ApplyOne(el, occ.Property, search, replacement, caseSensitive);
                        if (next != occ.Value)
                            ApplyField(el, occ.Property, next);
                    }
                    commands.Add(new OperCAD.Core.Undo.ChangeElementPropertiesCommand(
                        el, before, OperCAD.Core.Undo.ElementInfoSnapshot.Of(el)));
                    break;
                }
                case TextItem t:
                {
                    var before = OperCAD.Core.Undo.TextItemSnapshot.Of(t);
                    t.Text = Replace(t.Text, search, replacement, caseSensitive);
                    commands.Add(new OperCAD.Core.Undo.ChangeTextItemPropertiesCommand(
                        t, before, OperCAD.Core.Undo.TextItemSnapshot.Of(t)));
                    break;
                }
                case Conductor c:
                {
                    var before = OperCAD.Core.Undo.ConductorInfoSnapshot.Of(c);
                    c.Text = Replace(c.Text, search, replacement, caseSensitive);
                    commands.Add(new OperCAD.Core.Undo.ChangeConductorPropertiesCommand(
                        c, before, OperCAD.Core.Undo.ConductorInfoSnapshot.Of(c)));
                    break;
                }
            }
        }

        return commands.Count == 0
            ? null
            : new OperCAD.Core.Undo.CompositeCommand($"Substituir \"{search}\" por \"{replacement}\"", commands);
    }

    private static string ApplyOne(PlacedElement el, string property, string search, string replacement, bool caseSensitive)
        => property switch
        {
            "Etiqueta" => Replace(el.Label, search, replacement, caseSensitive),
            "Comentário" => Replace(el.Comment, search, replacement, caseSensitive),
            "Função" => Replace(el.Function, search, replacement, caseSensitive),
            "Descrição" => Replace(el.Description, search, replacement, caseSensitive),
            "Fabricante" => Replace(el.Manufacturer, search, replacement, caseSensitive),
            "Designação" => Replace(el.Designation, search, replacement, caseSensitive),
            _ => throw new InvalidOperationException("Propriedade desconhecida: " + property)
        };

    /// <summary>
    /// Substitui todas as ocorrências com modo e filtro avançados.
    /// </summary>
    /// <param name="page">Folha a processar.</param>
    /// <param name="search">Texto de busca.</param>
    /// <param name="replacement">Texto de substituição.</param>
    /// <param name="caseSensitive">Diferencia maiúsculas de minúsculas.</param>
    /// <param name="mode">Modo de correspondência.</param>
    /// <param name="filter">Filtro por tipo de objeto.</param>
    public static OperCAD.Core.Undo.CompositeCommand? ReplaceAll(
        Page page,
        string search,
        string replacement,
        bool caseSensitive,
        SearchMode mode,
        SearchFilter filter)
    {
        var occurrences = Find(page, search, caseSensitive, mode, filter);
        if (occurrences.Count == 0)
            return null;

        var commands = new List<OperCAD.Core.Undo.IUndoableCommand>();

        // Agrupa por objeto para gerar um comando por objeto alterado.
        foreach (var group in occurrences.GroupBy(o => o.Object))
        {
            var obj = group.Key;
            switch (obj)
            {
                case PlacedElement el:
                {
                    var before = OperCAD.Core.Undo.ElementInfoSnapshot.Of(el);
                    foreach (var occ in group)
                    {
                        var next = ApplyOne(el, occ.Property, search, replacement, caseSensitive);
                        if (next != occ.Value)
                            ApplyField(el, occ.Property, next);
                    }
                    commands.Add(new OperCAD.Core.Undo.ChangeElementPropertiesCommand(
                        el, before, OperCAD.Core.Undo.ElementInfoSnapshot.Of(el)));
                    break;
                }
                case TextItem t:
                {
                    var before = OperCAD.Core.Undo.TextItemSnapshot.Of(t);
                    t.Text = Replace(t.Text, search, replacement, caseSensitive);
                    commands.Add(new OperCAD.Core.Undo.ChangeTextItemPropertiesCommand(
                        t, before, OperCAD.Core.Undo.TextItemSnapshot.Of(t)));
                    break;
                }
                case Conductor c:
                {
                    var before = OperCAD.Core.Undo.ConductorInfoSnapshot.Of(c);
                    c.Text = Replace(c.Text, search, replacement, caseSensitive);
                    commands.Add(new OperCAD.Core.Undo.ChangeConductorPropertiesCommand(
                        c, before, OperCAD.Core.Undo.ConductorInfoSnapshot.Of(c)));
                    break;
                }
            }
        }

        return commands.Count == 0
            ? null
            : new OperCAD.Core.Undo.CompositeCommand($"Substituir \"{search}\" por \"{replacement}\"", commands);
    }

    /// <summary>
    /// Substitui UMA ocorrência específica (encontrada por Find). Devolve um comando
    /// de undo já aplicado, ou null se o campo não existe mais.
    /// </summary>
    public static OperCAD.Core.Undo.IUndoableCommand? ReplaceSingle(SearchOccurrence occurrence, string replacement)
    {
        var obj = occurrence.Object;
        switch (obj)
        {
            case PlacedElement el:
            {
                var current = GetField(el, occurrence.Property);
                if (occurrence.Start < 0 || occurrence.Start + occurrence.Length > current.Length)
                    return null;
                var before = OperCAD.Core.Undo.ElementInfoSnapshot.Of(el);
                ApplyField(el, occurrence.Property,
                    current.Remove(occurrence.Start, occurrence.Length).Insert(occurrence.Start, replacement));
                return new OperCAD.Core.Undo.ChangeElementPropertiesCommand(
                    el, before, OperCAD.Core.Undo.ElementInfoSnapshot.Of(el));
            }
            case TextItem t:
            {
                var current = t.Text ?? string.Empty;
                if (occurrence.Start < 0 || occurrence.Start + occurrence.Length > current.Length)
                    return null;
                var before = OperCAD.Core.Undo.TextItemSnapshot.Of(t);
                t.Text = current.Remove(occurrence.Start, occurrence.Length).Insert(occurrence.Start, replacement);
                return new OperCAD.Core.Undo.ChangeTextItemPropertiesCommand(
                    t, before, OperCAD.Core.Undo.TextItemSnapshot.Of(t));
            }
            case Conductor c:
            {
                var current = c.Text ?? string.Empty;
                if (occurrence.Start < 0 || occurrence.Start + occurrence.Length > current.Length)
                    return null;
                var before = OperCAD.Core.Undo.ConductorInfoSnapshot.Of(c);
                c.Text = current.Remove(occurrence.Start, occurrence.Length).Insert(occurrence.Start, replacement);
                return new OperCAD.Core.Undo.ChangeConductorPropertiesCommand(
                    c, before, OperCAD.Core.Undo.ConductorInfoSnapshot.Of(c));
            }
            default:
                return null;
        }
    }

    private static string GetField(PlacedElement el, string property)
        => property switch
        {
            "Etiqueta" => el.Label ?? string.Empty,
            "Comentário" => el.Comment ?? string.Empty,
            "Função" => el.Function ?? string.Empty,
            "Descrição" => el.Description ?? string.Empty,
            "Fabricante" => el.Manufacturer ?? string.Empty,
            "Designação" => el.Designation ?? string.Empty,
            _ => string.Empty
        };

    private static void ApplyField(PlacedElement el, string property, string value)
    {
        switch (property)
        {
            case "Etiqueta": el.Label = value; break;
            case "Comentário": el.Comment = value; break;
            case "Função": el.Function = value; break;
            case "Descrição": el.Description = value; break;
            case "Fabricante": el.Manufacturer = value; break;
            case "Designação": el.Designation = value; break;
        }
    }

    private static string Replace(string? input, string search, string replacement, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var result = input;
        var index = 0;
        while ((index = result.IndexOf(search, index, comparison)) >= 0)
        {
            result = result.Remove(index, search.Length).Insert(index, replacement);
            index += replacement.Length;
        }
        return result;
    }
}
