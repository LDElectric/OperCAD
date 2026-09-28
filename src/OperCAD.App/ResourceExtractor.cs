using System.IO;
using System.IO.Compression;
using System.Reflection;
using OperCAD.Application;

namespace OperCAD.App;

/// <summary>
/// Extrai os recursos embutidos (elements_opcad, titleblocks, template.json)
/// para o diretório do executável na primeira execução. Em execuções subsequentes,
/// os arquivos já existem e nada é feito.
/// </summary>
public static class ResourceExtractor
{
    private const string ResourceName = "embedded-resources.zip";
    private const string MarkerFileName = ".resources-extracted";

    /// <summary>
    /// Verifica se os recursos já foram extraídos. Se não, extrai o .zip embutido
    /// para o diretório do executável (ao lado do .exe, não na pasta temporária).
    /// </summary>
    /// <returns>true se os recursos foram extraídos nesta chamada (primeira execução).</returns>
    public static bool ExtractIfNeeded()
    {
        var baseDir = PortablePath.ExecutableDirectory;
        var markerPath = Path.Combine(baseDir, MarkerFileName);

        if (File.Exists(markerPath))
            return false;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return false;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(baseDir, overwriteFiles: true);

        File.WriteAllText(markerPath, $"Extraído em {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        return true;
    }
}
