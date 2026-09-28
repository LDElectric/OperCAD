using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;
using OperCAD.Core.Undo;
using OperCAD.Drawing;
using OperCAD.UI.Controls;

namespace OperCAD.UI.ViewModels;

/// <summary>
/// ViewModel da janela principal do OperCAD (reimplementação do padrão CAD).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Instância estática atual (usada pelo ProjectListViewModel para acessar o estado).</summary>
    public static MainWindowViewModel? Instance { get; private set; }

    private SheetViewport? _sheet;
    private string? _currentFilePath;
    private readonly IProjectFileService _fileService = new ProjectFileService();

    /// <summary>ViewModel da lista de projetos (painel esquerdo).</summary>
    public ProjectListViewModel ProjectList { get; } = new();

    /// <summary>Id do projeto atualmente aberto no registro.</summary>
    public Guid? CurrentProjectId { get; private set; }

    public Project Project { get; private set; } = new();
    public ObservableRangeCollection<Page> Pages { get; } = new();
    public Page? ActivePage => Project.ActivePage;

    /// <summary>Árvore da aba "Projeto": folhas + elementos usados em cada folha.</summary>
    public ObservableRangeCollection<PageTreeItemViewModel> ProjectTree { get; } = new();

    /// <summary>Paleta de elementos da biblioteca padrão CAD.</summary>
    public ElementPaletteViewModel Palette { get; } = new();

    /// <summary>Barra de classes de elementos estilo CADE-SIMU.</summary>
    public ElementClassToolbarViewModel ElementToolbar { get; } = new();

    /// <summary>Propriedades de desenho (cor, espessura, dash, preenchimento) para novas formas.</summary>
    public DrawingPropertiesViewModel DrawingProperties { get; } = new();

    [ObservableProperty]
    private string _windowTitle = "OperCAD — Novo projeto";

    [ObservableProperty]
    private string _statusMessage = "Pronto";

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    private int _activePageIndex;

    [ObservableProperty]
    private bool _gridVisible = true;

    [ObservableProperty]
    private bool _guidesVisible;

    /// <summary>Crosshair com retas completas (estilo AutoCAD).</summary>
    [ObservableProperty]
    private bool _isFullCrosshair = true;

    /// <summary>Crosshair curto (cruz com semi-retas pequenas).</summary>
    [ObservableProperty]
    private bool _isShortCrosshair;

    /// <summary>Visibilidade do painel esquerdo (Projeto/Elementos).</summary>
    [ObservableProperty]
    private bool _isLeftPanelVisible = true;

    /// <summary>Visibilidade do painel direito (Propriedades/Desfazer).</summary>
    [ObservableProperty]
    private bool _isRightPanelVisible = false;

    [ObservableProperty]
    private bool _greyBackground;

    /// <summary>
    /// Snap à grade (elementos presos à grade). Ligado por padrão;
    /// segure Ctrl para posicionar livremente. Atalho: N.
    /// </summary>
    [ObservableProperty]
    private bool _snapToGrid = true;

    [ObservableProperty]
    private bool _isInserting;

    [ObservableProperty]
    private bool _isTextPending;

    [ObservableProperty]
    private bool _isImagePending;

    [ObservableProperty]
    private bool _isConductorTool;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _cursorPosition = "X: 0,0 mm  Y: 0,0 mm";

    [ObservableProperty]
    private string _zoomPercent = "Zoom: 100%";

    /// <summary>Escala dos elementos da folha ativa, exibida na barra de status.</summary>
    [ObservableProperty]
    private string _scaleStatus = "Escala: 1,0";

    [ObservableProperty]
    private bool _isSelectionMode = true;

    [ObservableProperty]
    private DocumentObject? _selectedObject;

    [ObservableProperty]
    private string _selectionType = "none"; // "none", "element", "conductor"

    // Campos do elemento selecionado (binding seguro no painel de propriedades:
    // evita erros de "path not found" quando o objeto selecionado não é um elemento).
    [ObservableProperty]
    private string _elementLabel = string.Empty;

    [ObservableProperty]
    private string _elementComment = string.Empty;

    [ObservableProperty]
    private string _elementFunction = string.Empty;

    [ObservableProperty]
    private string _elementDescription = string.Empty;

    [ObservableProperty]
    private string _elementManufacturer = string.Empty;

    [ObservableProperty]
    private string _elementDesignation = string.Empty;

    partial void OnSelectedObjectChanged(DocumentObject? value) => RefreshElementFields();

    private void RefreshElementFields()
    {
        var el = SelectedObject as PlacedElement;
        ElementLabel = el?.Label ?? string.Empty;
        ElementComment = el?.Comment ?? string.Empty;
        ElementFunction = el?.Function ?? string.Empty;
        ElementDescription = el?.Description ?? string.Empty;
        ElementManufacturer = el?.Manufacturer ?? string.Empty;
        ElementDesignation = el?.Designation ?? string.Empty;
    }

    partial void OnElementLabelChanged(string value)
    { if (SelectedObject is PlacedElement el) el.Label = value; }

    partial void OnElementCommentChanged(string value)
    { if (SelectedObject is PlacedElement el) el.Comment = value; }

    partial void OnElementFunctionChanged(string value)
    { if (SelectedObject is PlacedElement el) el.Function = value; }

    partial void OnElementDescriptionChanged(string value)
    { if (SelectedObject is PlacedElement el) el.Description = value; }

    partial void OnElementManufacturerChanged(string value)
    { if (SelectedObject is PlacedElement el) el.Manufacturer = value; }

    partial void OnElementDesignationChanged(string value)
    { if (SelectedObject is PlacedElement el) el.Designation = value; }

    private bool _isReadOnly;
    /// <summary>Indica se o projeto está em modo somente-leitura.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            _isReadOnly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReadOnlyStatusText));
        }
    }

    /// <summary>Texto de status exibido quando em modo somente-leitura.</summary>
    public string ReadOnlyStatusText => IsReadOnly ? "Somente Leitura" : "";

    private double _selectionHandleSize = 6.0;
    /// <summary>Tamanho dos handles de seleção (pontos de controle) em pixels.</summary>
    public double SelectionHandleSize
    {
        get => _selectionHandleSize;
        set
        {
            _selectionHandleSize = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Itens do painel Desfazer (descrições dos comandos aplicados).</summary>
    public ObservableRangeCollection<string> UndoItems { get; } = new();

    public void RefreshSheet() => _sheet?.Refresh();

    /// <summary>Escala dos elementos da folha ativa (padrão 1.0 se não houver folha).</summary>
    private double CurrentElementScale => ActivePage?.Settings.ElementScale ?? 1.0;

    /// <summary>Aplica as configurações de grade (estilo e espaçamento) ao viewport ativo.
    /// O espaçamento efetivo = base × ElementScale da folha, mantendo os terminais alinhados à malha.</summary>
    public void ApplyGridSettings()
    {
        if (_sheet is null) return;
        var settings = AppSettingsService.Default.Load();
        _sheet.Grid.Style = settings.GridStyle == "dots"
            ? Drawing.GridVisualStyle.Dots
            : Drawing.GridVisualStyle.Lines;
        _sheet.Grid.Spacing = settings.GridSpacing * CurrentElementScale;
        _sheet.IsSnapEnabled = settings.SnapToGrid;
        if (SnapToGrid != settings.SnapToGrid)
            SnapToGrid = settings.SnapToGrid;
        _sheet.Refresh();
    }

    /// <summary>
    /// Reaplica a escala dos elementos da folha ativa após alteração da configuração
    /// (ElementScale): atualiza o fator global de render, ajusta o grid proporcionalmente
    /// e recomputa os condutores para acompanhar os novos terminais.
    /// </summary>
    public void ApplyElementScale()
    {
        if (ActivePage is { } page)
            ElementDefinition.GlobalScale = page.Settings.ElementScale;
        ApplyGridSettings();
        ActivePage?.RecomputeConductors();
        UpdateStatusFromSheet();
    }

    /// <summary>
    /// Insere um elemento diretamente na posição especificada (drag-and-drop da barra de classes).
    /// As coordenadas são relativas ao SheetViewport.
    /// </summary>
    public void InsertElementAt(ElementDefinition def, double screenX, double screenY)
    {
        _sheet?.PlaceElementAt(def, screenX, screenY);
        StatusMessage = $"Inserido: {ElementNameTranslator.Translate(def.Name)}";
    }

    /// <summary>
    /// Insere um elemento na posição ATUAL do cursor (física da tela). Usado no drag-and-drop,
    /// cujo <c>DragEventArgs.GetPosition</c> devolve coordenada relativa à janela (bug que
    /// deslocava o elemento para longe do cursor).
    /// </summary>
    public void InsertElementAtCursor(ElementDefinition def)
    {
        if (def.IsConductor)
        {
            _sheet?.BeginConductorTool(def);
            StatusMessage = "Condutor: clique no ponto inicial, clique para os cotovelos da rota, snap em terminais e Enter/duplo clique para concluir (Esc cancela).";
        }
        else
        {
            _sheet?.PlaceElementAtCursor(def);
            StatusMessage = $"Inserido: {ElementNameTranslator.Translate(def.Name)}";
        }
    }

    public void AttachSheetViewport(SheetViewport sheet)
    {
        _sheet = sheet;
        sheet.ProjectPages = Project.Pages;
        sheet.TerminalStrips = Project.TerminalStrips;
        sheet.FolhaAutoNumFormula = Project.FolhaAutoNumFormula;
        sheet.UndoStack = Project.UndoStack;
        sheet.Page = ActivePage;
        sheet.DrawingProperties = DrawingProperties;

        // Quando propriedades mudam no painel, aplica às formas selecionadas
        DrawingProperties.PropertiesChanged += (_, _) =>
        {
            if (_sheet?.Page is null) return;
            var selectedShapes = _sheet.Page.Objects.OfType<ShapeItem>().Where(s => s.IsSelected).ToList();
            if (selectedShapes.Count == 0) return;
            var beforeSnapshots = selectedShapes.Select(s => ShapeItemSnapshot.Of(s)).ToList();
            foreach (var shape in selectedShapes)
            {
                shape.Color = DrawingProperties.SelectedColor;
                shape.PenWidth = DrawingProperties.SelectedPenWidth;
                shape.DashStyle = DrawingProperties.SelectedDashStyle;
                shape.Filling = DrawingProperties.Filling;
            }
            // Undo: usa o primeiro snapshot como referência (operação em grupo)
            if (selectedShapes.Count == 1)
            {
                _sheet.UndoStack?.Push(new ChangeShapePropertiesCommand(
                    selectedShapes[0], beforeSnapshots[0], ShapeItemSnapshot.Of(selectedShapes[0])));
            }
            else if (selectedShapes.Count > 1)
            {
                var cmds = selectedShapes.Select((s, i) =>
                    (IUndoableCommand)new ChangeShapePropertiesCommand(s, beforeSnapshots[i], ShapeItemSnapshot.Of(s))).ToList();
                _sheet.UndoStack?.Push(new CompositeCommand("Alterar propriedades de formas", cmds));
            }
            _sheet?.NotifyEditStateChanged();
            _sheet.Refresh();
        };

        // Inicializa o ViewModel da lista de projetos
        ProjectList.Initialize(
            confirmUnsavedChanges: ConfirmUnsavedChanges,
            openProjectAction: OpenProjectFromList,
            clearWorkspaceAction: () => ClearWorkspace());
        ProjectList.LoadProjects();

        // Aplica configurações de grade das configurações da aplicação (grid acompanha a escala)
        var settings = AppSettingsService.Default.Load();
        sheet.Grid.Style = settings.GridStyle == "dots"
            ? Drawing.GridVisualStyle.Dots
            : Drawing.GridVisualStyle.Lines;
        sheet.Grid.Spacing = settings.GridSpacing * (Project.ActivePage?.Settings.ElementScale ?? 1.0);
        sheet.IsSnapEnabled = settings.SnapToGrid;
        SnapToGrid = settings.SnapToGrid;

        // Toggle do snap pelo teclado (N) reflete na UI e persiste.
        sheet.SnapChanged += (_, _) => SnapToGrid = sheet.IsSnapEnabled;

        sheet.ViewChanged += (_, _) => UpdateStatusFromSheet();
        sheet.CursorMoved += (_, _) => UpdateCursorPosition();
        sheet.CrosshairMode = IsFullCrosshair ? CrosshairMode.Full : CrosshairMode.Short;
        sheet.InsertionChanged += (_, _) =>
        {
            IsInserting = sheet.PendingElement is not null;
            IsTextPending = sheet.PendingText is not null;
            IsImagePending = sheet.PendingImage is not null;
        };
        sheet.ConductorToolChanged += (_, _) => IsConductorTool = sheet.ConductorToolActive;
        sheet.DrawToolChanged += (_, _) => SyncDrawToolState(sheet.DrawTool);
        sheet.SelectionChanged += (_, _) =>
        {
            HasSelection = sheet.SelectedElements.Count > 0;
            // Quando uma única forma é selecionada, carrega suas propriedades no painel
            var selectedShapes = sheet.Page?.Objects.OfType<ShapeItem>().Where(s => s.IsSelected).ToList();
            if (selectedShapes is { Count: 1 })
            {
                DrawingProperties.LoadFromShape(selectedShapes[0]);
            }
            else
            {
                DrawingProperties.ClearShapeEditing();
            }
        };
        sheet.EditStateChanged += (_, _) =>
        {
            RefreshUndoItems();
            RefreshProjectTree();
            MarkModified();
            ScheduleAutoSave();
        };
        Palette.InsertRequested += def =>
        {
            if (def.IsConductor)
            {
                _sheet?.BeginConductorTool(def);
                StatusMessage = "Condutor: clique no ponto inicial, clique para os cotovelos da rota, snap em terminais e Enter/duplo clique para concluir (Esc cancela).";
            }
            else
            {
                _sheet?.BeginInsert(def);
                StatusMessage = $"Inserindo: {ElementNameTranslator.Translate(def.Name)} — clique para colocar (clique de novo p/ outra cópia; Esc ou botão direito libera).";
            }
        };
        ElementToolbar.InsertRequested += def =>
        {
            if (def.IsConductor)
            {
                _sheet?.BeginConductorTool(def);
                StatusMessage = "Condutor: clique no ponto inicial, clique para os cotovelos da rota, snap em terminais e Enter/duplo clique para concluir (Esc cancela).";
            }
            else
            {
                _sheet?.BeginInsert(def);
                StatusMessage = $"Inserindo: {ElementNameTranslator.Translate(def.Name)} — clique para colocar (clique de novo p/ outra cópia; Esc ou botão direito libera).";
            }
        };
        ElementToolbar.ConductorToolRequested += () =>
        {
            _sheet?.BeginConductorTool();
            StatusMessage = "Condutor: clique no ponto inicial, clique para os cotovelos da rota, snap em terminais e Enter/duplo clique para concluir (Esc cancela).";
        };
        // Barra de classes: exibida IMEDIATAMENTE (ícones) e preenchida em background.
        ElementToolbar.InitializeShell();
        _ = LoadPaletteAndToolbarAsync();
        LoadRecentFiles();
        RefreshUndoItems();
        UpdateStatusFromSheet();

        // Auto-save debounced: salva 3 segundos após a última edição
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            ExecuteAutoSave();
        };
        Instance = this;
    }

    /// <summary>Atualiza a lista de comandos do painel Desfazer.</summary>
    public void RefreshUndoItems()
    {
        var items = _sheet?.UndoDescriptions ?? Array.Empty<string>();
        UndoItems.ReplaceAll(items);
    }

    /// <summary>Reconstrói a árvore da aba "Projeto" (folhas + elementos usados em cada uma).</summary>
    public void RefreshProjectTree()
    {
        ProjectTree.Clear();
        foreach (var page in Project.Pages)
        {
            var node = new PageTreeItemViewModel { Page = page };
            foreach (var el in page.Objects.OfType<PlacedElement>())
                node.Elements.Add(new ElementUsageViewModel { Element = el });
            ProjectTree.Add(node);
        }
    }

    /// <summary>Renomeia uma folha (menu de contexto da aba de folha).</summary>
    public void RenamePage(Page page, string newName)
    {
        var name = newName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;
        page.Name = name;
        Pages.ReplaceAll(Project.Pages);
        RefreshProjectTree();
        MarkModified();
        StatusMessage = $"Folha renomeada para: {name}";
    }

    /// <summary>Exclui uma folha específica (menu de contexto da aba de folha).</summary>
    [RelayCommand]
    public void DeletePage(Page page)
    {
        if (Project.Pages.Count <= 1)
        {
            StatusMessage = "Não é possível excluir a única folha do projeto.";
            return;
        }
        var idx = Project.Pages.IndexOf(page);
        if (idx < 0)
            return;
        Project.Pages.Remove(page);
        Pages.ReplaceAll(Project.Pages);
        var newIdx = Math.Min(idx, Project.Pages.Count - 1);
        Project.ActivePage = Project.Pages[newIdx];
        ActivePageIndex = newIdx;
        RefreshProjectTree();
        MarkModified();
        StatusMessage = $"Folha excluída: {page.Name}";
        SyncSheet();
    }

    /// <summary>Carrega a paleta de elementos e preenche a barra de classes em background.</summary>
    private async Task LoadPaletteAndToolbarAsync()
    {
        // Barra de classes carrega os próprios .opcad em background (independente da paleta),
        // por isso já aparece visível assim que o app abre.
        _ = ElementToolbar.PopulateAsync();
        await Palette.LoadAsync();
    }

    /// <summary>Sincroniza os estados dos botões da barra "Adicionar" com a ferramenta ativa.</summary>
    private void SyncDrawToolState(Controls.InsertTool tool)
    {
        IsLineTool = tool == Controls.InsertTool.Line;
        IsRectangleTool = tool == Controls.InsertTool.Rectangle;
        IsEllipseTool = tool == Controls.InsertTool.Ellipse;
        IsTriangleTool = tool == Controls.InsertTool.Triangle;
        IsCurveTool = tool == Controls.InsertTool.Curve;
    }

    [RelayCommand]
    private void ToggleConductorTool()
    {
        if (_sheet is null)
            return;
        if (_sheet.ConductorToolActive)
            _sheet.EndConductorTool();
        else
            _sheet.BeginConductorTool();
        StatusMessage = IsConductorTool
            ? "Ferramenta condutor: clique em um terminal e depois em outro para ligar (Esc cancela)."
            : "Ferramenta condutor desativada.";
    }

    /// <summary>Ferramenta de desenho ativa na barra "Adicionar" (para checkable buttons).</summary>
    [ObservableProperty]
    private bool _isLineTool;

    [ObservableProperty]
    private bool _isRectangleTool;

    [ObservableProperty]
    private bool _isEllipseTool;

    [ObservableProperty]
    private bool _isTriangleTool;

    [ObservableProperty]
    private bool _isArcTool;

    [ObservableProperty]
    private bool _isEllipseArcTool;

    [ObservableProperty]
    private bool _isCurveTool;

    [ObservableProperty]
    private bool _isRoundedRectangleTool;

    [ObservableProperty]
    private bool _isMeasureTool;

    /// <summary>Ativa a ferramenta de linha (Adicionar → Linha).</summary>
    [RelayCommand]
    private void StartLineTool()
    {
        if (IsLineTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.Line);
        IsLineTool = true;
        StatusMessage = "Linha: clique no início e no fim (Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de retângulo (Adicionar → Retângulo).</summary>
    [RelayCommand]
    private void StartRectangleTool()
    {
        if (IsRectangleTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.Rectangle);
        IsRectangleTool = true;
        StatusMessage = "Retângulo: clique nos dois cantos (Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de elipse (Adicionar → Elipse).</summary>
    [RelayCommand]
    private void StartEllipseTool()
    {
        if (IsEllipseTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.Ellipse);
        IsEllipseTool = true;
        StatusMessage = "Elipse: clique nos dois cantos da caixa (Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de triângulo (Adicionar → Triângulo).</summary>
    [RelayCommand]
    private void StartTriangleTool()
    {
        if (IsTriangleTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.Triangle);
        IsTriangleTool = true;
        StatusMessage = "Triângulo: clique nos dois cantos da caixa (Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de arco (Adicionar → Arco).</summary>
    [RelayCommand]
    private void StartArcTool()
    {
        if (IsArcTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.Arc);
        IsArcTool = true;
        StatusMessage = "Arco (centro-raio-varredura): clique no centro, depois defina o raio; scroll ajusta a varredura e Shift+scroll o início (Enter/3º clique conclui, Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de arco de elipse (Adicionar → Arco de Elipse).</summary>
    [RelayCommand]
    private void StartEllipseArcTool()
    {
        if (IsEllipseArcTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.EllipseArc);
        IsEllipseArcTool = true;
        StatusMessage = "Arco de elipse: clique no centro, depois defina as extremidades da parábola (rx, ry); scroll ajusta a varredura e Shift+scroll o início (Enter/3º clique conclui, Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de curva suave (Adicionar → Curva).</summary>
    [RelayCommand]
    private void StartCurveTool()
    {
        if (IsCurveTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.Curve);
        IsCurveTool = true;
        StatusMessage = "Curva suave: clique nos pontos que a curva deve passar; botão direito desfaz o último ponto e Enter ou duplo clique conclui (Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de retângulo arredondado (Adicionar → Ret. Arredondado).</summary>
    [RelayCommand]
    private void StartRoundedRectangleTool()
    {
        if (IsRoundedRectangleTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.RoundedRectangle);
        IsRoundedRectangleTool = true;
        StatusMessage = "Retângulo arredondado: clique nos dois cantos (Esc cancela).";
    }

    /// <summary>Ativa a ferramenta de medição (Adicionar → Régua).</summary>
    [RelayCommand]
    private void StartMeasureTool()
    {
        if (IsMeasureTool)
        {
            EndDrawTool();
            return;
        }
        _sheet?.BeginDrawTool(Controls.InsertTool.Measure);
        IsMeasureTool = true;
        StatusMessage = "Régua: clique no início e no fim para medir a distância (Esc cancela).";
    }

    /// <summary>Desativa a ferramenta de desenho de formas.</summary>
    [RelayCommand]
    private void EndDrawTool()
    {
        _sheet?.EndDrawTool();
        IsLineTool = false;
        IsRectangleTool = false;
        IsEllipseTool = false;
        IsTriangleTool = false;
        IsArcTool = false;
        IsEllipseArcTool = false;
        IsCurveTool = false;
        IsRoundedRectangleTool = false;
        IsMeasureTool = false;
    }

    /// <summary>Espelha a seleção horizontalmente.</summary>
    [RelayCommand]
    private void MirrorSelectionHorizontal()
    {
        _sheet?.MirrorSelectionHorizontal();
        StatusMessage = "Seleção espelhada horizontalmente.";
    }

    /// <summary>Espelha a seleção verticalmente.</summary>
    [RelayCommand]
    private void MirrorSelectionVertical()
    {
        _sheet?.MirrorSelectionVertical();
        StatusMessage = "Seleção espelhada verticalmente.";
    }

    /// <summary>Gira a seleção por um ângulo livre (diálogo).</summary>
    [RelayCommand]
    private void RotateSelectionFree()
    {
        var dialog = new Views.RotateDialog { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            _sheet?.RotateSelectionFree(dialog.AngleDegrees);
            StatusMessage = $"Seleção girada {dialog.AngleDegrees}°.";
        }
    }

    /// <summary>Solicita a inserção de um texto independente (Adicionar → Texto).</summary>
    [RelayCommand]
    private void AddTextItem()
        => _sheet?.RequestTextPlacement();

    /// <summary>Solicita a inserção de uma imagem (Adicionar → Imagem).</summary>
    [RelayCommand]
    private void AddImageItem()
        => _sheet?.RequestImagePlacement();

    /// <summary>Inicia o posicionamento de um texto independente (chamado após o diálogo).</summary>
    public void BeginTextPlacement(TextItem item)
        => _sheet?.BeginTextPlacement(item);

    /// <summary>Inicia o posicionamento de uma imagem (chamado após o diálogo).</summary>
    public void BeginImagePlacement(ImageItem image)
        => _sheet?.BeginImagePlacement(image);

    /// <summary>Ativa o posicionamento de um plano de bornes na folha (Adicionar → Plano de bornes).</summary>
    [RelayCommand]
    private void AddTerminalStrip()
    {
        if (_sheet is null)
            return;
        if (TerminalStrips.Count == 0)
        {
            // Cria um borne padrão se ainda não houver nenhum no projeto.
            var strip = new OperCAD.Core.TerminalStrips.TerminalStrip { Name = "X1" };
            Project.TerminalStrips.Add(strip);
            MarkModified();
            StatusMessage = "Borne X1 criado. Posicione-o na folha (Esc cancela).";
            _sheet.BeginTerminalStripPlacement(strip);
            return;
        }
        _sheet.BeginTerminalStripPlacement(TerminalStrips[0]);
        StatusMessage = $"Posicione o borne {TerminalStrips[0].Name} na folha (Esc cancela).";
    }

    /// <summary>Bornes do projeto (para a barra Adicionar → Plano de bornes).</summary>
    public IReadOnlyList<OperCAD.Core.TerminalStrips.TerminalStrip> TerminalStrips
        => Project.TerminalStrips;

    /// <summary>Lista de arquivos recentes para o menu.</summary>
    public ObservableRangeCollection<RecentFileItem> RecentFiles { get; } = new();

    private readonly RecentFilesService _recentFilesService = new();
    private List<string> _recentPaths = new();
    private DispatcherTimer? _autoSaveTimer;
    private string? _autoSavePath;

    [RelayCommand]
    private void DeleteSelection()
    {
        _sheet?.DeleteSelection();
        HasSelection = false;
        StatusMessage = "Elementos excluídos.";
    }

    [RelayCommand]
    private void SelectImportedGroup()
    {
        StatusMessage = _sheet?.SelectSelectedImportGroup() == true
            ? "Grupo do PDF selecionado."
            : "Selecione uma linha ou texto importado do PDF primeiro.";
    }

    [RelayCommand]
    private void ToggleImportedGroupLock()
    {
        StatusMessage = _sheet?.ToggleSelectedImportGroupLock() == true
            ? "Estado de bloqueio do grupo do PDF alternado."
            : "Selecione uma linha ou texto importado do PDF primeiro.";
    }

    [RelayCommand]
    private void RotateSelection()
    {
        _sheet?.RotateSelection();
        StatusMessage = "Seleção girada 90°.";
    }

    /// <summary>
    /// Separa os objetos selecionados nos rótulos/traços que o PDF traz num único path — para
    /// poder selecionar, editar ou excluir cada um sozinho (funciona em desenho já importado).
    /// </summary>
    [RelayCommand]
    private void SplitSelectedShapes()
    {
        var count = _sheet?.SplitSelectedShapes() ?? 0;
        if (count == 0)
        {
            StatusMessage = "Selecione um objeto que contenha vários contornos (ex.: um rótulo " +
                            "com várias palavras) para separar.";
            return;
        }

        MarkModified();
        StatusMessage = $"Objeto separado em {count} partes.";
    }

    [RelayCommand]
    private void RotateWithAngle()
    {
        RotateWithAngleRequested?.Invoke();
    }

    public event Action? RotateWithAngleRequested;

    /// <summary>
    /// Passo do "zoom" de escala (Ctrl++ / Ctrl+-): 5% por tecla. Os dois sentidos são
    /// INVERSOS entre si (×1,05 e ÷1,05), então subir e descer a mesma quantidade de vezes
    /// devolve exatamente o tamanho original — o fator antigo (1,2 / 0,8) acumulava desvio.
    /// </summary>
    private const double ScaleStep = 1.05;

    /// <summary>MUDANÇA DE ESCALA da seleção — aumenta (Ctrl++), como o SCALE do CAD.</summary>
    [RelayCommand]
    private void ZoomElementIn() => ScaleSelection(ScaleStep);

    /// <summary>MUDANÇA DE ESCALA da seleção — diminui (Ctrl+-), como o SCALE do CAD.</summary>
    [RelayCommand]
    private void ZoomElementOut() => ScaleSelection(1 / ScaleStep);

    /// <summary>
    /// Escala a seleção (elementos .opcad, formas/linhas desenhadas ou importadas do PDF e
    /// textos) com o traçado das primitivas proporcional, sem tocar na grade.
    /// Antes isto mexia APENAS na escala de inserção de elementos .opcad, com fator
    /// multiplicativo e re-snap no grid a cada tecla (o desenho "andava" e traços/textos
    /// eram ignorados/sem undo).
    /// </summary>
    private void ScaleSelection(double factor)
    {
        if (_sheet is null)
            return;

        var message = _sheet.ScaleSelection(factor);
        if (message is null)
        {
            StatusMessage = "Selecione elementos, traços/linhas ou textos para aplicar a escala.";
            return;
        }

        MarkModified();
        RefreshUndoItems();
        UpdateStatusFromSheet();
        StatusMessage = message;
    }

    /// <summary>Desfaz o último comando (Ctrl+Z).</summary>
    [RelayCommand]
    private void Undo()
    {
        _sheet?.Undo();
        RefreshUndoItems();
        StatusMessage = "Desfeito.";
    }

    /// <summary>Refaz o próximo comando desfeito (Ctrl+Y).</summary>
    [RelayCommand]
    private void Redo()
    {
        _sheet?.Redo();
        RefreshUndoItems();
        StatusMessage = "Refeito.";
    }

    /// <summary>Desfaz até o item de índice especificado na lista de histórico.</summary>
    [RelayCommand]
    private void UndoToIndex(int index)
    {
        if (_sheet?.UndoStack is { } stack)
        {
            stack.UndoTo(index);
            _sheet.Refresh();
            RefreshUndoItems();
            StatusMessage = $"Desfazer até item {index}.";
        }
    }

    /// <summary>Recorta a seleção para a área de transferência (Ctrl+X).</summary>
    [RelayCommand]
    private void Cut()
    {
        _sheet?.CutSelection();
        HasSelection = false;
        RefreshUndoItems();
        StatusMessage = "Seleção recortada.";
    }

    /// <summary>Copia a seleção para a área de transferência (Ctrl+C).</summary>
    [RelayCommand]
    private void Copy()
    {
        _sheet?.CopySelection();
        StatusMessage = "Seleção copiada.";
    }

    /// <summary>Copia a seleção (sem retirar da folha) e cola o conteúdo na posição atual.</summary>
    [RelayCommand]
    private void Paste()
    {
        _sheet?.Paste();
        RefreshUndoItems();
        StatusMessage = "Conteúdo colado.";
    }

    /// <summary>Cola o conteúdo da área de transferência na posição atual do cursor.</summary>
    [RelayCommand]
    private void PasteHere()
    {
        _sheet?.PasteAtCursor();
        RefreshUndoItems();
        StatusMessage = "Conteúdo colado na posição do cursor.";
    }

    /// <summary>Seleciona todos os objetos da folha (Ctrl+A).</summary>
    [RelayCommand]
    private void SelectAll()
    {
        if (ActivePage is null)
            return;
        foreach (var obj in ActivePage.Objects)
            obj.IsSelected = true;
        _sheet?.Refresh();
        _sheet?.NotifySelectionChanged();
        HasSelection = true;
        StatusMessage = "Todos os objetos selecionados.";
    }

    /// <summary>Desseleciona todos os objetos da folha (Ctrl+Shift+A).</summary>
    [RelayCommand]
    private void DeselectAll()
    {
        if (ActivePage is null)
            return;
        foreach (var obj in ActivePage.Objects)
            obj.IsSelected = false;
        _sheet?.Refresh();
        _sheet?.NotifySelectionChanged();
        HasSelection = false;
        StatusMessage = "Seleção removida.";
    }

    /// <summary>Inverte a seleção (Ctrl+I).</summary>
    [RelayCommand]
    private void InvertSelection()
    {
        _sheet?.InvertSelection();
        _sheet?.NotifySelectionChanged();
        StatusMessage = "Seleção invertida.";
    }

    /// <summary>Trai o objeto selecionado para o topo (z-order).</summary>
    [RelayCommand]
    private void BringToFront()
    {
        _sheet?.BringToFront();
        RefreshUndoItems();
        StatusMessage = "Objeto trazido para a frente.";
    }

    /// <summary>Sobe um nível o objeto selecionado (z-order).</summary>
    [RelayCommand]
    private void Raise()
    {
        _sheet?.Raise();
        RefreshUndoItems();
        StatusMessage = "Objeto subiu um nível.";
    }

    /// <summary>Desce um nível o objeto selecionado (z-order).</summary>
    [RelayCommand]
    private void Lower()
    {
        _sheet?.Lower();
        RefreshUndoItems();
        StatusMessage = "Objeto desceu um nível.";
    }

    /// <summary>Envia o objeto selecionado para o fundo (z-order).</summary>
    [RelayCommand]
    private void SendToBack()
    {
        _sheet?.SendToBack();
        RefreshUndoItems();
        StatusMessage = "Objeto enviado para trás.";
    }

    /// <summary>Abre a janela de configuração do OperCAD.</summary>
    [RelayCommand]
    private void Configure()
        => ConfigRequested?.Invoke();

    /// <summary>Fecha o projeto atual (Ctrl+W) perguntando salvar se modificado.</summary>
    [RelayCommand]
    private void CloseProject() => ConfirmClose();

    /// <summary>Sai do aplicativo (Ctrl+Q), perguntando salvar se modificado.</summary>
    [RelayCommand]
    private void Exit() => ConfirmClose();

    /// <summary>
    /// Vincula dois elementos selecionados (referência cruzada mestre↔escravo):
    /// um mestre (ex.: bobina K1) e um escravo (ex.: contato K2).
    /// </summary>
    [RelayCommand]
    private void LinkElements()
    {
        if (_sheet is null)
            return;
        var sel = _sheet.SelectedElements;
        if (sel.Count != 2)
        {
            StatusMessage = "Selecione exatamente 2 elementos (1 mestre + 1 escravo) para vincular.";
            return;
        }
        var a = sel[0];
        var b = sel[1];
        if (!ProjectXref.CanLink(a, b))
        {
            StatusMessage = "Para vincular, um elemento deve ser mestre (ex.: bobina) e o outro escravo (ex.: contato).";
            return;
        }
        a.LinkTo(b);
        _sheet.Refresh();
        StatusMessage = $"Vinculados: {LabelOrName(a)} ↔ {LabelOrName(b)} (referência cruzada).";
    }

    /// <summary>Remove os vínculos entre os elementos selecionados.</summary>
    [RelayCommand]
    private void UnlinkElements()
    {
        if (_sheet is null)
            return;
        var sel = _sheet.SelectedElements;
        if (sel.Count < 2)
        {
            StatusMessage = "Selecione 2 ou mais elementos vinculados para desvincular.";
            return;
        }
        var count = 0;
        for (var i = 0; i < sel.Count; i++)
        {
            for (var j = i + 1; j < sel.Count; j++)
            {
                if (sel[i].LinkedUuids.Contains(sel[j].Id))
                {
                    sel[i].UnlinkFrom(sel[j]);
                    count++;
                }
            }
        }
        _sheet.Refresh();
        StatusMessage = count > 0
            ? $"{count} vínculo(s) de referência cruzada removido(s)."
            : "Nenhum vínculo encontrado entre os elementos selecionados.";
    }

    private static string LabelOrName(PlacedElement e)
        => string.IsNullOrWhiteSpace(e.Label) ? ElementNameTranslator.Translate(e.Definition.Name) : e.Label;

    /// <summary>Disparado ao pedir as propriedades do esquema ativo.</summary>
    public event Action<Page>? DiagramPropertiesRequested;

    /// <summary>Disparado ao pedir a janela de configuração.</summary>
    public event Action? ConfigRequested;

    /// <summary>Disparado ao pedir a janela de impressão.</summary>
    public event Action? PrintRequested;

    /// <summary>Disparado ao pedir a janela de exportação de folhas.</summary>
    public event Action? ExportRequested;

    /// <summary>Disparado ao pedir a janela de exportação em PDF (seleção de folhas).</summary>
    public event Action? PdfExportRequested;

    /// <summary>Disparado ao pedir a janela de reconhecimento assistido (marcar elementos).</summary>
    public event Action? ElementTriageRequested;

    /// <summary>Disparado para abrir o arquivo/nova janela (multi-projeto, como o padrão CAD).</summary>
    public event Action<string?>? OpenInNewWindowRequested;

    /// <summary>Disparado para fechar a janela atual (fechar projeto com várias janelas).</summary>
    public event Action? CloseWindowRequested;

    /// <summary>Disparado para buscar/substituir.</summary>
    public event Action? SearchRequested;

    /// <summary>Disparado para tela cheia.</summary>
    public event Action? ToggleFullScreenRequested;

    /// <summary>Janela sem conteúdo próprio: pode reutilizar para novo/abrir sem prompt.</summary>
    public bool IsPristine => !IsModified && string.IsNullOrEmpty(_currentFilePath);

    /// <summary>Abre um arquivo na janela atual (usado ao criar a janela já com arquivo).</summary>
    public void OpenInitialFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            // Tenta carregar o último projeto do registro
            var registryService = new ProjectRegistryService();
            var lastProject = registryService.GetMostRecent();
            if (lastProject is not null)
            {
                var filePath = registryService.GetFilePath(lastProject);
                if (System.IO.File.Exists(filePath))
                {
                    OpenProjectFromList(lastProject.Id, filePath);
                    return;
                }
            }
            // Se não houver último projeto, limpa a área de trabalho
            ClearWorkspace();
            return;
        }
        try
        {
            var loaded = _fileService.LoadAsync(path).GetAwaiter().GetResult();
            Project = loaded;
            GarantirMolduraEmTodasAsFolhas(Project);
            OnPropertyChanged(nameof(Project));
            OnPropertyChanged(nameof(ActivePage));
            Pages.ReplaceAll(Project.Pages);
            ActivePageIndex = Math.Max(0, Project.Pages.IndexOf(Project.ActivePage!));
            _currentFilePath = path;
            WindowTitle = $"OperCAD — {System.IO.Path.GetFileName(path)}";
            StatusMessage = $"Projeto aberto: {System.IO.Path.GetFileName(path)}";
            AddToRecentFiles(path);
            SyncSheet();
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro ao abrir projeto: " + ex.Message;
            ClearWorkspace();
        }
    }

    /// <summary>Marca o projeto como modificado (asterisco no título, prompt ao fechar).</summary>
    public void MarkModified()
    {
        IsModified = true;
        WindowTitle = (WindowTitle.EndsWith("*", StringComparison.Ordinal) ? WindowTitle : WindowTitle + " *");
    }

    /// <summary>Confirma o fechamento (pergunta salvar se modificado) — porta do padrão CAD.</summary>
    public void ConfirmClose()
    {
        if (!IsModified)
        {
            CloseWindowRequested?.Invoke();
            return;
        }
        var result = MessageBox.Show(
            "O projeto foi modificado. Deseja salvar as alterações?",
            "OperCAD — Salvar alterações",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            SaveProjectCommand.Execute(null);
            CloseWindowRequested?.Invoke();
        }
        else if (result == MessageBoxResult.No)
        {
            CloseWindowRequested?.Invoke();
        }
    }

    /// <summary>Pergunta ao usuário se deseja salvar alterações (para uso pelo ProjectListViewModel).</summary>
    /// <returns>True se o usuário quer salvar, False se não.</returns>
    public bool ConfirmUnsavedChanges()
    {
        var result = MessageBox.Show(
            "Deseja salvar as alterações?",
            "OperCAD — Alterações não salvas",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>Abre um projeto a partir da lista lateral (painel de projetos).</summary>
    public void OpenProjectFromList(Guid id, string filePath)
    {
        try
        {
            var loaded = _fileService.LoadAsync(filePath).GetAwaiter().GetResult();
            Project = loaded;
            CurrentProjectId = id;
            GarantirMolduraEmTodasAsFolhas(Project);
            OnPropertyChanged(nameof(Project));
            OnPropertyChanged(nameof(ActivePage));
            Pages.ReplaceAll(Project.Pages);
            ActivePageIndex = Math.Max(0, Project.Pages.IndexOf(Project.ActivePage!));
            _currentFilePath = filePath;
            IsModified = false;
            WindowTitle = $"OperCAD — {Project.Name}";
            StatusMessage = $"Projeto aberto: {Project.Name}";
            AddToRecentFiles(filePath);
            SyncSheet();
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro ao abrir projeto: " + ex.Message;
        }
    }

    /// <summary>Registra a alteração de propriedades de um elemento no undo.</summary>
    public void PushElementProperties(PlacedElement el, OperCAD.Core.Undo.ElementInfoSnapshot before, OperCAD.Core.Undo.ElementInfoSnapshot after)
        => Project.UndoStack.Push(new OperCAD.Core.Undo.ChangeElementPropertiesCommand(el, before, after));

    /// <summary>Registra a alteração de propriedades de um condutor no undo.</summary>
    public void PushConductorProperties(Conductor cond, OperCAD.Core.Undo.ConductorInfoSnapshot before, OperCAD.Core.Undo.ConductorInfoSnapshot after)
        => Project.UndoStack.Push(new OperCAD.Core.Undo.ChangeConductorPropertiesCommand(cond, before, after));

    /// <summary>Registra a alteração de propriedades da folha no undo.</summary>
    public void PushFolhaProperties(Page page, OperCAD.Core.Undo.FolhaSnapshot before, OperCAD.Core.Undo.FolhaSnapshot after)
        => Project.UndoStack.Push(new OperCAD.Core.Undo.ChangeFolhaPropertiesCommand(page, before, after));

    [RelayCommand]
    private void AddPage()
    {
        var page = Project.AddPage();
        ApplyDefaultFolha(page);
        Pages.ReplaceAll(Project.Pages);
        Project.ActivePage = page;
        ActivePageIndex = Project.Pages.Count - 1;
        MarkModified();
        StatusMessage = $"Folha adicionada: {page.Name}";
        SyncSheet();
    }

    [RelayCommand]
    private void RemovePage()
    {
        if (Project.Pages.Count <= 1)
        {
            StatusMessage = "Não é possível excluir a única folha do projeto.";
            return;
        }
        var page = Project.Pages[ActivePageIndex];
        Project.Pages.Remove(page);
        Pages.ReplaceAll(Project.Pages);
        var idx = Math.Min(ActivePageIndex, Project.Pages.Count - 1);
        Project.ActivePage = Project.Pages[idx];
        ActivePageIndex = idx;
        MarkModified();
        StatusMessage = $"Folha excluída: {page.Name}";
        SyncSheet();
    }

    [RelayCommand]
    private void NextPage()
    {
        if (ActivePageIndex < Project.Pages.Count - 1)
            ActivePageIndex++;
        else
            StatusMessage = "Já está na última folha.";
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (ActivePageIndex > 0)
            ActivePageIndex--;
        else
            StatusMessage = "Já está na primeira folha.";
    }

    [RelayCommand]
    private void OpenDiagramProperties()
    {
        if (ActivePage is not null)
            DiagramPropertiesRequested?.Invoke(ActivePage);
    }

    [RelayCommand]
    private void ExportImage()
    {
        if (ActivePage is null)
            return;
        ExportRequested?.Invoke();
    }

    [RelayCommand]
    private void ExportPdf()
    {
        if (Project is null)
            return;
        PdfExportRequested?.Invoke();
    }

    /// <summary>Abre a ferramenta de reconhecimento assistido (marcar/triar elementos importados).</summary>
    [RelayCommand]
    private void ElementTriage()
    {
        ElementTriageRequested?.Invoke();
    }

    [RelayCommand]
    private void Print()
    {
        if (ActivePage is null)
            return;
        PrintRequested?.Invoke();
    }

    [RelayCommand]
    private void Renumber()
    {
        ProjectReports.Renumber(Project);
        MarkModified();
        SyncSheet();
        StatusMessage = "Elementos renumerados (K1, K2...).";
    }

    [RelayCommand]
    private void ExportWiringList()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "lista_fiacao"
        };
        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dlg.FileName, ProjectReports.WiringListCsv(Project), System.Text.Encoding.UTF8);
            StatusMessage = $"Lista de fiação exportada: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
    }

    [RelayCommand]
    private void ExportBillOfMaterials()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "nomenclatura"
        };
        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dlg.FileName, ProjectReports.BillOfMaterialsCsv(Project), System.Text.Encoding.UTF8);
            StatusMessage = $"Nomenclatura exportada: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
    }

    [RelayCommand]
    private void NewProject()
    {
        // Salva o projeto atual se modificado
        if (IsModified && CurrentProjectId.HasValue)
        {
            SaveProjectCommand.Execute(null);
        }

        // O dialog é aberto pelo MainWindow.xaml.cs (OnNewProjectFromPanel)
        // ou pelo ProjectListViewModel.CreateProject. Aqui apenas criamos
        // um projeto em branco sem registro (usado pelo startup).
        CreateBlankProject("Novo projeto");
    }

    /// <summary>Limpa a área de trabalho (Estado Vazio), mostrando uma folha em branco sem vinculá-la a um arquivo.
    /// A folha ABNT sempre é exibida, mesmo sem projetos salvos.</summary>
    public void ClearWorkspace()
    {
        var project = new Project { Name = "Novo projeto" };
        var page = project.AddPage("Folha em Branco");
        ApplyDefaultFolha(page);

        Project = project;
        Project.ActivePage = page;
        _currentFilePath = null;
        CurrentProjectId = null;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(ActivePage));
        Pages.ReplaceAll(Project.Pages);
        ActivePageIndex = 0;
        IsModified = false;
        WindowTitle = "OperCAD";
        StatusMessage = "Pronto.";
        SyncSheet();
        _sheet?.FitToPage();
    }

    /// <summary>Cria um projeto em branco com o nome especificado e o abre.
    /// NÃO salva na biblioteca — só será salvo quando houver conteúdo na folha.</summary>
    public void CreateBlankProject(string name)
    {
        var project = new Project { Name = name };
        var page = project.AddPage("Folha 01");
        ApplyDefaultFolha(page);

        Project = project;
        Project.ActivePage = page;
        _currentFilePath = null;
        CurrentProjectId = null;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(ActivePage));
        Pages.ReplaceAll(Project.Pages);
        ActivePageIndex = 0;
        IsModified = false;
        WindowTitle = $"OperCAD — {name}";
        StatusMessage = $"Novo projeto: {name}";
        SyncSheet();
        _sheet?.FitToPage();
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Projeto OperCAD (*.xml)|*.xml|Projeto OperCAD legado (*.opcad)|*.opcad|Documento PDF (*.pdf)|*.pdf|Todos os arquivos (*.*)|*.*",
            DefaultExt = ".xml"
        };
        if (dlg.ShowDialog() != true)
            return;
        if (dlg.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            PdfWizardRequested?.Invoke(dlg.FileName);
            return;
        }
        if (!IsPristine)
        {
            OpenInNewWindowRequested?.Invoke(dlg.FileName);
            return;
        }
        try
        {
            var loaded = await _fileService.LoadAsync(dlg.FileName);
            Project = loaded;
            OnPropertyChanged(nameof(Project));
            OnPropertyChanged(nameof(ActivePage));
            Pages.ReplaceAll(Project.Pages);
            ActivePageIndex = Math.Max(0, Project.Pages.IndexOf(Project.ActivePage!));
            _currentFilePath = dlg.FileName;
            WindowTitle = $"OperCAD — {System.IO.Path.GetFileName(dlg.FileName)}";
            StatusMessage = $"Projeto aberto: {System.IO.Path.GetFileName(dlg.FileName)}";
            AddToRecentFiles(dlg.FileName);
            SyncSheet();
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro ao abrir projeto: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ImportPdf()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Documento PDF (*.pdf)|*.pdf|Todos os arquivos (*.*)|*.*",
            Title = "Importar PDF"
        };
        if (dlg.ShowDialog() != true)
            return;

        PdfWizardRequested?.Invoke(dlg.FileName);
    }

    /// <summary>Disparado para abrir o wizard de importação de PDF (tratado pela view).</summary>
    public event Action<string>? PdfWizardRequested;

    /// <summary>Executa a importação com as opções escolhidas no wizard e registra o projeto.</summary>
    public async Task ImportPdfWithOptions(string filePath, PdfImportOptions options)
    {
        if (IsModified)
        {
            await SaveProject();
            if (IsModified)
                return;
        }

        try
        {
            var imported = new PdfImporter(options).Import(filePath);

            // Converter clusters reconhecidos em PlacedElement nativos
            if (options.RecognizedClusters is { Count: > 0 } && imported.Pages.Count > 0)
            {
                var page = imported.Pages[0];
                var placed = ClusterToElementConverter.ConvertAll(options.RecognizedClusters, page);
                foreach (var elem in placed)
                    page.Add(elem);
            }

            var registryService = new ProjectRegistryService();
            var baseName = imported.Name;
            var finalName = baseName;
            var counter = 2;
            while (registryService.NameExists(finalName))
                finalName = $"{baseName} {counter++}";
            imported.Name = finalName;

            var entry = registryService.Add(finalName)
                ?? throw new InvalidOperationException("Não foi possível registrar o projeto importado.");
            var projectFilePath = registryService.GetFilePath(entry);
            await _fileService.SaveAsync(imported, projectFilePath);

            Project = imported;
            CurrentProjectId = entry.Id;
            _currentFilePath = projectFilePath;
            IsModified = false;
            OnPropertyChanged(nameof(Project));
            OnPropertyChanged(nameof(ActivePage));
            Pages.ReplaceAll(Project.Pages);
            ActivePageIndex = Math.Max(0, Project.Pages.IndexOf(Project.ActivePage!));
            WindowTitle = $"OperCAD — {finalName}";
            StatusMessage = $"PDF importado: {System.IO.Path.GetFileName(filePath)} ({Project.Pages.Count} folha(s)).";
            ProjectList.LoadProjects();
            SyncSheet();
            _sheet?.FitToPage();
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro ao importar PDF: " + ex.Message;
        }
    }

    /// <summary>
    /// Importa shapes/textos de um PDF diretamente para a página ativa como objetos nativos editáveis.
    /// Usado pelo wizard de importação. A configuração de folha vem do próprio IMPORTADOR
    /// (<paramref name="sheetSettings"/>): o wizard pode ter importado em escala real (1:1) sobre
    /// A3/A2/etc., e forçar A4 aqui anularia a escala do desenho. Sem ela, usa a folha ABNT padrão (A4).
    /// </summary>
    public void ImportShapesDirectly(List<ShapeItem> shapes, List<TextItem> texts,
        PageSettings? sheetSettings = null)
    {
        if (ActivePage is null)
            return;

        // A folha (papel + moldura + guias) é a decidida pelo importador; sem ela, ABNT padrão.
        if (sheetSettings is not null)
            ApplySheetSettings(ActivePage.Settings, sheetSettings);
        else
            ConfigurePageAsAbntSheet(ActivePage);

        foreach (var shape in shapes)
            ActivePage.Add(shape);
        foreach (var text in texts)
            ActivePage.Add(text);

        MarkModified();
        RefreshProjectTree();
        SyncSheet();
        _sheet?.FitToPage();
        StatusMessage = $"Importado: {shapes.Count} formas, {texts.Count} textos. " +
                        $"Folha {DescribeSheet(ActivePage.Settings)}.";
    }

    /// <summary>Copia a configuração de folha decidida pelo importador (papel, moldura, guias, recuos).</summary>
    private static void ApplySheetSettings(PageSettings target, PageSettings source)
    {
        target.Size = source.Size;
        target.CustomWidth = source.CustomWidth;
        target.CustomHeight = source.CustomHeight;
        target.FolhaTemplate = source.FolhaTemplate;
        target.FolhaTemplateFile = source.FolhaTemplateFile;
        target.FolhaFrameInset = source.FolhaFrameInset;
        target.TitleBlockFile = source.TitleBlockFile;
        target.ColumnsCount = source.ColumnsCount;
        target.RowsCount = source.RowsCount;
        target.ElementScale = source.ElementScale;
        target.ShowFrame = source.ShowFrame;
        target.ShowGrid = source.ShowGrid;
        target.ShowColumns = source.ShowColumns;
        target.ShowRows = source.ShowRows;
        target.MarginLeft = source.MarginLeft;
        target.MarginRight = source.MarginRight;
        target.MarginTop = source.MarginTop;
        target.MarginBottom = source.MarginBottom;
    }

    /// <summary>Descrição curta da folha ("A3 420×297 mm") para a barra de status.</summary>
    private static string DescribeSheet(PageSettings s) =>
        (s.Size == PaperSize.Custom ? "sob medida" : s.Size.ToString()) +
        $" {s.Width:0.#}×{s.Height:0.#} mm";

    /// <summary>
    /// Configura uma página como folha ABNT padrão (template A4) com moldura, grade
    /// e dimensões de referência — usada ao importar PDFs para manter consistência.
    /// </summary>
    private static void ConfigurePageAsAbntSheet(Page page)
    {
        var builtin = FolhaTemplateCatalog.Builtin();
        if (builtin is null)
            return;

        var s = page.Settings;
        s.FolhaTemplate = builtin;
        s.FolhaTemplateFile = builtin.FileName;
        s.Size = builtin.Size;
        s.FolhaFrameInset = builtin.FrameInset > 0 ? builtin.FrameInset : s.FolhaFrameInset;
        s.ColumnsCount = builtin.ColumnsCount;
        s.RowsCount = builtin.RowsCount;
        s.TitleBlockFile = builtin.FileName;
        s.ElementScale = 1.0;
        s.ShowFrame = true;
        s.ShowGrid = true;
        s.ShowColumns = true;
        s.ShowRows = true;
        s.MarginLeft = builtin.FrameInset;
        s.MarginRight = builtin.FrameInset;
        s.MarginTop = builtin.FrameInset;
        s.MarginBottom = builtin.FrameInset;
    }

    [RelayCommand]
    private async Task SaveProject()
    {
        if (_currentFilePath is null)
        {
            // Projeto ainda não registrado — registrar e salvar
            var registryService = new ProjectRegistryService();
            var entry = registryService.Add(Project.Name);
            if (entry is null)
            {
                await SaveProjectAs();
                return;
            }
            _currentFilePath = registryService.GetFilePath(entry);
            CurrentProjectId = entry.Id;
            ProjectList.LoadProjects();
        }
        try
        {
            await _fileService.SaveAsync(Project, _currentFilePath);
            IsModified = false;
            if (CurrentProjectId.HasValue)
            {
                var registryService = new ProjectRegistryService();
                registryService.UpdateLastModified(CurrentProjectId.Value);
                ProjectList.LoadProjects();
            }
            WindowTitle = $"OperCAD — {Project.Name}";
            StatusMessage = $"Projeto salvo: {Project.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro ao salvar projeto: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveProjectAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Projeto OperCAD (*.xml)|*.xml",
            DefaultExt = ".xml",
            FileName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath ?? Project.Name)
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            _currentFilePath = dlg.FileName;
            await _fileService.SaveAsync(Project, dlg.FileName);
            IsModified = false;
            WindowTitle = $"OperCAD — {Project.Name}";
            StatusMessage = $"Projeto salvo: {Project.Name}";
            AddToRecentFiles(dlg.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = "Erro ao salvar projeto: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ZoomIn() => _sheet?.ZoomIn();

    [RelayCommand]
    private void ZoomOut() => _sheet?.ZoomOut();

    [RelayCommand]
    private void FitPage()
    {
        _sheet?.FitToPage();
        StatusMessage = "Ajustar página.";
    }

    [RelayCommand]
    private void FitToContent()
    {
        _sheet?.FitToContent();
        StatusMessage = "Ajustar ao conteúdo.";
    }

    partial void OnGridVisibleChanged(bool value)
    {
        if (_sheet is not null)
        {
            _sheet.Grid.IsVisible = value;
            _sheet.Refresh();
        }
        if (ActivePage is not null && ActivePage.Settings.ShowGrid != value)
        {
            ActivePage.Settings.ShowGrid = value;
            MarkModified();
        }
    }

    partial void OnGuidesVisibleChanged(bool value)
    {
        // Só age quando há mudança real (evita marcar como modificado ao trocar de folha).
        if (ActivePage is not null &&
            (ActivePage.Settings.ShowColumns != value || ActivePage.Settings.ShowRows != value))
        {
            ActivePage.Settings.ShowColumns = value;
            ActivePage.Settings.ShowRows = value;
            _sheet?.Refresh();
            MarkModified();
            StatusMessage = value ? "Guias (cabeçalhos) ativadas." : "Guias (cabeçalhos) desativadas.";
        }
    }

    partial void OnGreyBackgroundChanged(bool value)
    {
        if (_sheet is not null)
        {
            _sheet.GreyBackground = value;
            _sheet.Refresh();
        }
        StatusMessage = value ? "Fundo cinza ativado." : "Fundo cinza desativado.";
    }

    partial void OnIsFullCrosshairChanged(bool value)
    {
        if (value)
        {
            if (IsShortCrosshair)
                IsShortCrosshair = false;
        }
        else if (!IsShortCrosshair)
        {
            // Garante que um dos modos esteja sempre ativo (toggle em modo rádio).
            IsShortCrosshair = true;
            return;
        }
        ApplyCrosshairMode();
    }

    partial void OnIsShortCrosshairChanged(bool value)
    {
        if (value)
        {
            if (IsFullCrosshair)
                IsFullCrosshair = false;
        }
        else if (!IsFullCrosshair)
        {
            IsFullCrosshair = true;
            return;
        }
        ApplyCrosshairMode();
    }

    private void ApplyCrosshairMode()
    {
        if (_sheet is not null)
        {
            _sheet.CrosshairMode = IsFullCrosshair ? CrosshairMode.Full : CrosshairMode.Short;
            _sheet.Refresh();
        }
        StatusMessage = IsFullCrosshair
            ? "Cursor: retas completas (AutoCAD)."
            : "Cursor: cruz curta.";
    }

    [RelayCommand]
    private void ToggleGrid() => GridVisible = !GridVisible;

    partial void OnSnapToGridChanged(bool value)
    {
        if (_sheet is not null)
            _sheet.IsSnapEnabled = value;
        var settings = AppSettingsService.Default.Load();
        settings.SnapToGrid = value;
        AppSettingsService.Default.Save(settings);
        StatusMessage = value
            ? "Ajustar à grade ativado (N) — segure Ctrl para posicionar livremente."
            : "Ajustar à grade desativado (N).";
    }

    [RelayCommand]
    private void ToggleGuides() => GuidesVisible = !GuidesVisible;

    [RelayCommand]
    private void ToggleGreyBackground() => GreyBackground = !GreyBackground;

    partial void OnIsLeftPanelVisibleChanged(bool value)
    {
        StatusMessage = value
            ? "Painel esquerdo visível."
            : "Painel esquerdo ocultado — reabrir pelo menu Exibir.";
        ReajustarFolhaAposLayout();
    }

    partial void OnIsRightPanelVisibleChanged(bool value)
    {
        StatusMessage = value
            ? "Painel direito visível."
            : "Painel direito ocultado — reabrir pelo menu Exibir.";
        ReajustarFolhaAposLayout();
    }

    [RelayCommand]
    private void ToggleLeftPanel() => IsLeftPanelVisible = !IsLeftPanelVisible;

    [RelayCommand]
    private void ToggleRightPanel() => IsRightPanelVisible = !IsRightPanelVisible;

    /// <summary>Reajusta a folha depois que o layout colapsa/expande os painéis, para que ela ocupe todo o espaço novo.</summary>
    private void ReajustarFolhaAposLayout()
    {
        if (_sheet is null)
            return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => _sheet.FitToPage()));
    }

    [RelayCommand]
    private void ResetZoom()
    {
        _sheet?.ResetZoom();
        StatusMessage = "Zoom redefinido (100%).";
    }

    [RelayCommand]
    private void NotReady()
        => StatusMessage = "Em construção — recurso ainda não implementado.";

    /// <summary>Edita o item selecionado (Ctrl+E) — abre propriedades do elemento ou condutor.</summary>
    [RelayCommand]
    private void EditSelected()
    {
        if (SelectedObject is PlacedElement el)
            ElementPropertiesRequested?.Invoke(el);
        else if (SelectedObject is Conductor cond)
            ConductorPropertiesRequested?.Invoke(cond);
        else
            StatusMessage = "Nenhum elemento ou condutor selecionado.";
    }

    /// <summary>Abre o painel de busca/substituição (Ctrl+F).</summary>
    [RelayCommand]
    private void Search() => SearchRequested?.Invoke();

    /// <summary>Abre o diálogo de propriedades do projeto.</summary>
    [RelayCommand]
    private void ProjectProperties() => ProjectPropertiesRequested?.Invoke();

    /// <summary>Alterna tela cheia (F11).</summary>
    [RelayCommand]
    private void ToggleFullScreen() => ToggleFullScreenRequested?.Invoke();

    /// <summary>Alterna o modo somente-leitura.</summary>
    [RelayCommand]
    private void ToggleReadOnly()
    {
        IsReadOnly = !IsReadOnly;
        if (_sheet is not null)
            _sheet.IsReadOnly = IsReadOnly;
        StatusMessage = IsReadOnly
            ? "Modo somente-leitura ativado."
            : "Modo somente-leitura desativado.";
    }

    /// <summary>Disparado ao pedir propriedades de um elemento.</summary>
    public event Action<PlacedElement>? ElementPropertiesRequested;

    /// <summary>Disparado ao pedir propriedades de um condutor.</summary>
    public event Action<Conductor>? ConductorPropertiesRequested;

    /// <summary>Disparado ao pedir propriedades do projeto.</summary>
    public event Action? ProjectPropertiesRequested;

    /// <summary>Disparado ao pedir o editor de legendas.</summary>
    public event Action? TitleBlockEditorRequested;

    /// <summary>Disparado ao pedir o seletor de potencial.</summary>
    public event Action? PotentialSelectorRequested;

    /// <summary>Disparado ao pedir a autonumeração com fórmula.</summary>
    public event Action? AutoNumberFormulaRequested;

    /// <summary>Disparado ao pedir o editor de fórmulas.</summary>
    public event Action? FormulaEditorRequested;

    /// <summary>Disparado quando um elemento é importado via DXF (para localizar na paleta).</summary>
    public event Action<string?>? StatusElementNameChanged;

    public void SaveWindowSettings(double left, double top, double width, double height, bool maximized)
    {
        var settings = AppSettingsService.Default.Load();
        settings.WindowLeft = left;
        settings.WindowTop = top;
        settings.WindowWidth = width;
        settings.WindowHeight = height;
        settings.IsMaximized = maximized;
        AppSettingsService.Default.Save(settings);
    }

    public void RestoreWindowSettings(out double left, out double top, out double width, out double height, out bool maximized)
    {
        var settings = AppSettingsService.Default.Load();
        left = settings.WindowLeft;
        top = settings.WindowTop;
        width = settings.WindowWidth;
        height = settings.WindowHeight;
        maximized = settings.IsMaximized;
    }

    [RelayCommand]
    private void ToggleSelectionMode()
    {
        IsSelectionMode = true;
        StatusMessage = "Modo seleção ativado.";
    }

    [RelayCommand]
    private void ToggleVisualizationMode()
    {
        IsSelectionMode = false;
        if (_sheet is not null)
            _sheet.IsReadOnly = true;
        StatusMessage = "Modo visualização ativado.";
    }

    [RelayCommand]
    private void ExportConductorNumbering()
    {
        if (ActivePage is null)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "numeracao_condutores"
        };
        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dlg.FileName, ProjectReports.ConductorNumberingCsv(Project), System.Text.Encoding.UTF8);
            StatusMessage = $"Numeração de condutores exportada: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
    }

    [RelayCommand]
    private void CleanPage()
    {
        if (ActivePage is null) return;

        if (ActivePage.Objects.Count == 0)
        {
            StatusMessage = "A folha já está vazia.";
            return;
        }

        var result = MessageBox.Show(
            $"Apagar tudo na folha \"{ActivePage.Name}\"?",
            "Limpar folha",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        ActivePage.Clear();
        _sheet?.Refresh();
        MarkModified();
        StatusMessage = "Folha limpa.";
    }

    [RelayCommand]
    private void OpenSearchReplace()
    {
        SearchRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenTitleBlockEditor()
    {
        TitleBlockEditorRequested?.Invoke();
    }

    [RelayCommand]
    private void ImportDxf()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Arquivo DXF (*.dxf)|*.dxf|Todos os arquivos (*.*)|*.*",
            Title = "Importar DXF"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var def = DxfImporter.ImportFromDxf(dlg.FileName);
                StatusElementName = ElementNameTranslator.Translate(def.Name);
                StatusElementNameChanged?.Invoke(ElementNameTranslator.Translate(def.Name));
                StatusMessage = $"DXF importado: {ElementNameTranslator.Translate(def.Name)} ({def.Primitives.Count} primitivas). Clique na folha para posicionar.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Erro ao importar DXF: " + ex.Message;
            }
        }
    }

    [RelayCommand]
    private void ExportSvg()
    {
        if (ActivePage is null)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "SVG (*.svg)|*.svg",
            FileName = ActivePage.Name ?? "folha"
        };
        if (dlg.ShowDialog() == true)
        {
            SvgExporter.SaveToFile(ActivePage, dlg.FileName);
            StatusMessage = $"SVG exportado: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
    }

    [RelayCommand]
    private void ExportDxf()
    {
        if (ActivePage is null)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "DXF (*.dxf)|*.dxf",
            FileName = ActivePage.Name ?? "folha"
        };
        if (dlg.ShowDialog() == true)
        {
            DxfExporter.SaveToFile(ActivePage, dlg.FileName);
            StatusMessage = $"DXF exportado: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
    }

    [RelayCommand]
    private void OpenPotentialSelector()
    {
        PotentialSelectorRequested?.Invoke();
    }

    [RelayCommand]
    private void AutoNumberFormula()
    {
        AutoNumberFormulaRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenFormulaEditor()
    {
        FormulaEditorRequested?.Invoke();
    }

    [RelayCommand]
    private void LocateInPalette()
    {
        var name = SelectedObject switch
        {
            PlacedElement el => ElementNameTranslator.Translate(el.Definition.Name),
            _ => null
        };
        if (name is not null)
            Palette.LocateInPalette(name);
        else
            StatusMessage = "Selecione um elemento para localizar na paleta.";
    }

    [RelayCommand]
    private void SaveWindowState()
    {
        StatusMessage = "Estado da janela salvo.";
    }

    [RelayCommand]
    private void RestoreWindowState()
    {
        StatusMessage = "Estado da janela restaurado.";
    }

    [ObservableProperty]
    private string? _statusElementName;

    /// <summary>Atualiza o objeto selecionado para o painel de propriedades dinâmico.</summary>
    public void UpdateSelectedObject(DocumentObject? obj)
    {
        SelectedObject = obj;
        SelectionType = obj switch
        {
            PlacedElement => "element",
            Conductor => "conductor",
            _ => "none"
        };
    }

    partial void OnActivePageIndexChanged(int value)
    {
        if (value < 0 || value >= Project.Pages.Count)
            return;
        Project.ActivePage = Project.Pages[value];
        OnPropertyChanged(nameof(ActivePage));
        SyncSheet();
    }

    private void SyncSheet()
    {
        if (_sheet is null)
            return;
        UpdateAutomaticTitleBlockPagination();
        _sheet.Page = ActivePage;
        _sheet.ProjectPages = Project.Pages;
        _sheet.TerminalStrips = Project.TerminalStrips;
        _sheet.FolhaAutoNumFormula = Project.FolhaAutoNumFormula;
        _sheet.UndoStack = Project.UndoStack;

        // Sincroniza o estado do botão "Guias" com a folha ativa.
        if (ActivePage is not null)
        {
            GuidesVisible = ActivePage.Settings.ShowColumns;
            if (GridVisible != ActivePage.Settings.ShowGrid)
                GridVisible = ActivePage.Settings.ShowGrid;
        }

        // Garante que o grid acompanha a escala da folha ativa automaticamente
        // (ex.: ElementScale 0.8 → grid efetivo = 2,5 × 0,8 = 2,0 mm), sem precisar
        // abrir as propriedades da folha. Também define a escala de render e
        // recomputa os condutores (ApplyElementScale já chama ApplyGridSettings).
        ApplyElementScale();

        RefreshUndoItems();
        RefreshProjectTree();
        UpdateStatusFromSheet();
    }

    /// <summary>
    /// Mantém página/total automáticos enquanto o usuário não editou esses campos diretamente.
    /// Ao editar, as flags do <see cref="TitleBlock"/> desligam apenas o campo correspondente.
    /// </summary>
    private void UpdateAutomaticTitleBlockPagination()
    {
        var pages = Project.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            var titleBlock = pages[i].TitleBlock;
            if (titleBlock.SheetAutoNumbered)
                titleBlock.Sheet = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (titleBlock.TotalSheetsAutoNumbered)
                titleBlock.TotalSheets = pages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Garante que TODAS as folhas do projeto tenham moldura (template.json). Se alguma
    /// folha carregada/importada estiver sem template, aplica a moldura padrão — assim a
    /// área de desenho (clamp de posicionamento) sempre coincide com a moldura renderizada,
    /// nunca com as margens antigas (que deixavam as molduras contarem como área de desenho).
    /// </summary>
    private static void GarantirMolduraEmTodasAsFolhas(OperCAD.Core.Documents.Project project)
    {
        if (project is null)
            return;
        // Ao abrir/importar: FORÇA a folha ABNT padrão em todas as folhas com moldura,
        // substituindo qualquer moldura/template gravado diferente (não pode haver folha
        // concorrente com a folha padrão do OperCAD).
        foreach (var page in project.Pages)
            AplicarFolhaAbnt(page, aplicarTamanhoReferencia: false);
    }

    /// <summary>
    /// Aplica a moldura padrão (template.json) a uma folha nova.
    /// </summary>
    private static void ApplyDefaultFolha(Page page)
        => AplicarFolhaAbnt(page, aplicarTamanhoReferencia: true);

    /// <summary>
    /// Garante que uma folha (com moldura ativa) use EXATAMENTE a folha padrão do OperCAD
    /// (ABNT — template.json). Quando <paramref name="aplicarTamanhoReferencia"/> é true
    /// (folha nova), o papel segue o tamanho de referência do template. Em folhas já
    /// existentes que já tinham um template gravado, o tamanho de papel é preservado
    /// (apenas o template/marca é substituído pelo padrão ABNT); folhas sem template
    /// prévio também passam a seguir o tamanho de referência ABNT.
    /// </summary>
    private static void AplicarFolhaAbnt(Page page, bool aplicarTamanhoReferencia)
    {
        var s = page.Settings;
        if (!s.ShowFrame)
            return;
        var tinhaTemplate = s.FolhaTemplate is not null || !string.IsNullOrEmpty(s.FolhaTemplateFile);
        var builtin = FolhaTemplateCatalog.Builtin();
        if (builtin is null)
            return;
        s.FolhaTemplate = builtin;
        s.FolhaTemplateFile = builtin.FileName;
        s.FolhaFrameInset = builtin.FrameInset > 0 ? builtin.FrameInset : s.FolhaFrameInset;
        // Colunas/linhas da moldura: copia as contagens do template para as configurações
        // da folha, para que os botões +C/-C/+L/-L e "Guias" tenham efeito real na folha.
        if (builtin.ColumnsCount > 0)
            s.ColumnsCount = builtin.ColumnsCount;
        if (builtin.RowsCount > 0)
            s.RowsCount = builtin.RowsCount;
        // A folha padrão segue o tamanho de referência do template quando é nova ou quando
        // ainda não tinha um template gravado (moldura antiga/em branco sendo normalizada).
        if (aplicarTamanhoReferencia || !tinhaTemplate)
        {
            s.Size = builtin.Size;
            if (builtin.Size == PaperSize.Custom)
            {
                s.CustomWidth = builtin.CustomWidth;
                s.CustomHeight = builtin.CustomHeight;
            }
        }
    }

    private void UpdateStatusFromSheet()
    {
        if (_sheet is null)
            return;
        UpdateCursorPosition();
        ZoomPercent = $"Zoom: {_sheet.View.Scale * 100:0}%";
        ScaleStatus = $"Escala: {ScaleToNotation(ActivePage?.Settings.ElementScale ?? 1.0)}";
    }

    private static string ScaleToNotation(double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.001) return "1:1";
        if (scale > 1.0) return $"{scale:0.#}:1";
        return $"1:{1.0 / scale:0.#}";
    }

    /// <summary>Atualiza apenas as coordenadas (disparado a cada movimento do cursor).</summary>
    private void UpdateCursorPosition()
    {
        if (_sheet is null)
            return;
        var c = _sheet.CursorWorld;
        CursorPosition = $"X: {c.X:0,0.0} mm  Y: {c.Y:0,0.0} mm";
    }

    /// <summary>Carrega a lista de arquivos recentes do disco.</summary>
    public void LoadRecentFiles()
    {
        _recentPaths = _recentFilesService.Load().ToList();
        RecentFiles.ReplaceAll(RecentFilesService.ToItems(_recentPaths));
    }

    /// <summary>Adiciona um caminho à lista de arquivos recentes.</summary>
    public void AddToRecentFiles(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        _recentPaths = RecentFilesService.Add(_recentPaths, path);
        _recentFilesService.Save(_recentPaths);
        RecentFiles.ReplaceAll(RecentFilesService.ToItems(_recentPaths));
    }

    /// <summary>Abre um arquivo recente.</summary>
    [RelayCommand]
    private void OpenRecentFile(RecentFileItem item)
    {
        if (item is null)
            return;
        if (!IsPristine)
        {
            OpenInNewWindowRequested?.Invoke(item.Path);
            return;
        }
        OpenInitialFile(item.Path);
    }

    /// <summary>Salva automaticamente o projeto (auto-save debounced, 3s após última edição).</summary>
    private void AutoSave()
    {
        if (!IsModified)
            return;
        if (ActivePage is { } page && page.Objects.Count == 0)
            return;
        try
        {
            var path = _autoSavePath;
            if (string.IsNullOrEmpty(path))
            {
                var dir = PortablePath.AutoSaveDirectory;
                System.IO.Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, "autosave.xml");
                _autoSavePath = path;
            }
            _fileService.SaveAsync(Project, path).GetAwaiter().GetResult();
            StatusMessage = "Projeto salvo automaticamente.";
        }
        catch
        {
            // falha silenciosa: auto-save não é crítico
        }
    }

    /// <summary>Agenda o auto-save debounced (reseta o timer a cada edição).</summary>
    private void ScheduleAutoSave()
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Start();
    }

    /// <summary>Interrompe callbacks de auto-save quando a janela começa a fechar.</summary>
    public void StopAutoSave()
    {
        _autoSaveTimer?.Stop();
    }

    /// <summary>Executa o auto-save imediato no arquivo do projeto.
    /// Só salva se houver conteúdo na folha. Se o projeto ainda não foi registrado,
    /// registra na biblioteca automaticamente ao salvar.</summary>
    private void ExecuteAutoSave()
    {
        if (!IsModified)
            return;
        if (ActivePage is { } page && page.Objects.Count == 0)
            return;
        try
        {
            // Se o projeto ainda não tem arquivo, registrar na biblioteca agora
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                var registryService = new ProjectRegistryService();
                var entry = registryService.Add(Project.Name);
                if (entry is null)
                    return;
                _currentFilePath = registryService.GetFilePath(entry);
                CurrentProjectId = entry.Id;
                ProjectList.LoadProjects();
            }

            _fileService.SaveAsync(Project, _currentFilePath).GetAwaiter().GetResult();
            if (CurrentProjectId.HasValue)
            {
                var registryService = new ProjectRegistryService();
                registryService.UpdateLastModified(CurrentProjectId.Value);
            }
        }
        catch
        {
            // falha silenciosa
        }
    }
}

/// <summary>Nó da árvore da aba "Projeto": uma folha e os elementos nela usados.</summary>
public sealed class PageTreeItemViewModel
{
    public Page Page { get; init; } = null!;
    public string Name => Page.Name;
    public ObservableRangeCollection<ElementUsageViewModel> Elements { get; } = new();
}

/// <summary>Uso de um elemento na folha (etiqueta + nome traduzido do símbolo).</summary>
public sealed class ElementUsageViewModel
{
    public PlacedElement Element { get; init; } = null!;
    public string Display => string.IsNullOrWhiteSpace(Element.Label)
        ? ElementNameTranslator.Translate(Element.Definition.Name)
        : $"{Element.Label} — {ElementNameTranslator.Translate(Element.Definition.Name)}";
}

/// <summary>
/// Coleção observável simples com suporte a ReplaceAll (evita dependência extra).
/// </summary>
public sealed class ObservableRangeCollection<T> : System.Collections.ObjectModel.ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
