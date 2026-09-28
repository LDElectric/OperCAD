namespace OperCAD.Application;

/// <summary>
/// Fornece caminhos portáteis baseados no diretório do executável.
/// Todos os dados da aplicação são salvos em uma pasta "data" ao lado do .exe.
/// </summary>
public static class PortablePath
{
    /// <summary>
    /// Diretório real do executável. Em publicação single-file o
    /// <c>AppContext.BaseDirectory</c> aponta para a pasta temporária de extração
    /// (que muda a cada execução), por isso usamos <see cref="Environment.ProcessPath"/>,
    /// que sempre retorna o caminho do .exe em uso.
    /// </summary>
    public static string ExecutableDirectory =>
        !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;

    private static readonly string AppDir = ExecutableDirectory;

    /// <summary>Diretório raiz dos dados (ao lado do executável).</summary>
    public static string DataDirectory => Path.Combine(AppDir, "data");

    /// <summary>Diretório dos arquivos de projeto (.xml).</summary>
    public static string ProjectsDirectory => Path.Combine(DataDirectory, "projects");

    /// <summary>Caminho do arquivo de registro de projetos.</summary>
    public static string RegistryPath => Path.Combine(DataDirectory, "projects.json");

    /// <summary>Caminho do arquivo de configurações.</summary>
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Caminho do arquivo de arquivos recentes.</summary>
    public static string RecentFilesPath => Path.Combine(DataDirectory, "recentfiles.json");

    /// <summary>Diretório de auto-save.</summary>
    public static string AutoSaveDirectory => Path.Combine(DataDirectory, "autosave");

    /// <summary>Cria todas as pastas necessárias se não existirem.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(AutoSaveDirectory);
    }
}
