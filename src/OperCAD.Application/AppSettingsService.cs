using System.Text.Json;

namespace OperCAD.Application;

/// <summary>
/// Configurações da aplicação (análogas às páginas do diálogo de configuração do padrão CAD:
/// Geral, Nova folha, Exportação e Impressão). Persistidas em JSON.
/// </summary>
public sealed class AppSettings
{
    // Geral
    public string Language { get; set; } = "pt_BR";
    public string DiagramFontFamily { get; set; } = "Segoe UI";
    public int MaxElementsInList { get; set; } = 500;

    // Nova folha
    public string NewPageSize { get; set; } = "A4";
    public int NewPageColumns { get; set; } = 8;
    public int NewPageRows { get; set; } = 6;
    public bool NewPageShowFrame { get; set; } = true;

    // Aparência
    public string GridStyle { get; set; } = "dots"; // "dots" ou "lines"
    public double GridSpacing { get; set; } = 2.5; // mm — base do snap; 5 mm e 2,5 mm coexistem (5 = 2×2,5)
    public bool SnapToGrid { get; set; } = true; // ajustar elementos à grade (ligado por padrão; segure Ctrl para posicionar livremente)

    // Exportação
    public string ExportFormat { get; set; } = "png";
    public double ExportResolutionPxPerMm { get; set; } = 4.0;
    public string ExportFolder { get; set; } = string.Empty;
    public bool ExportDrawTitleBlock { get; set; } = true;

    // Impressão
    public bool PrintDrawFrame { get; set; } = true;
    public bool PrintDrawTitleBlock { get; set; } = true;
    public bool PrintKeepConductorColors { get; set; } = true;
    public bool PrintDrawTerminals { get; set; } = true;
    public bool PrintFitInPage { get; set; } = true;

    // Exportação SVG / render
    public bool ExportSvgIncludeFrame { get; set; } = true;
    public bool ExportSvgTransparentBackground { get; set; } = false;
    public double ExportImageScale { get; set; } = 1.0;

    // Janela
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool IsMaximized { get; set; } = false;
    public bool IsToolBarVisible { get; set; } = true;
    public bool IsSidePanelVisible { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>
/// Carrega/salva as configurações da aplicação em
/// %LocalAppData%\OperCAD\settings.json.
/// </summary>
public sealed class AppSettingsService
{
    private static readonly Lazy<AppSettingsService> DefaultInstance = new(() => new AppSettingsService());

    /// <summary>Instância compartilhada da aplicação.</summary>
    public static AppSettingsService Default => DefaultInstance.Value;

    private readonly string _storePath;

    public AppSettingsService(string? storeDirectory = null)
    {
        var dir = storeDirectory ?? PortablePath.DataDirectory;
        _storePath = Path.Combine(dir, "settings.json");
    }

    public string StorePath => _storePath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_storePath))
                return new AppSettings();
            var json = File.ReadAllText(_storePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return loaded ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // falha silenciosa: configurações são não-críticas
        }
    }
}
