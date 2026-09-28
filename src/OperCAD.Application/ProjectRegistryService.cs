using System.Text.Json;

namespace OperCAD.Application;

/// <summary>
/// Serviço de persistência do registro de projetos. Mantém um índice JSON
/// com os metadados de todos os projetos, permitindo listar, criar, renomear
/// e excluir projetos sem ler/parsear os arquivos .xml.
/// Nomes duplicados são impedidos (case-insensitive), como no Windows Explorer.
/// </summary>
public sealed class ProjectRegistryService
{
    private readonly string _registryPath;
    private readonly string _projectsDirectory;

    public ProjectRegistryService(string? registryPath = null, string? projectsDirectory = null)
    {
        _registryPath = registryPath ?? PortablePath.RegistryPath;
        _projectsDirectory = projectsDirectory ?? PortablePath.ProjectsDirectory;
    }

    /// <summary>Carrega a lista de projetos do registro.</summary>
    public List<ProjectRegistryEntry> Load()
    {
        try
        {
            if (!File.Exists(_registryPath))
                return new List<ProjectRegistryEntry>();
            var json = File.ReadAllText(_registryPath);
            return JsonSerializer.Deserialize<List<ProjectRegistryEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<ProjectRegistryEntry>();
        }
        catch
        {
            return new List<ProjectRegistryEntry>();
        }
    }

    /// <summary>Salva a lista de projetos no registro.</summary>
    public void Save(List<ProjectRegistryEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(_registryPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_registryPath, json);
        }
        catch
        {
            // falha silenciosa
        }
    }

    /// <summary>Verifica se já existe um projeto com o nome especificado (case-insensitive).</summary>
    /// <param name="name">Nome a verificar.</param>
    /// <param name="excludeId">Id para excluir da verificação (usado ao renomear).</param>
    public bool NameExists(string name, Guid? excludeId = null)
    {
        var entries = Load();
        return entries.Any(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (excludeId is null || e.Id != excludeId));
    }

    /// <summary>
    /// Cria um novo projeto no registro. Retorna null se já existir um projeto
    /// com o mesmo nome (case-insensitive).
    /// </summary>
    public ProjectRegistryEntry? Add(string name)
    {
        if (NameExists(name))
            return null;

        var entries = Load();
        var id = Guid.NewGuid();
        var fileName = $"{id}.xml";
        var entry = new ProjectRegistryEntry
        {
            Id = id,
            Name = name,
            FileName = fileName,
            CreatedAt = DateTime.Now,
            LastModifiedAt = DateTime.Now
        };
        entries.Add(entry);
        Save(entries);
        return entry;
    }

    /// <summary>Remove um projeto do registro e deleta o arquivo .xml.</summary>
    public void Remove(Guid id)
    {
        var entries = Load();
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return;

        entries.Remove(entry);
        Save(entries);

        var filePath = Path.Combine(_projectsDirectory, entry.FileName);
        try
        {
            if (File.Exists(filePath))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    filePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }
        catch
        {
            // falha silenciosa ao deletar arquivo
        }
    }

    /// <summary>
    /// Renomeia um projeto. Retorna false se já existir outro projeto com o mesmo nome.
    /// </summary>
    public bool Rename(Guid id, string newName)
    {
        if (NameExists(newName, excludeId: id))
            return false;

        var entries = Load();
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return false;

        entry.Name = newName;
        entry.LastModifiedAt = DateTime.Now;
        Save(entries);
        return true;
    }

    /// <summary>Atualiza a data de última modificação de um projeto.</summary>
    public void UpdateLastModified(Guid id)
    {
        var entries = Load();
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return;

        entry.LastModifiedAt = DateTime.Now;
        Save(entries);
    }

    /// <summary>Retorna o projeto mais recentemente modificado, ou null se não houver nenhum.</summary>
    public ProjectRegistryEntry? GetMostRecent()
    {
        var entries = Load();
        return entries
            .OrderByDescending(e => e.LastModifiedAt)
            .FirstOrDefault();
    }

    /// <summary>Retorna o caminho completo do arquivo .xml de um projeto.</summary>
    public string GetFilePath(ProjectRegistryEntry entry)
        => Path.Combine(_projectsDirectory, entry.FileName);
}
