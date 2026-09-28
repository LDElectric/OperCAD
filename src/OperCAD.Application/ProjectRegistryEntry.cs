namespace OperCAD.Application;

/// <summary>
/// Entrada no registro de projetos. Armazena os metadados de um projeto
/// para exibição na lista lateral sem precisar ler o arquivo .xml.
/// </summary>
public sealed class ProjectRegistryEntry
{
    /// <summary>Identificador único do projeto.</summary>
    public Guid Id { get; set; }

    /// <summary>Nome dado pelo usuário.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Nome do arquivo .xml (Guid + extensão).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Data de criação do projeto.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Data da última modificação (salva).</summary>
    public DateTime LastModifiedAt { get; set; }
}
