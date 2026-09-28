namespace OperCAD.Application;

/// <summary>
/// Mapeia nomes de pastas da biblioteca .opcad para nomes exibidos em PT-BR.
/// A biblioteca organiza 1.445 elementos em 4 domínios: Elétrico, Pneumático,
/// Lógica e Hidráulico, com subcategorias próprias.
/// </summary>
public static class CategoryMapper
{
    // ── Domínios (pastas raiz) ─────────────────────────────────────────────

    private static readonly Dictionary<string, string> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Eletrico"] = "Elétrico",
        ["Pneumatico"] = "Pneumático",
        ["Logica"] = "Lógica",
        ["Hidraulico"] = "Hidráulico"
    };

    // ── Subcategorias Elétrico ─────────────────────────────────────────────

    private static readonly Dictionary<string, string> ElectricCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alimentadores e fontes"] = "Alimentadores e Fontes",
        ["Fusiveis e disjuntores"] = "Fusíveis e Disjuntores",
        ["Contatores, Contatos e bobinas"] = "Contatores, Contatos e Bobinas",
        ["Chaves e botoeiras"] = "Chaves e Botoeiras",
        ["motores"] = "Motores",
        ["sensores"] = "Sensores",
        ["electronica"] = "Eletrônica",
        ["Conectores"] = "Conectores",
        ["terminais"] = "Terminais",
        ["transformadores"] = "Transformadores",
        ["Utilidades"] = "Utilidades"
    };

    // ── Subcategorias Pneumático ───────────────────────────────────────────

    private static readonly Dictionary<string, string> PneumaticCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Valvulas"] = "Válvulas",
        ["Atuadores"] = "Atuadores",
        ["Sensores"] = "Sensores",
        ["Utilizaveis"] = "Utilizáveis"
    };

    // ── Subcategorias Lógica ───────────────────────────────────────────────

    private static readonly Dictionary<string, string> LogicCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Portas Logicas"] = "Portas Lógicas",
        ["fluxograma"] = "Fluxogramas"
    };

    // ── Subcategorias Hidráulico ───────────────────────────────────────────

    private static readonly Dictionary<string, string> HydraulicCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Valvulas"] = "Válvulas",
        ["Atuadores"] = "Atuadores",
        ["Bombas"] = "Bombas"
    };

    // ── API pública ────────────────────────────────────────────────────────

    /// <summary>Obtém o nome de exibição de um domínio (ex.: "electric" → "Elétrico").</summary>
    public static string GetDomainName(string folderName)
        => Domains.TryGetValue(folderName, out var name) ? name : folderName;

    /// <summary>Obtém o nome de exibição de uma subcategoria dentro de um domínio.</summary>
    public static string GetCategoryName(string domain, string subcategory)
    {
        var dict = domain.ToLowerInvariant() switch
        {
            "eletrico" => ElectricCategories,
            "pneumatico" => PneumaticCategories,
            "logica" => LogicCategories,
            "hidraulico" => HydraulicCategories,
            _ => null
        };
        if (dict is not null && dict.TryGetValue(subcategory, out var name))
            return name;
        return subcategory;
    }

    /// <summary>Obtém o nome completo de uma categoria composta (ex.: "electric/power_feedings").</summary>
    public static string GetFullName(string composedCategory)
    {
        var parts = composedCategory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return composedCategory;

        var domain = GetDomainName(parts[0]);
        if (parts.Length == 1)
            return domain;

        var sub = GetCategoryName(parts[0], parts[1]);
        return $"{domain} → {sub}";
    }

    /// <summary>Retorna todos os domínios disponíveis.</summary>
    public static IReadOnlyDictionary<string, string> GetAllDomains() => Domains;

    /// <summary>Retorna as subcategorias de um domínio.</summary>
    public static IReadOnlyDictionary<string, string> GetSubcategories(string domain)
    {
        return domain.ToLowerInvariant() switch
        {
            "eletrico" => ElectricCategories,
            "pneumatico" => PneumaticCategories,
            "logica" => LogicCategories,
            "hidraulico" => HydraulicCategories,
            _ => new Dictionary<string, string>()
        };
    }
}
