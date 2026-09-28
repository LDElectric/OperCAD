using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OperCAD.Application;
using OperCAD.Core.Elements;
using OperCAD.Drawing;

using SkiaSharp;

namespace OperCAD.UI.ViewModels;

/// <summary>
/// ViewModel da barra de classes de elementos estilo CADE-SIMU.
/// Exibe uma barra horizontal com ícones de classes (sem texto).
/// Ao clicar numa classe, exibe uma segunda barra com os elementos dessa classe.
/// Os elementos são arrastáveis para a folha de desenho.
///
/// A barra aparece IMEDIATAMENTE ao abrir o app: as classes (ícones) são criadas de
/// forma síncrona por <see cref="InitializeShell"/> e os símbolos de cada classe são
/// preenchidos em segundo plano por <see cref="PopulateAsync"/> — sem "carregando".
/// </summary>
public sealed partial class ElementClassToolbarViewModel : ObservableObject
{
    // ── Estado ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ElementClassViewModel? _selectedClass;

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>
    /// Modo ativo da barra: "Elétrica" (Eletrico + Logica) ou "Fluidos"
    /// (Pneumatico + Hidraulico). Filtra quais classes aparecem na barra superior.
    /// </summary>
    [ObservableProperty]
    private ElementMode _selectedMode = ElementMode.Eletrica;

    /// <summary>Lista de classes de elementos (barra superior).</summary>
    public ObservableCollection<ElementClassViewModel> Classes { get; } = new();

    /// <summary>Elementos da classe selecionada (barra inferior).</summary>
    public ObservableCollection<ElementToolbarItemViewModel> SelectedClassElements { get; } = new();

    /// <summary>Disparado quando o usuário seleciona um elemento para inserir.</summary>
    public event Action<ElementDefinition>? InsertRequested;

    /// <summary>Disparado quando o usuário seleciona a ferramenta especial "Condutor".</summary>
    public event Action? ConductorToolRequested;

    /// <summary>Ativa a ferramenta de desenho de condutor (rota livre multi-segmentos).</summary>
    public void RequestConductorTool() => ConductorToolRequested?.Invoke();

    /// <summary>Rótulo do modo selecionado (exibido na barra de status).</summary>
    public string ModeStatus => SelectedMode switch
    {
        ElementMode.Fluidos => "Modo: Fluidos (Pneumática + Hidráulica)",
        _ => "Modo: Elétrica (Elétrica + Lógica)"
    };

    /// <summary>True quando o modo Elétrica está ativo (para o seletor segmentado).</summary>
    public bool IsEletricaMode => SelectedMode == ElementMode.Eletrica;

    /// <summary>True quando o modo Fluidos está ativo (para o seletor segmentado).</summary>
    public bool IsFluidosMode => SelectedMode == ElementMode.Fluidos;

    /// <summary>Definições já parseadas, por categoria — evita reparsear ao trocar de modo.</summary>
    private Dictionary<string, List<ElementDefinition>> _byCategory = new(StringComparer.OrdinalIgnoreCase);

    // ── Apresentação imediata (sem "carregando") ──────────────────────────

    /// <summary>
    /// Cria IMEDIATAMENTE as classes da barra (ícones + tooltips), sem esperar o
    /// carregamento dos elementos. Deve ser chamado de forma síncrona ao abrir o app.
    /// Os símbolos são preenchidos em segundo plano por <see cref="PopulateAsync"/>.
    /// </summary>
    public void InitializeShell()
    {
        RebuildClasses(SelectedMode);
        OnModeStatusChanged();
    }

    /// <summary>Reconstrói a lista de classes para o modo informado (mantém os elementos do cache).</summary>
    private void RebuildClasses(ElementMode mode)
    {
        Classes.Clear();
        foreach (var shell in GetShells(mode))
        {
            Classes.Add(new ElementClassViewModel
            {
                Id = shell.Id,
                Name = shell.Name,
                IconKey = shell.IconKey,
                ToolTip = shell.ToolTip,
                Category = shell.Category
            });
        }
        ApplyElementsFromCache();
        // Seleciona a primeira classe: a 2ª linha já aparece pronta quando os
        // símbolos da classe chegarem (preenchidos em background).
        if (Classes.Count > 0)
            SelectedClass = Classes[0];
        IsVisible = true;
    }

    /// <summary>Reage à troca de modo (seletor segmentado): repopula a barra e o status.</summary>
    partial void OnSelectedModeChanged(ElementMode value)
    {
        RebuildClasses(value);
        OnModeStatusChanged();
    }

    private void OnModeStatusChanged()
    {
        OnPropertyChanged(nameof(ModeStatus));
        OnPropertyChanged(nameof(IsEletricaMode));
        OnPropertyChanged(nameof(IsFluidosMode));
    }

    /// <summary>
    /// Carrega em segundo plano os elementos .opcad e preenche as classes (com os
    /// previews). Não bloqueia a interface: a barra já está visível (InitializeShell)
    /// e os símbolos vão surgindo conforme carregam.
    /// </summary>
    public async Task PopulateAsync()
    {
        try
        {
            var service = new ElementCollectionService();
            var opcadRoot = ElementCollectionService.DefaultOpCadPath();

            // 1. Escaneia e parseia apenas os elementos .opcad em background.
            var defs = await Task.Run(() =>
            {
                var scan = service.ScanCategories();
                return service.ParseBatch(scan.OpCadPaths, opcadRoot)
                    .Where(e => e.SourcePath.Contains("elements_opcad"))
                    .ToList();
            });

            // 2. Agrupa por categoria relativa (pastas PT-BR, ex.: "Eletrico/Conectores").
            var byCategory = new Dictionary<string, List<ElementDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in defs)
            {
                var rel = Path.GetRelativePath(opcadRoot, el.SourcePath).Replace('\\', '/');
                var folder = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : rel;
                if (!byCategory.TryGetValue(folder, out var list))
                    byCategory[folder] = list = new List<ElementDefinition>();
                list.Add(el);
            }
            foreach (var k in byCategory.Keys.ToList())
                byCategory[k] = byCategory[k]
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            // 3. Aplica no thread da UI e guarda em cache (reutilizável ao trocar de modo).
            if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher)
                return;
            await dispatcher.InvokeAsync(() =>
            {
                _byCategory = byCategory;
                ApplyElementsFromCache();
            });
        }
        catch
        {
            // Se falhar, a barra continua visível com as classes (eventualmente vazias).
        }
    }

    /// <summary>Preenche os elementos de cada classe a partir do cache de definições (thread da UI).</summary>
    private void ApplyElementsFromCache()
    {
        foreach (var cls in Classes)
        {
            cls.Elements.Clear();
            if (_byCategory.TryGetValue(cls.Category, out var list))
            {
                foreach (var el in list)
                    cls.Elements.Add(new ElementToolbarItemViewModel(el));
            }
        }
        RefreshSelectedClass();
    }

    /// <summary>Re-sincroniza a barra inferior com os elementos da classe selecionada.</summary>
    public void RefreshSelectedClass()
    {
        SelectedClassElements.Clear();
        if (SelectedClass is null)
            return;

        // Se a classe selecionada é "Condutor" ou "Tubulação", mostra apenas a ferramenta
        if (SelectedClass.Id == "condutor")
        {
            SelectedClassElements.Add(ElementToolbarItemViewModel.CreateConductorTool());
            return;
        }
        if (SelectedClass.Id == "tubulacao")
        {
            SelectedClassElements.Add(ElementToolbarItemViewModel.CreateConductorTool(
                "Tubulação",
                "Tubulação — ferramenta de desenho de conexão de fluidos (rota livre com snap em terminais)",
                new SKColor(0x0E, 0x7A, 0xC0)));
            return;
        }

        foreach (var item in SelectedClass.Elements)
            SelectedClassElements.Add(item);
    }

    // ── Seleção ───────────────────────────────────────────────────────────

    partial void OnSelectedClassChanged(ElementClassViewModel? value)
    {
        foreach (var c in Classes)
            c.IsSelected = false;
        if (value is not null)
            value.IsSelected = true;
        RefreshSelectedClass();
    }

    // ── Inserção ──────────────────────────────────────────────────────────

    /// <summary>Chamado quando o usuário clica num elemento da barra.</summary>
    [RelayCommand]
    private void InsertElement(ElementToolbarItemViewModel item)
    {
        if (item.IsConductorTool)
        {
            RequestConductorTool();
            return;
        }
        if (item.Definition is not null)
            InsertRequested?.Invoke(item.Definition);
    }

    // ── Definição das classes (estática — disponível sem carregar elementos) ──

    private sealed record ClassShell(string Id, string Name, string IconKey, string ToolTip, string Category, ElementMode Group);

    private static IEnumerable<ClassShell> GetShells(ElementMode mode)
        => AllClassShells.Where(s => s.Group == mode);

    private static readonly IReadOnlyList<ClassShell> AllClassShells = new ClassShell[]
    {
        // ── Modo "Elétrica" (Elétrico + Lógica) ─────────────────────────────
        new("condutor", "Condutor", "Icon.Class.Connections", "Condutor — ferramenta de desenho de conexão elétrica", "", ElementMode.Eletrica),
        new("alimentadores_fontes", "Alimentadores e Fontes", "Icon.Class.PowerFeedings", "Alimentadores e Fontes", "Eletrico/Alimentadores e fontes", ElementMode.Eletrica),
        new("fusiveis_disjuntores", "Fusíveis e Disjuntores", "Icon.Class.FusesDisconnects", "Fusíveis e Disjuntores", "Eletrico/Fusiveis e disjuntores", ElementMode.Eletrica),
        new("contatores_bobinas", "Contatores, Contatos e Bobinas", "Icon.Class.ContactorsSwitches", "Contatores, Contatos e Bobinas", "Eletrico/Contatores, Contatos e bobinas", ElementMode.Eletrica),
        new("chaves_botoeiras", "Chaves e Botoeiras", "Icon.Class.Controls", "Chaves e Botoeiras", "Eletrico/Chaves e botoeiras", ElementMode.Eletrica),
        new("motores", "Motores", "Icon.Class.Motors", "Motores", "Eletrico/motores", ElementMode.Eletrica),
        new("sensores", "Detectores e Sensores", "Icon.Class.Detectors", "Detectores e Sensores", "Eletrico/sensores", ElementMode.Eletrica),
        new("electronica", "Eletrônica", "Icon.Class.CoilsSignaling", "Eletrônica", "Eletrico/electronica", ElementMode.Eletrica),
        new("conectores", "Conectores", "Icon.Class.Connectors", "Conectores", "Eletrico/Conectores", ElementMode.Eletrica),
        new("transformadores", "Transformadores", "Icon.Class.Transformers", "Transformadores", "Eletrico/transformadores", ElementMode.Eletrica),
        new("utilidades", "Utilidades", "Icon.Class.Power", "Utilidades", "Eletrico/Utilidades", ElementMode.Eletrica),
        new("portas_logicas", "Portas Lógicas", "Icon.Class.LogicGates", "Portas Lógicas", "Logica/Portas Logicas", ElementMode.Eletrica),
        new("fluxogramas", "Fluxogramas", "Icon.Class.LogicFlowchart", "Fluxogramas", "Logica/fluxograma", ElementMode.Eletrica),

        // ── Modo "Fluidos" (Pneumática + Hidráulica) ────────────────────────
        new("tubulacao", "Tubulação", "Icon.Class.Connections", "Tubulação — ferramenta de desenho de conexão de fluidos", "", ElementMode.Fluidos),
        new("pneumatic_valves", "Válvulas Pneumáticas", "Icon.Class.PneumaticValves", "Válvulas Pneumáticas", "Pneumatico/Valvulas", ElementMode.Fluidos),
        new("pneumatic_actuators", "Atuadores Pneumáticos", "Icon.Class.PneumaticActuators", "Atuadores Pneumáticos", "Pneumatico/Atuadores", ElementMode.Fluidos),
        new("pneumatic_sensors", "Sensores Pneumáticos", "Icon.Class.PneumaticSensors", "Sensores Pneumáticos", "Pneumatico/Sensores", ElementMode.Fluidos),
        new("pneumatic_utilizaveis", "Utilizáveis", "Icon.Class.PneumaticOther", "Utilizáveis Pneumáticos", "Pneumatico/Utilizaveis", ElementMode.Fluidos),
        new("hydraulic_valves", "Válvulas Hidráulicas", "Icon.Class.HydraulicValves", "Válvulas Hidráulicas", "Hidraulico/Valvulas", ElementMode.Fluidos),
        new("hydraulic_actuators", "Atuadores Hidráulicos", "Icon.Class.HydraulicActuators", "Atuadores Hidráulicos", "Hidraulico/Atuadores", ElementMode.Fluidos),
        new("hydraulic_pumps", "Bombas Hidráulicas", "Icon.Class.HydraulicPumps", "Bombas Hidráulicas", "Hidraulico/Bombas", ElementMode.Fluidos)
    };
}

/// <summary>Modo de exibição da barra de classes: agrupa domínios afins.</summary>
public enum ElementMode
{
    /// <summary>Elétrico + Lógica.</summary>
    Eletrica,

    /// <summary>Pneumático + Hidráulico.</summary>
    Fluidos
}

// ── ViewModels auxiliares ─────────────────────────────────────────────────

/// <summary>Uma classe de elementos na barra (ex.: "Power Feedings").</summary>
public sealed partial class ElementClassViewModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string IconKey { get; init; } = "";
    public string ToolTip { get; init; } = "";

    /// <summary>Prefixo de categoria .opcad (ex.: "Eletrico/Conectores").</summary>
    public string Category { get; init; } = "";

    /// <summary>True quando esta classe está selecionada na barra (destacada).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<ElementToolbarItemViewModel> Elements { get; } = new();
}

/// <summary>Um elemento individual na barra de elementos.</summary>
public sealed class ElementToolbarItemViewModel
{
    /// <summary>Definição do elemento (null quando é a ferramenta especial de desenho de conexão).</summary>
    public ElementDefinition? Definition { get; }

    /// <summary>True quando este item é a ferramenta especial de desenho de conexão (não um símbolo físico).</summary>
    public bool IsConductorTool { get; }

    private readonly string _toolName;
    private readonly string _toolTip;

    public string Name => IsConductorTool ? _toolName : ElementNameTranslator.Translate(Definition!.Name);
    public string ToolTip => IsConductorTool ? _toolTip : ElementNameTranslator.Translate(Definition!.Name);

    /// <summary>Preview real do elemento renderizado a partir dos dados .opcad.</summary>
    public BitmapSource PreviewImage { get; }

    public ElementToolbarItemViewModel(ElementDefinition definition)
    {
        Definition = definition;
        IsConductorTool = false;
        _toolName = "";
        _toolTip = "";
        PreviewImage = RenderPreview(definition);
    }

    private ElementToolbarItemViewModel(string toolName, string toolTip, SKColor color)
    {
        Definition = null;
        IsConductorTool = true;
        _toolName = toolName;
        _toolTip = toolTip;
        PreviewImage = RenderConductorGlyph(color);
    }

    /// <summary>
    /// Cria o item da ferramenta de desenho de conexão: "Condutor" no modo Elétrica,
    /// "Tubulação" no modo Fluidos. A MESMA ferramenta, contextualizada por nome/glifo.
    /// </summary>
    public static ElementToolbarItemViewModel CreateConductorTool(
        string toolName = "Condutor",
        string? toolTip = null,
        SKColor? color = null)
        => new(toolName,
               toolTip ?? $"{toolName} — ferramenta de desenho de conexão (rota livre com snap em terminais)",
               color ?? new SKColor(0x1A, 0x1A, 0x1A));

    private static BitmapSource RenderPreview(ElementDefinition def)
    {
        try
        {
            const int size = 40;
            using var bitmap = ElementRenderer.RenderPreview(def, size, size);
            return ToBitmapSource(bitmap);
        }
        catch
        {
            // Fallback: imagem vazia
            return CreateFallbackImage();
        }
    }

    /// <summary>
    /// Renderiza o glifo da ferramenta de conexão: linha horizontal simples
    /// (representa o condutor/tubulação que liga terminais de elementos).
    /// </summary>
    private static BitmapSource RenderConductorGlyph(SKColor color)
    {
        const int size = 48;
        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = color,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawLine(8, 24, 40, 24, paint);
        }
        return ToBitmapSource(bitmap);
    }

    private static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        stream.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = stream;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }

    private static BitmapSource CreateFallbackImage()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            new Pen(Brushes.DarkGray, 1),
            new RectangleGeometry(new Rect(0, 0, 48, 48))));

        var drawingVisual = new DrawingVisual();
        using (var context = drawingVisual.RenderOpen())
        {
            context.DrawDrawing(group);
        }

        var renderTarget = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
        renderTarget.Render(drawingVisual);
        renderTarget.Freeze();
        return renderTarget;
    }
}