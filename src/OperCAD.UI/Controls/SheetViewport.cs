using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OperCAD.Application;
using OperCAD.Core.Conductors;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;
using OperCAD.Core.Undo;
using OperCAD.Drawing;
using OperCAD.UI.Views;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Controls;

/// <summary>
/// Viewport da folha de desenho: renderiza a página (grid, folha e moldura) via
/// SkiaSharp com zoom (roda do mouse) e pano (botão do meio ou direito). Suporta
/// inserção de elementos, condutores, seleção, mover, excluir e girar (fiel ao padrão CAD).
/// </summary>
public sealed class SheetViewport : Grid
{
    private readonly SKElement _canvas;
    private Page? _page;
    private Point2D _ghostWorld;

    /// <summary>
    /// ⭐ [DEBUG] Posição BRUTA do mouse sobre o canvas em DIPs (e.GetPosition), sem
    /// transformação — ground truth para a auditoria Screen→World→Screen.
    /// </summary>
    private System.Windows.Point _mouseCanvasPos;

    /// <summary>
    /// Fator DPI do canvas (pixels de dispositivo ÷ DIPs). Com <c>IgnorePixelScaling=true</c>
    /// o SKElement já informa <c>e.Info</c> em DIPs (fator 1.0). Se a biblioteca não aplicar
    /// o fator, o render fica em pixels de dispositivo enquanto o mouse usa DIPs — causando o
    /// deslocamento "elemento longe do cursor" que CRESCE com a distância da origem do canvas.
    /// Guardamos o fator real para manter canvas e mouse no mesmo espaço.
    /// </summary>
    private double _dpiCanvasScaleX = 1.0;
    private double _dpiCanvasScaleY = 1.0;

    /// <summary>
    /// Verdadeiro quando o usuário ajustou o zoom/pan manualmente (neste caso, redimensionar
    /// a janela NÃO re-ajusta a folha — preserva o ajuste do usuário). Se o usuário nunca
    /// mexeu no zoom/pan, o redimensionamento re-ajusta a folha (folha em evidência).
    /// </summary>
    private bool _viewUserAdjusted;

    // Estado de arrastar (mover) elemento selecionado
    private bool _isDragging;
    private Point2D _dragStartWorld;
    private List<(PlacedElement El, double Ox, double Oy)> _dragOrigins = new();
    private List<(TextItem Txt, double Ox, double Oy)> _dragTextOrigins = new();
    private List<(ShapeItem Shape, ShapeItemSnapshot Before)> _dragShapeOrigins = new();
    private List<(Conductor Cond, List<Point2D> OrigPath)> _dragConductorOrigins = new();

    /// <summary>Captura as posições iniciais de TODOS os tipos de objetos selecionados.</summary>
    private void CaptureAllDragOrigins()
    {
        _dragOrigins = SelectedElements.Select(el => (el, el.X, el.Y)).ToList();
        _dragConductorOrigins = _page!.Objects
            .OfType<Conductor>()
            .Where(c => c.IsSelected)
            .Select(c => (c, new List<Point2D>(c.Path)))
            .ToList();
        _dragTextOrigins = _page.Objects
            .OfType<TextItem>()
            .Where(t => t.IsSelected)
            .Select(t => (t, t.X, t.Y))
            .ToList();
        _dragShapeOrigins = _page.Objects
            .OfType<ShapeItem>()
            .Where(s => s.IsSelected)
            .Select(s => (s, ShapeItemSnapshot.Of(s)))
            .ToList();
    }

    /// <summary>Captura o estado de TODOS os tipos de objetos selecionados para a rotação rígida do grupo.</summary>
    private void CaptureAllRotationOrigins(Point2D center, Point2D world)
    {
        _rotCenter = center;
        _rotStartAngleDeg = AngleDeg(world, center);
        _rotOrigins = SelectedElements
            .Select(el => (el, el.Rotation, el.X, el.Y))
            .ToList();
        _rotShapeOrigins = _page!.Objects
            .OfType<ShapeItem>()
            .Where(s => s.IsSelected)
            .Select(s => (s, ShapeItemSnapshot.Of(s)))
            .ToList();
        _rotTextOrigins = _page.Objects
            .OfType<TextItem>()
            .Where(t => t.IsSelected)
            .Select(t => (t, TextItemSnapshot.Of(t)))
            .ToList();
        _rotConductorOrigins = _page.Objects
            .OfType<Conductor>()
            .Where(c => c.IsSelected)
            .Select(c => (c, new List<Point2D>(c.Path)))
            .ToList();
        _isRotating = true;
    }

    /// <summary>Centro da rotação do grupo: centro da bounding box de TODOS os objetos selecionados.</summary>
    private Point2D SelectionRotationCenter()
    {
        if (_page is null)
            return new Point2D(0, 0);
        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        var any = false;
        void Merge(Rect2D b)
        {
            if (b.Width <= 0 || b.Height <= 0) return;
            if (!any) { minX = b.MinX; maxX = b.MaxX; minY = b.MinY; maxY = b.MaxY; any = true; return; }
            minX = Math.Min(minX, b.MinX); maxX = Math.Max(maxX, b.MaxX);
            minY = Math.Min(minY, b.MinY); maxY = Math.Max(maxY, b.MaxY);
        }

        foreach (var s in _page.Objects.OfType<ShapeItem>().Where(s => s.IsSelected))
            Merge(s.Bounds);
        foreach (var el in SelectedElements)
            Merge(new Rect2D(el.X, el.Y, 1, 1));
        foreach (var t in _page.Objects.OfType<TextItem>().Where(t => t.IsSelected))
            Merge(new Rect2D(t.X, t.Y, 1, 1));
        foreach (var c in _page.Objects.OfType<Conductor>().Where(c => c.IsSelected && c.Path.Count > 0))
        {
            foreach (var p in c.Path)
            {
                if (!any) { minX = p.X; maxX = p.X; minY = p.Y; maxY = p.Y; any = true; }
                else
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
            }
        }

        if (!any)
            return new Point2D(0, 0);
        return new Point2D((minX + maxX) / 2, (minY + maxY) / 2);
    }

    /// <summary>Gira um ponto em torno de <paramref name="center"/> (transformação rígida).</summary>
    private static Point2D RotatePointAround(Point2D p, Point2D center, double angleDegrees)
    {
        if (angleDegrees == 0)
            return p;
        var rad = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        return new Point2D(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    /// <summary>
    /// Ao clicar (sem Shift) num objeto JÁ selecionado com uma multi-seleção ativa, o arrasto
    /// deve MOVER o conjunto inteiro — não colapsar a seleção num único objeto/grupo. Sem isso,
    /// clicar sobre um desenho+textos selecionados podia soltar parte do conjunto.
    /// </summary>
    private bool ShouldPreserveMultiSelectionOnClick(DocumentObject obj, bool shift)
        => !shift && obj.IsSelected && _page is not null
           && _page.Objects.Count(o => o.IsSelected) > 1;

    // Estado das alças de redimensionar/rotacionar de um texto selecionado
    private TextItem? _resizingText;
    private TextItemSnapshot _resizeBefore = null!;
    private double _resizeStartFont;
    private Point2D _resizeAnchor;
    private double _resizeStartDist;
    private bool _isTextResizing;

    private TextItem? _rotatingText;
    private TextItemSnapshot _rotateBefore = null!;
    private Point2D _rotateCenter;
    private bool _isTextRotating;

    // Estado da caixa de seleção (rubber band)
    private bool _isRubberBanding;
    private bool _rubberBandShift;
    private Point2D _rubberBandStart;
    private Point2D _rubberBandCurrent;

    // Estado da ciclagem de seleção de formas sobrepostas (clique repetido no mesmo ponto)
    private ShapeItem? _cycleShape;
    private Point2D _cycleWorld;
    private bool _cycleEnabled;

    // Estado da rotação com o mouse (arrastar um elemento selecionado para girá-lo)
    private bool _isRotating;
    private Point2D _rotCenter;
    private double _rotStartAngleDeg;

    // Estado do pan (arrastar com botão do meio do mouse)
    private bool _isPanning;
    private Point2D _panStartScreen;
    private List<(PlacedElement El, double Before, double Ox, double Oy)> _rotOrigins = new();
    private List<(ShapeItem Shape, ShapeItemSnapshot Before)> _rotShapeOrigins = new();
    private List<(TextItem Txt, TextItemSnapshot Before)> _rotTextOrigins = new();
    private List<(Conductor Cond, List<Point2D> OrigPath)> _rotConductorOrigins = new();

    /// <summary>
    /// Quando ativo (check no menu de contexto), arrastar um elemento selecionado com
    /// o mouse gira-o livremente ao redor do próprio centro, em vez de movê-lo.
    /// </summary>
    public bool IsMouseRotationEnabled { get; set; }

    /// <summary>Pilha de desfazer/refazer do projeto (atribuída pelo ViewModel).</summary>
    public UndoStack? UndoStack { get; set; }

    /// <summary>Área de transferência interna (copiar/colar elementos e condutores).</summary>
    public DiagramClipboard Clipboard { get; } = new();

    /// <summary>Disparado quando o estado de desfazer/refazer/área de transferência muda.</summary>
    public event EventHandler? EditStateChanged;

    public ViewTransform View { get; private set; } = ViewTransform.Identity;
    public GridSettings Grid { get; } = new() { IsVisible = true };

    /// <summary>
    /// Snap à grade ativo? (ligado por padrão; segure Ctrl para posicionar livremente).
    /// </summary>
    public bool IsSnapEnabled { get; set; } = true;

    /// <summary>Terminal sob o cursor (para highlight visual e snap).</summary>
    private (PlacedElement Element, int Index, Point2D Pos)? _hoverTerminal;

    /// <summary>Elemento sob o cursor (para hover highlight).</summary>
    private PlacedElement? _hoverElement;

    /// <summary>Texto independente sob o cursor (para hover highlight).</summary>
    private TextItem? _hoverText;

    /// <summary>Se verdadeiro, exibe o crosshair mundial alinhado ao grid.</summary>
    public bool ShowCrosshair { get; set; } = true;

    /// <summary>Forma do crosshair: Full (retas completas, estilo AutoCAD) ou Short (cruz curta).</summary>
    public CrosshairMode CrosshairMode { get; set; } = CrosshairMode.Full;

    /// <summary>Se verdadeiro, exibe highlight no terminal sob o cursor.</summary>
    public bool ShowTerminalHighlight { get; set; } = true;

    /// <summary>Se verdadeiro, exibe hover highlight nos elementos.</summary>
    public bool ShowElementHover { get; set; } = true;

    /// <summary>
    /// TextBox para edição inline de texto de elemento (tag).
    /// Criado dinamicamente quando o usuário clica em um texto dinâmico.
    /// </summary>
    private System.Windows.Controls.TextBox? _inlineTextBox;

    /// <summary>Disparado quando o snap é alternado (para o ViewModel sincronizar a UI).</summary>
    public event EventHandler? SnapChanged;

    /// <summary>Alterna o snap à grade (atalho N).</summary>
    public void ToggleSnap()
    {
        IsSnapEnabled = !IsSnapEnabled;
        SnapChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>Elemento selecionado na paleta aguardando posicionamento (ou null).</summary>
    public ElementDefinition? PendingElement { get; set; }

    /// <summary>Texto independente aguardando posicionamento (ferramenta "Adicionar texto").</summary>
    public TextItem? PendingText { get; set; }

    /// <summary>Imagem aguardando posicionamento (ferramenta "Adicionar imagem").</summary>
    public ImageItem? PendingImage { get; set; }

    /// <summary>Borne aguardando posicionamento (ferramenta "Adicionar plano de bornes").</summary>
    public OperCAD.Core.TerminalStrips.TerminalStrip? PendingTerminalStrip { get; set; }

    /// <summary>Ferramenta de condutor ativa (clicar em terminais para ligar).</summary>
    public bool ConductorToolActive { get; private set; }

    /// <summary>Ferramenta de desenho de formas ativa (linha, retângulo, elipse, polilinha).</summary>
    public InsertTool DrawTool { get; private set; } = InsertTool.None;

    /// <summary>Propriedades de desenho (cor, espessura, dash) para novas formas.</summary>
    public ViewModels.DrawingPropertiesViewModel? DrawingProperties { get; set; }

    /// <summary>Há alguma ferramenta de inserção ativa (elemento, forma, texto, imagem ou borne).</summary>
    public bool HasInsertionTool
        => PendingElement is not null || DrawTool != InsertTool.None ||
           PendingText is not null || PendingImage is not null || PendingTerminalStrip is not null;

    private readonly List<Point2D> _drawPoints = new();
    private Point2D _drawCursor;
    /// <summary>Verdadeiro quando o ponto da ferramenta de desenho veio de snap a um objeto
    /// existente (desenha o marcador quadrado no ponto engatado).</summary>
    private bool _drawSnapActive;
    private Point2D _pendingCursor;
    private double _measureResult = -1;
    private double _arcSweepPreview = 90;
    private double _arcStartAnglePreview = 0;

    // ── Estado da ferramenta de condutor (rota livre multi-segmentos) ─────
    /// <summary>Pontos clicados da rota em construção (o primeiro é o início).</summary>
    private readonly List<Point2D> _conductorPoints = new();
    private ElementTerminalRef? _conductorStartRef;
    private char _conductorStartOrient = 'e';
    private ElementTerminalRef? _conductorEndRef;
    private char _conductorEndOrient = 'e';
    private Point2D _conductorCursor;
    /// <summary>Elemento da biblioteca que originou o condutor (para espessura visual).</summary>
    private ElementDefinition? _conductorSourceElement;

    // ── Estado de edição de condutor existente ──────────────────────────
    /// <summary>Condutor sendo editado (ou null se nenhum).</summary>
    private Conductor? _editingConductor;
    /// <summary>Índice do vértice sendo arrastado (-1 = nenhum).</summary>
    private int _editingVertexIndex = -1;
    /// <summary>Índice do vértice sob o cursor (hover highlight).</summary>
    private int _editingHoverVertex = -1;
    /// <summary>Índice do segmento sob o cursor (para inserir ponto).</summary>
    private int _editingHoverSegment = -1;
    /// <summary>Se verdadeiro, está arrastando um vértice.</summary>
    private bool _isDraggingVertex;
    /// <summary>Caminho do condutor antes da edição (para undo).</summary>
    private List<Point2D>? _editingOriginalPath;
    private ShapeItem? _editingShape;
    private int _editingShapeVertexIndex = -1;
    private List<Point2D>? _editingShapeOriginalPoints;
    private bool _isDraggingShapeVertex;

    // Estado de edição de ângulo de arco
    private ShapeItem? _editingArcAngle;
    private bool _isDraggingArcStartAngle;
    private bool _isDraggingArcEndAngle;
    private ShapeItemSnapshot? _arcAngleEditBefore;
    private Point2D _arcAngleEditCenter;
    private double _arcAngleEditRadius;
    private double _arcAngleEditInitialAngle;

    // Estado de resize de forma (alças de canto/borda)
    private ShapeItem? _resizingShape;
    private ShapeItemSnapshot? _resizeShapeBefore;
    private int _resizeShapeHandleIndex = -1;
    private Rect2D _resizeShapeOrigBounds;
    private Point2D _resizeShapeAnchor;
    private bool _isShapeResizing;

    // Estado de rotação de forma (alça de rotação)
    private ShapeItem? _rotatingShape;
    private ShapeItemSnapshot? _rotateShapeBefore;
    private Point2D _rotateShapeCenter;
    private bool _isShapeRotating;

    // Estado de agrupamento

    // Guias de alinhamento (snap de alinhamento)
    private readonly List<(Point2D A, Point2D B)> _alignmentGuides = new();
    private const double AlignmentSnapTolerance = 2.0;

    // Escala mínima no redimensionamento por alças (1% do tamanho original):
    // permite reduzir bastante sem colapsar a forma a zero.
    private const double MinResizeScale = 0.01;

    /// <summary>Disparado quando a vista muda (zoom/pano/folha) — para atualizar a barra de status.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Disparado quando o cursor se move sobre a folha — para atualizar as coordenadas na barra de status.</summary>
    public event EventHandler? CursorMoved;

    /// <summary>Disparado quando o modo de inserção muda.</summary>
    public event EventHandler? InsertionChanged;

    /// <summary>Disparado quando a ferramenta de condutor é ativada/desativada.</summary>
    public event EventHandler? ConductorToolChanged;

    /// <summary>Disparado quando a ferramenta de desenho de formas muda.</summary>
    public event EventHandler? DrawToolChanged;

    /// <summary>Disparado em duplo-clique em um elemento (abrir propriedades).</summary>
    public event Action<PlacedElement>? ElementActivated;

    /// <summary>Disparado em duplo-clique em um condutor (abrir propriedades do condutor).</summary>
    public event Action<Conductor>? ConductorActivated;

    /// <summary>Disparado em duplo-clique no legenda (abrir "Propriedades da folha", como o padrão CAD).</summary>
    public event Action<Page>? TitleBlockActivated;

    /// <summary>Disparado em duplo-clique sobre um campo específico da legenda.</summary>
    public event Action<Page, DrawingFrameRenderer.TitleBlockFieldHit>? TitleBlockFieldActivated;
    public event EventHandler<ValueTuple<string, string, OperCAD.Core.Documents.TitleBlockFieldStyle>>? TitleBlockFieldCommitted;

    /// <summary>Disparado ao dar duplo-clique em um item de borne (abre o editor do borne).</summary>
    public event Action<OperCAD.Core.TerminalStrips.TerminalStrip>? TerminalStripActivated;

    /// <summary>Disparado ao pedir inserção de texto independente (barra Inserir).</summary>
    public event Action? TextPlacementRequested;

    /// <summary>Disparado ao pedir inserção de imagem (barra Inserir).</summary>
    public event Action? ImagePlacementRequested;

    /// <summary>Disparado quando a seleção muda.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Disparado ao clicar com o botão direito sobre um objeto (menu de contexto).</summary>
    public event Action<DocumentObject>? ObjectContextRequested;

    /// <summary>Disparado ao clicar com o botão direito em espaço vazio (abre Propriedades da folha).</summary>
    public event Action? EmptySpacePropertiesRequested;

    /// <summary>Elementos selecionados atualmente.</summary>
    public IReadOnlyList<PlacedElement> SelectedElements
    {
        get
        {
            if (_page is null)
                return Array.Empty<PlacedElement>();
            return _page.Objects.OfType<PlacedElement>().Where(e => e.IsSelected).ToList();
        }
    }

    /// <summary>Condutor selecionado atualmente (ou null se nenhum condutor estiver selecionado).</summary>
    public Conductor? SelectedConductor
    {
        get
        {
            if (_page is null)
                return null;
            return _page.Objects.OfType<Conductor>().FirstOrDefault(c => c.IsSelected);
        }
    }

    /// <summary>Objeto selecionado (elemento, condutor ou null).</summary>
    public DocumentObject? SelectedObject
    {
        get
        {
            if (_page is null)
                return null;
            return _page.Objects.FirstOrDefault(o => o.IsSelected);
        }
    }

    public Page? Page
    {
        get => _page;
        set
        {
            _page = value;
            FitToPage();
            _canvas.InvalidateVisual();
        }
    }

    /// <summary>
    /// Todas as folhas do projeto (para a referência cruzada mestre↔escravo entre
    /// folhas). Pode ser null (sem referência cruzada de projeto).
    /// </summary>
    public IReadOnlyList<Page>? ProjectPages { get; set; }

    /// <summary>Bornes do projeto (para render dos itens de borne na folha).</summary>
    public IReadOnlyList<OperCAD.Core.TerminalStrips.TerminalStrip>? TerminalStrips { get; set; }

    /// <summary>Modo visualização (somente leitura — como o "Modo visualização" do padrão CAD).</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Fundo cinza ao redor da folha (como o "Arrière-plan" do padrão CAD).</summary>
    public bool GreyBackground { get; set; }

    /// <summary>Fórmula da autonumeração de folhas (ex.: "%id/%total").</summary>
    public string? FolhaAutoNumFormula { get; set; }

    public SheetViewport()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        // IgnorePixelScaling=true: o SKElement escala o canvas para DIPs (coordenadas lógicas
        // do WPF). SEM isso, o canvas é renderizado em pixels de dispositivo e, em telas com
        // DPI > 100%, o conteúdo não coincide com a posição dos cliques (o mouse usa DIPs).
        _canvas = new SKElement { Focusable = true, IgnorePixelScaling = true };
        // ⭐ Esconde o cursor do SO sobre a área de desenho: apenas o crosshair é exibido.
        _canvas.Cursor = Cursors.None;
        Children.Add(_canvas);

        _canvas.PaintSurface += OnPaintSurface;
        _canvas.MouseWheel += OnMouseWheel;
        _canvas.MouseDown += OnMouseDown;
        _canvas.MouseUp += OnMouseUp;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeave += (_, _) => { };
        _canvas.SizeChanged += OnCanvasSizeChanged;
        _canvas.KeyDown += OnKeyDown;
        Loaded += (_, _) => FitToPage();
    }

    /// <summary>
    /// Quando o canvas muda de tamanho (janela maximizada/restaurada/redimensionada, painéis
    /// recolhidos), re-ajusta a folha SE o usuário não ajustou o zoom/pan manualmente.
    /// Sem isso, o View fica obsoleto (ajustado para o tamanho antigo) e a folha/posição
    /// dos cliques fica deslocada — bug de janela maximizada.
    /// </summary>
    private void OnCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            return;
        if (_viewUserAdjusted)
            return; // preserva o ajuste manual do usuário (zoom/pan)
        // Dispara após o layout estabilizar, para o FitToPage usar o tamanho final.
        Dispatcher.BeginInvoke(new Action(FitToPage), System.Windows.Threading.DispatcherPriority.Loaded);
        _canvas.InvalidateVisual();
    }

    /// <summary>Ativa o modo de inserção para o elemento dado.</summary>
    public void BeginInsert(ElementDefinition def)
    {
        PendingElement = def;
        // ⭐ Usa a posição ATUAL do mouse sobre o canvas (não o último CursorWorld, que
        // fica obsoleto quando o usuário clica na paleta com o mouse fora da folha).
        var mousePos = Mouse.GetPosition(_canvas);
        _ghostWorld = View.ScreenToWorld(new Point2D(mousePos.X, mousePos.Y));
        Cursor = Cursors.Cross;
        _canvas.Focus();
        _canvas.InvalidateVisual();
        InsertionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancela o modo de inserção.</summary>
    public void CancelInsert()
    {
        if (PendingElement is null)
            return;
        PendingElement = null;
        Cursor = Cursors.Arrow;
        _canvas.InvalidateVisual();
        InsertionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Insere um elemento diretamente na posição especificada (usado para drag-and-drop).
    /// As coordenadas são relativas ao controle (screen coordinates).
    /// Valida que o elemento sempre fica DENTRO dos bounds da folha (nunca fora).
    /// </summary>
    public void PlaceElementAt(ElementDefinition def, double screenX, double screenY)
    {
        if (_page is null)
            return;
        var world = View.ScreenToWorld(new Point2D(screenX, screenY));
        // ⭐ Fonte única de colocação (body centrado no cursor + clamp à área de desenho).
        var (x, y) = ComputePlacement(def, world);
        var placed = new PlacedElement(def) { X = x, Y = y };
        if (def.HasLabelField && string.IsNullOrEmpty(placed.Label))
            placed.Label = _page.NextLabel(ElementLabels.Prefix(def));
        _page.AddElement(placed);
        UndoStack?.Push(new AddObjectsCommand(_page, new[] { placed }, "Inserir elemento"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();

        // [DEBUG] saída do VS: corpo (mundo → tela física) vs mouse; e input do drop vs mouse real.
        GetCursorPos(out var mousePtDrop);
        DebugLogPlacement("DnD-solto", def, screenX, screenY, x, y, mousePtDrop.X, mousePtDrop.Y);
    }

    /// <summary>
    /// Posição do cursor (física, GetCursorPos) convertida para coordenadas DIP do canvas.
    /// É a fonte da verdade — independe do DragDrop e do DPI. Fórmula: canvasDip = (física − origem)/dpi.
    /// </summary>
    private (double X, double Y) CursorPhysicalToCanvasDip()
    {
        GetCursorPos(out var pt);
        var (ox, oy) = CanvasOriginPhysical();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_canvas);
        double x = (pt.X - ox) / dpi.DpiScaleX;
        double y = (pt.Y - oy) / dpi.DpiScaleY;
        return (x, y);
    }

    /// <summary>
    /// Insere um elemento na posição ATUAL do cursor (física da tela do PC). Usado no
    /// drag-and-drop: o <c>DragEventArgs.GetPosition</c> do WPF devolve coordenada relativa
    /// à janela (deslocada pela posição do Sheet na janela), fazendo o elemento cair longe
    /// do mouse. A posição física do cursor é a referência correta.
    /// </summary>
    public void PlaceElementAtCursor(ElementDefinition def)
    {
        if (_page is null)
            return;
        var (x, y) = CursorPhysicalToCanvasDip();
        PlaceElementAt(def, x, y);
    }

    /// <summary>Ativa a ferramenta de condutor (rota livre: clique em pontos, snap em terminais).</summary>
    public void BeginConductorTool(ElementDefinition? sourceElement = null)
    {
        CancelInsert();
        EndDrawTool();
        CancelPendingPlacement();
        ConductorToolActive = true;
        _conductorSourceElement = sourceElement;
        ClearConductorDrawing();
        Cursor = Cursors.Cross;
        _canvas.Focus();
        _canvas.InvalidateVisual();
        ConductorToolChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Desativa a ferramenta de condutor.</summary>
    public void EndConductorTool()
    {
        if (!ConductorToolActive)
            return;
        ConductorToolActive = false;
        _conductorSourceElement = null;
        ClearConductorDrawing();
        Cursor = Cursors.Arrow;
        _canvas.InvalidateVisual();
        ConductorToolChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Limpa o desenho da rota do condutor em construção.</summary>
    private void ClearConductorDrawing()
    {
        _conductorPoints.Clear();
        _conductorStartRef = null;
        _conductorEndRef = null;
    }

    /// <summary>
    /// Conclui a rota do condutor: cria um único <see cref="Conductor"/> com a rota
    /// desenhada (Path) e as referências aos terminais de início/fim (quando houver snap).
    /// </summary>
    private void FinishConductor()
    {
        if (_page is null || _conductorPoints.Count < 2)
        {
            ClearConductorDrawing();
            _canvas.InvalidateVisual();
            return;
        }

        var start = _conductorPoints[0];
        var end = _conductorPoints[^1];
        var intermediates = _conductorPoints
            .Skip(1)
            .Take(Math.Max(0, _conductorPoints.Count - 2))
            .ToList();

        var path = ConductorRouter.GenerateFreeRoute(
            start,
            _conductorStartRef is { } sr ? _conductorStartOrient : null,
            intermediates,
            end,
            _conductorEndRef is { } er ? _conductorEndOrient : null,
            Grid.Spacing);

        var cond = new Conductor
        {
            From = _conductorStartRef ?? ElementTerminalRef.Empty,
            To = _conductorEndRef ?? ElementTerminalRef.Empty,
            Path = path,
            Width = 0.29,
            PreservePath = true,
            SourceElementId = _conductorSourceElement?.Id
        };
        _page.Add(cond);
        UndoStack?.Push(new AddObjectsCommand(_page, new[] { cond }, "Criar condutor"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();

        // Mantém a ferramenta ativa para desenhar o próximo condutor.
        ClearConductorDrawing();
        _canvas.InvalidateVisual();
    }

    /// <summary>Registra o terminal de destino da rota (snap no terminal sob o cursor).</summary>
    private void SetEndTerminal((PlacedElement Element, int Index, Point2D Pos) hit)
    {
        _conductorEndRef = new ElementTerminalRef(hit.Element.Id, hit.Index);
        _conductorEndOrient = TerminalGeometry.WorldOrientation(
            hit.Element, hit.Element.Definition.Terminals[hit.Index]);
    }

    // ── Edição de condutor existente ───────────────────────────────────

    /// <summary>Entra no modo de edição de um condutor existente.</summary>
    public void EnterConductorEditMode(Conductor conductor)
    {
        ExitConductorEditMode(false);
        EndConductorTool();
        CancelInsert();
        _editingConductor = conductor;
        _editingOriginalPath = conductor.Path.Select(p => new Point2D(p.X, p.Y)).ToList();
        _editingVertexIndex = -1;
        _editingHoverVertex = -1;
        _editingHoverSegment = -1;
        _isDraggingVertex = false;
        SelectOnly(conductor);
        Cursor = Cursors.Cross;
        _canvas.Focus();
        _canvas.InvalidateVisual();
    }

    /// <summary>Sai do modo de edição de condutor.</summary>
    /// <param name="commit">Se verdadeiro e houver alterações, registra undo.</param>
    public void ExitConductorEditMode(bool commit = true)
    {
        if (_editingConductor is null)
            return;

        if (commit && _editingOriginalPath is not null && _editingConductor is not null)
        {
            var currentPath = _editingConductor.Path.Select(p => new Point2D(p.X, p.Y)).ToList();
            var changed = currentPath.Count != _editingOriginalPath.Count ||
                          currentPath.Zip(_editingOriginalPath).Any(p => p.First != p.Second);
            if (changed)
            {
                _editingConductor.SimplifyPath();
                var finalPath = _editingConductor.Path.Select(p => new Point2D(p.X, p.Y)).ToList();
                UndoStack?.Push(new ChangeConductorPathCommand(_editingConductor, _editingOriginalPath, finalPath));
                EditStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        _editingConductor = null;
        _editingOriginalPath = null;
        _editingVertexIndex = -1;
        _editingHoverVertex = -1;
        _editingHoverSegment = -1;
        _isDraggingVertex = false;
        Cursor = Cursors.Arrow;
        _canvas.InvalidateVisual();
    }

    /// <summary>Há um condutor sendo editado.</summary>
    public bool IsConductorEditing => _editingConductor is not null;

    private int HitTestConductorVertex(Point2D world, double tolerance)
    {
        if (_editingConductor is null) return -1;
        for (var i = 0; i < _editingConductor.Path.Count; i++)
        {
            if (world.DistanceTo(_editingConductor.Path[i]) <= tolerance)
                return i;
        }
        return -1;
    }

    private int HitTestConductorSegment(Point2D world, double tolerance)
    {
        if (_editingConductor is null) return -1;
        for (var i = 0; i < _editingConductor.Path.Count - 1; i++)
        {
            if (DistanceToSegment(world, _editingConductor.Path[i], _editingConductor.Path[i + 1]) <= tolerance)
                return i;
        }
        return -1;
    }

    private void InsertConductorVertex(int segmentIndex, Point2D world)
    {
        if (_editingConductor is null) return;
        var (sx, sy) = SnapToGrid(world);
        _editingConductor.Path.Insert(segmentIndex + 1, new Point2D(sx, sy));
        _canvas.InvalidateVisual();
    }

    private void RemoveConductorVertex(int index)
    {
        if (_editingConductor is null) return;
        if (_editingConductor.Path.Count <= 2) return;
        _editingConductor.Path.RemoveAt(index);
        _canvas.InvalidateVisual();
    }

    // ── Fim edição de condutor ──────────────────────────────────────────

    /// <summary>Ativa uma ferramenta de desenho de forma (linha, retângulo, elipse, polilinha).</summary>
    public void BeginDrawTool(InsertTool tool)
    {
        CancelInsert();
        EndConductorTool();
        CancelPendingPlacement();
        DrawTool = tool;
        _drawPoints.Clear();
        _drawSnapActive = false;
        Cursor = Cursors.Cross;
        _canvas.Focus();
        _canvas.InvalidateVisual();
        DrawToolChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Desativa a ferramenta de desenho de forma.</summary>
    public void EndDrawTool()
    {
        if (DrawTool == InsertTool.None)
            return;
        DrawTool = InsertTool.None;
        _drawPoints.Clear();
        _drawSnapActive = false;
        Cursor = Cursors.Arrow;
        _canvas.InvalidateVisual();
        DrawToolChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Solicita a inserção de um texto independente (a janela abre o diálogo).</summary>
    public void RequestTextPlacement() => TextPlacementRequested?.Invoke();

    /// <summary>Solicita a inserção de uma imagem (a janela abre o diálogo de arquivo).</summary>
    public void RequestImagePlacement() => ImagePlacementRequested?.Invoke();

    /// <summary>Ativa o modo de posicionamento de um texto independente.</summary>
    public void BeginTextPlacement(TextItem item)
    {
        CancelInsert();
        EndConductorTool();
        EndDrawTool();
        PendingText = item;
        _pendingCursor = CursorWorld;
        Cursor = Cursors.Cross;
        _canvas.Focus();
        _canvas.InvalidateVisual();
        InsertionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ativa o modo de posicionamento de uma imagem.</summary>
    public void BeginImagePlacement(ImageItem image)
    {
        CancelInsert();
        EndConductorTool();
        EndDrawTool();
        PendingImage = image;
        _pendingCursor = CursorWorld;
        Cursor = Cursors.Cross;
        _canvas.Focus();
        _canvas.InvalidateVisual();
        InsertionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ativa o modo de posicionamento de um plano de bornes.</summary>
    public void BeginTerminalStripPlacement(OperCAD.Core.TerminalStrips.TerminalStrip strip)
    {
        CancelInsert();
        EndConductorTool();
        EndDrawTool();
        PendingTerminalStrip = strip;
        _pendingCursor = CursorWorld;
        Cursor = Cursors.Cross;
        _canvas.Focus();
        _canvas.InvalidateVisual();
        InsertionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancela qualquer posicionamento pendente (texto, imagem ou borne).</summary>
    public void CancelPendingPlacement()
    {
        var had = PendingText is not null || PendingImage is not null || PendingTerminalStrip is not null;
        PendingText = null;
        PendingImage = null;
        PendingTerminalStrip = null;
        if (had)
        {
            Cursor = Cursors.Arrow;
            _canvas.InvalidateVisual();
            InsertionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Âncora da "lupa": ponto da tela onde fica o CANTO SUPERIOR-ESQUERDO da folha. O zoom
    /// é ancorado aqui — a folha fica PARADA (não desliza na tela) e o zoom a magnífica em
    /// volta do canto dela, como uma lupa sobre uma folha fixa na mesa. Isso mantém a área de
    /// desenho numa região delimitada e previsível da tela (coordenadas estáveis).
    /// </summary>
    private Point2D SheetZoomAnchor()
    {
        if (_page is null)
            return new Point2D(ActualWidth / 2, ActualHeight / 2);
        // Canto superior-esquerdo da folha (mundo Y-up → MaxY é o topo).
        var fb = _page.Settings.FullBounds;
        return View.WorldToScreen(new Point2D(fb.MinX, fb.MaxY));
    }

    public void ZoomIn() => ZoomAt(CenterScreenAnchor(), 1.25);
    public void ZoomOut() => ZoomAt(CenterScreenAnchor(), 1 / 1.25);

    /// <summary>Âncora padrão para zoom: centro da tela (quando não há posição de mouse disponível).</summary>
    private Point2D CenterScreenAnchor() => new(ActualWidth / 2, ActualHeight / 2);

    public void ZoomAt(Point2D screenAnchor, double factor)
    {
        _viewUserAdjusted = true;
        View = View.ZoomAt(factor, screenAnchor);
        _canvas.InvalidateVisual();
        NotifyViewChanged();
    }

    /// <summary>Margem fixa (DIPs) entre a folha e o canvas no modo Fit.</summary>
    private const double SheetAnchorMargin = 10;

    /// <summary>
    /// Ajusta a escala e a posição para que a folha inteira caiba no canvas,
    /// centralizada e com o máximo de aproveitamento de espaço (zoom to fit).
    /// </summary>
    public void FitToPage()
    {
        if (_page is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        _viewUserAdjusted = false;
        var fb = _page.Settings.FullBounds;

        // Escala que faz a folha caber inteira no canvas com a margem
        double usableW = Math.Max(ActualWidth - SheetAnchorMargin * 2, 1);
        double usableH = Math.Max(ActualHeight - SheetAnchorMargin * 2, 1);
        var scale = Math.Clamp(Math.Min(usableW / fb.Width, usableH / fb.Height),
            ViewTransform.MinScale, ViewTransform.MaxScale);

        // Centraliza a folha no canvas
        var scaledW = fb.Width * scale;
        var scaledH = fb.Height * scale;
        var offsetX = (ActualWidth - scaledW) / 2.0 - fb.MinX * scale;
        var offsetY = (ActualHeight - scaledH) / 2.0 + fb.MaxY * scale;

        View = new ViewTransform(scale, offsetX, offsetY);
        _canvas.InvalidateVisual();
        NotifyViewChanged();
    }

    public void FitToContent()
    {
        if (_page is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool hasObjects = false;

        foreach (var obj in _page.Objects)
        {
            if (obj is PlacedElement el)
            {
                hasObjects = true;
                // Usa a caixa real do corpo (primitivas + texto + terminais) no mundo,
                // não a posição do hotspot — corrige elementos grandes cortados.
                var b = ElementWorldBounds(el);
                minX = Math.Min(minX, b.MinX);
                minY = Math.Min(minY, b.MinY);
                maxX = Math.Max(maxX, b.MaxX);
                maxY = Math.Max(maxY, b.MaxY);
            }
            else if (obj is TextItem t)
            {
                hasObjects = true;
                minX = Math.Min(minX, t.X);
                minY = Math.Min(minY, t.Y);
                maxX = Math.Max(maxX, t.X + 20);
                maxY = Math.Max(maxY, t.Y + 5);
            }
            else if (obj is Conductor c && c.Path.Count >= 2)
            {
                hasObjects = true;
                foreach (var p in c.Path)
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
            else if (obj is ShapeItem s)
            {
                hasObjects = true;
                foreach (var p in s.Points)
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
        }

        if (!hasObjects)
        {
            FitToPage();
            return;
        }

        var bounds = new Rect2D(minX, minY, maxX - minX, maxY - minY);
        _viewUserAdjusted = false;
        View = ViewTransform.Identity.FitToView(bounds, ActualWidth, ActualHeight, 40);
        _canvas.InvalidateVisual();
        NotifyViewChanged();
    }

    /// <summary>Redefine o zoom para 100% (1:1) centralizado na folha.</summary>
    public void ResetZoom()
    {
        if (_page is null)
            return;
        _viewUserAdjusted = true;
        var center = new Point2D(ActualWidth / 2, ActualHeight / 2);
        var world = View.ScreenToWorld(center);
        View = new ViewTransform(1.0, center.X - world.X, center.Y + world.Y);
        _canvas.InvalidateVisual();
        NotifyViewChanged();
    }

    public void ToggleGrid()
    {
        Grid.IsVisible = !Grid.IsVisible;
        _canvas.InvalidateVisual();
        NotifyViewChanged();
    }

    public void Refresh() => _canvas.InvalidateVisual();

    /// <summary>
    /// Redesenha a folha imediatamente. Sem isto, a chamada herdada de <see cref="UIElement"/>
    /// invalida apenas o Grid (que não desenha nada), e o conteúdo só reaparece na próxima
    /// passagem do mouse sobre o canvas. Agora qualquer <c>Sheet.InvalidateVisual()</c> repinta o canvas.
    /// </summary>
    public new void InvalidateVisual() => _canvas.InvalidateVisual();

    /// <summary>Posição do cursor no mundo (para a barra de status).</summary>
    public Point2D CursorWorld { get; private set; }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        // ⭐ DPI: e.Info.Width/Height = pixels físicos da superfície SkiaSharp;
        // ActualWidth/Height = DIPs do WPF. A razão dpiX/Y é o fator de escala entre eles.
        // canvas.Scale(dpiX) mapeia coordenadas DIP → pixels físicos (DIP × dpi = pixel).
        // IgnorarPixelScaling=true deveria dar e.Info em DIPs (fator 1.0), mas nem sempre
        // funciona — quando dpi > 1, aplicamos a escala para alinhar canvas com o mouse.
        var wDip = Math.Max(ActualWidth, 1.0);
        var hDip = Math.Max(ActualHeight, 1.0);
        var dpiX = e.Info.Width / wDip;
        var dpiY = e.Info.Height / hDip;
        _dpiCanvasScaleX = dpiX;
        _dpiCanvasScaleY = dpiY;
        ElementRenderer.UpdateDpi(dpiX, dpiY);
        if (Math.Abs(dpiX - 1.0) > 0.001 || Math.Abs(dpiY - 1.0) > 0.001)
            e.Surface.Canvas.Scale((float)dpiX, (float)dpiY);

        SceneRenderer.Render(e.Surface.Canvas, _page, View, Grid, wDip, hDip,
            ProjectPages, null, TerminalStrips, FolhaAutoNumFormula, GreyBackground);

        // Crosshair mundial (linhas tracejadas ao longo do cursor)
        if (ShowCrosshair && _page is not null)
        {
            var worldBounds = _page.Settings.FolhaTemplate is { } tpl
                ? FolhaTemplateRenderer.DrawingArea(tpl, _page.Settings)
                : _page.Settings.DrawingBounds;
            // O crosshair acompanha SEMPRE a posição física do cursor no mundo.
            // O fantasma do elemento é que se posiciona em relação ao cursor (via ComputePlacement),
            // garantindo que o centro visual do símbolo fique exatamente sob o crosshair.
            var crosshairWorld = CursorWorld;
            SceneRenderer.DrawCrosshair(e.Surface.Canvas, crosshairWorld, View, worldBounds, CrosshairMode);
        }

        // Hover highlight nos elementos (quando não há ferramenta ativa)
        if (ShowElementHover && _hoverElement is not null && PendingElement is null
            && !ConductorToolActive && DrawTool == InsertTool.None
            && PendingText is null && PendingImage is null && PendingTerminalStrip is null)
        {
            SceneRenderer.DrawElementHover(e.Surface.Canvas, _hoverElement, View);
        }

        // Hover highlight em texto independente (moldura azul translúcida)
        if (ShowElementHover && _hoverText is not null && PendingElement is null
            && !ConductorToolActive && DrawTool == InsertTool.None
            && PendingText is null && PendingImage is null && PendingTerminalStrip is null
            && !_hoverText.IsSelected)
        {
            TextItemRenderer.RenderSelectionOutline(e.Surface.Canvas, _hoverText, View);
        }

        // Highlight do terminal sob o cursor
        if (ShowTerminalHighlight && _hoverTerminal is { } hit && _page is not null)
        {
            var screenPos = View.WorldToScreen(hit.Pos);
            var isStart = _conductorStartRef is { } sr &&
                          sr.ElementId == hit.Element.Id &&
                          sr.TerminalIndex == hit.Index;
            SceneRenderer.DrawTerminalHighlight(e.Surface.Canvas, screenPos, View, isStart);
        }

        // Fantasma do elemento sendo inserido — VERMELHO, mostra EXATAMENTE onde o
        // elemento será colocado (hotspot clamped à área de desenho).
        // Usa ElementRenderer.Render (BuildMatrix) — MESMO caminho que o elemento final,
        // garantindo que fantasma e elemento colocado coincidam exatamente.
        if (PendingElement is not null && _page is not null)
        {
            var (ccx, ccy) = ComputePlacement(PendingElement, _ghostWorld);

            using var layer = new SKPaint { Color = new SKColor(255, 0, 0, 110) };
            e.Surface.Canvas.SaveLayer(layer);
            ElementRenderer.Render(e.Surface.Canvas, PendingElement, ccx, ccy, 0, View, null);
            // Tinge todo o conteúdo da camada de vermelho (silhueta vermelha do símbolo).
            using var tint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = SKColors.Red,
                BlendMode = SKBlendMode.SrcIn
            };
            e.Surface.Canvas.DrawRect(e.Surface.Canvas.LocalClipBounds, tint);
            e.Surface.Canvas.Restore();
        }

        // Fantasma de texto/imagem/borne aguardando posicionamento
        // (também limitado à área de desenho — mostra onde o item será colocado).
        if (_page is not null)
        {
            // Texto: fantasma na posição EXATA do cursor (sem grade). Demais: com snap de grade.
            var previewWorld = _pendingCursor;
            if (PendingText is null)
            {
                var (sxp, syp) = SnapToGrid(_pendingCursor);
                previewWorld = new Point2D(sxp, syp);
            }
            var (px, py) = ClampPointToDrawingArea(previewWorld);
            using var layer = new SKPaint { Color = new SKColor(0, 0, 0, 110) };
            e.Surface.Canvas.SaveLayer(layer);
            if (PendingText is not null)
            {
                var ghostText = new TextItem
                {
                    Text = PendingText.Text,
                    X = px,
                    Y = py,
                    FontSizePt = PendingText.FontSizePt,
                    FontFamily = PendingText.FontFamily,
                    Bold = PendingText.Bold,
                    Color = PendingText.Color
                };
                TextItemRenderer.Render(e.Surface.Canvas, ghostText, View);
            }
            if (PendingImage is not null)
            {
                var ghostImage = new ImageItem
                {
                    FilePath = PendingImage.FilePath,
                    X = px,
                    Y = py,
                    Width = PendingImage.Width,
                    Height = PendingImage.Height
                };
                ImageItemRenderer.Render(e.Surface.Canvas, ghostImage, View);
            }
            if (PendingTerminalStrip is not null)
            {
                var ghostItem = new TerminalStripItem
                {
                    StripUuid = PendingTerminalStrip.Uuid,
                    X = px,
                    Y = py
                };
                TerminalStripItemRenderer.Render(e.Surface.Canvas, ghostItem, PendingTerminalStrip, View);
            }
            e.Surface.Canvas.Restore();
        }

        // Marcador do ponto engatado (quadradinho verde): mostra EXATAMENTE onde o próximo
        // clique vai cair (vértice/meio/centro de objeto existente), mesmo antes de clicar.
        if (DrawTool != InsertTool.None && _drawSnapActive)
        {
            var sp = View.WorldToScreen(_drawCursor);
            using var snapPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(0, 150, 0, 230),
                StrokeWidth = 1.6f,
                IsAntialias = false
            };
            e.Surface.Canvas.DrawRect((float)sp.X - 3.5f, (float)sp.Y - 3.5f, 7, 7, snapPaint);
        }

        // Pré-visualização da ferramenta de desenho (formas)
        if (DrawTool != InsertTool.None && _drawPoints.Count > 0)
        {            var penW = (float)(DrawingProperties?.SelectedPenWidth ?? 0.29);
            using var dash = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 0, 0, 200),
                StrokeWidth = Math.Max(1.0f, penW * (float)View.Scale),
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0)
            };
            var cur = View.WorldToScreen(_drawCursor);

            if (DrawTool is InsertTool.Line)
            {
                // Desenha todos os segmentos já colocados
                for (var i = 0; i < _drawPoints.Count - 1; i++)
                {
                    var p1 = View.WorldToScreen(_drawPoints[i]);
                    var p2 = View.WorldToScreen(_drawPoints[i + 1]);
                    e.Surface.Canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, dash);
                }
                // Segmento fantasma do último ponto até o cursor. Usa _drawCursor (já com a
                // MESMA regra do clique: snap a objetos > crosshair exato) para que o fantasma
                // mostre exatamente onde a linha vai ficar.
                var last = View.WorldToScreen(_drawPoints[^1]);
                e.Surface.Canvas.DrawLine((float)last.X, (float)last.Y, (float)cur.X, (float)cur.Y, dash);
            }
            else if (DrawTool == InsertTool.Triangle)
            {
                if (_drawPoints.Count == 1)
                {
                    // Primeiro ponto colocado: linha até o cursor
                    var first = View.WorldToScreen(_drawPoints[0]);
                    e.Surface.Canvas.DrawLine((float)first.X, (float)first.Y, (float)cur.X, (float)cur.Y, dash);
                }
                else if (_drawPoints.Count == 2)
                {
                    // Dois pontos colocados: triângulo incompleto (2 vértices + cursor como 3º)
                    var sp0 = View.WorldToScreen(_drawPoints[0]);
                    var sp1 = View.WorldToScreen(_drawPoints[1]);
                    using var path = new SKPath();
                    path.MoveTo((float)sp0.X, (float)sp0.Y);
                    path.LineTo((float)sp1.X, (float)sp1.Y);
                    path.LineTo((float)cur.X, (float)cur.Y);
                    path.Close();
                    e.Surface.Canvas.DrawPath(path, dash);
                }
            }
            else if (DrawTool == InsertTool.Curve)
            {
                // Pré-visualiza a curva suave passando pelos pontos colocados + cursor
                var anchors = new List<Point2D>(_drawPoints) { _drawCursor };
                if (anchors.Count >= 2)
                {
                    var flat = OperCAD.Core.Documents.ShapeGeometry.FlattenCurve(anchors, false);
                    using var path = new SKPath();
                    for (var i = 0; i < flat.Count; i++)
                    {
                        var sp = View.WorldToScreen(flat[i]);
                        if (i == 0)
                            path.MoveTo((float)sp.X, (float)sp.Y);
                        else
                            path.LineTo((float)sp.X, (float)sp.Y);
                    }
                    e.Surface.Canvas.DrawPath(path, dash);
                }
            }
            else if (DrawTool is InsertTool.Arc or InsertTool.EllipseArc)
            {
                var center = _drawPoints[0];
                // Ponto de referência do raio: após o 2º clique é o ponto travado;
                // antes, é o cursor com snap (o mesmo usado no momento do clique).
                var refPt = _drawPoints.Count >= 2 ? _drawPoints[1] : _drawCursor;
                double rxW, ryW;
                if (DrawTool == InsertTool.Arc)
                {
                    var radiusW = center.DistanceTo(refPt);
                    rxW = ryW = radiusW;
                }
                else
                {
                    rxW = Math.Abs(refPt.X - center.X);
                    ryW = Math.Abs(refPt.Y - center.Y);
                }
                var sc = View.WorldToScreen(center);
                var srX = (float)Math.Max(rxW * View.Scale, 0.5f);
                var srY = (float)Math.Max(ryW * View.Scale, 0.5f);
                using var arcPath = new SKPath();
                arcPath.AddArc(
                    SKRect.Create((float)sc.X - srX, (float)sc.Y - srY, srX * 2, srY * 2),
                    (float)(-_arcStartAnglePreview), (float)(-_arcSweepPreview));
                e.Surface.Canvas.DrawPath(arcPath, dash);

                // Na fase em que o raio já está travado, mostra linhas-guia do centro
                // para o início/fim do arco (auxilia na leitura dos ângulos).
                if (_drawPoints.Count >= 2)
                {
                    var startRad = _arcStartAnglePreview * Math.PI / 180.0;
                    var endRad = (_arcStartAnglePreview + _arcSweepPreview) * Math.PI / 180.0;
                    var s = View.WorldToScreen(new Point2D(
                        center.X + rxW * Math.Cos(startRad), center.Y + ryW * Math.Sin(startRad)));
                    var e2 = View.WorldToScreen(new Point2D(
                        center.X + rxW * Math.Cos(endRad), center.Y + ryW * Math.Sin(endRad)));
                    using var guide = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = new SKColor(255, 0, 0, 120),
                        StrokeWidth = 1f,
                        IsAntialias = true
                    };
                    e.Surface.Canvas.DrawLine((float)sc.X, (float)sc.Y, (float)s.X, (float)s.Y, guide);
                    e.Surface.Canvas.DrawLine((float)sc.X, (float)sc.Y, (float)e2.X, (float)e2.Y, guide);
                }
                // Exibe os ângulos atuais
                using var textPaint = new SKPaint
                {
                    Color = new SKColor(255, 0, 0, 200),
                    TextSize = 12f,
                    IsAntialias = true
                };
                using var font = new SKFont(SKTypeface.Default, 12f);
                var label = $"Start: {_arcStartAnglePreview:F0}°  Sweep: {_arcSweepPreview:F0}°";
                e.Surface.Canvas.DrawText(label, (float)(sc.X + srX + 5), (float)sc.Y, font, textPaint);
            }
            else if (DrawTool == InsertTool.RoundedRectangle)
            {
                var first = View.WorldToScreen(_drawPoints[0]);
                var r = SKRect.Create(
                    (float)Math.Min(first.X, cur.X), (float)Math.Min(first.Y, cur.Y),
                    (float)Math.Abs(cur.X - first.X), (float)Math.Abs(cur.Y - first.Y));
                var rx = Math.Min(10f, r.Width * 0.15f);
                var ry = Math.Min(10f, r.Height * 0.15f);
                using var rrPath = new SKPath();
                rrPath.AddRoundRect(r, rx, ry);
                e.Surface.Canvas.DrawPath(rrPath, dash);
            }
            else if (DrawTool == InsertTool.Measure)
            {
                var first = View.WorldToScreen(_drawPoints[0]);
                e.Surface.Canvas.DrawLine((float)first.X, (float)first.Y, (float)cur.X, (float)cur.Y, dash);
                // Exibe a distância em tempo real
                var worldDist = _drawPoints[0].DistanceTo(CursorWorld);
                var label = $"{worldDist:F2} mm";
                using var textPaint = new SKPaint
                {
                    Color = new SKColor(255, 0, 0, 200),
                    TextSize = 12f,
                    IsAntialias = true
                };
                using var font = new SKFont(SKTypeface.Default, 12f);
                var midX = (float)((first.X + cur.X) / 2.0);
                var midY = (float)((first.Y + cur.Y) / 2.0);
                e.Surface.Canvas.DrawText(label, midX + 5, midY - 5, font, textPaint);
            }
            else
            {
                var first = View.WorldToScreen(_drawPoints[0]);
                var r = SKRect.Create(
                    (float)Math.Min(first.X, cur.X), (float)Math.Min(first.Y, cur.Y),
                    (float)Math.Abs(cur.X - first.X), (float)Math.Abs(cur.Y - first.Y));
                if (DrawTool == InsertTool.Ellipse)
                    e.Surface.Canvas.DrawOval(r, dash);
                else
                    e.Surface.Canvas.DrawRect(r, dash);
            }
        }

        // Rubber band do condutor em construção (rota livre multi-segmentos)
        if (_conductorPoints.Count > 0 && _page is not null)
        {
            var pts = ConductorRouter.GenerateFreeRoute(
                _conductorPoints[0],
                _conductorStartRef is { } sr ? _conductorStartOrient : null,
                _conductorPoints.Skip(1).ToList(),
                _conductorCursor,
                null,
                Grid.Spacing);
            using var layer = new SKPaint { Color = new SKColor(255, 0, 0, 110) };
            e.Surface.Canvas.SaveLayer(layer);
            using var path = new SKPath();
            var f = View.WorldToScreen(pts[0]);
            path.MoveTo((float)f.X, (float)f.Y);
            for (var i = 1; i < pts.Count; i++)
            {
                var p = View.WorldToScreen(pts[i]);
                path.LineTo((float)p.X, (float)p.Y);
            }
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Red,
                StrokeWidth = (float)Math.Max(0.29 * View.Scale, 1.0),
                IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round
            };
            e.Surface.Canvas.DrawPath(path, paint);
            e.Surface.Canvas.Restore();
        }

        // Destaque do terminal sob o cursor (snap) na ferramenta de condutor
        if (ConductorToolActive && _page is not null)
        {
            var hover = TerminalGeometry.HitTestTerminal(_page, CursorWorld, 6.0 / View.Scale);
            if (hover is not null)
            {
                var hp = View.WorldToScreen(hover.Value.Pos);
                using var ring = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(255, 0, 0),
                    StrokeWidth = 2.2f,
                    IsAntialias = true
                };
                e.Surface.Canvas.DrawCircle((float)hp.X, (float)hp.Y, 5.0f, ring);
                using var dot = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(255, 0, 0),
                    IsAntialias = true
                };
                e.Surface.Canvas.DrawCircle((float)hp.X, (float)hp.Y, 1.6f, dot);
            }
        }

        // Handles de edição de condutor (vértices e segmentos)
        if (_editingConductor is not null)
        {
            ConductorRenderer.RenderEditHandles(e.Surface.Canvas, _editingConductor, View,
                _editingHoverVertex, _editingHoverSegment);
        }

        // Exibe resultado da medição
        if (_measureResult >= 0)
        {
            var label = $"Distância: {_measureResult:F2} mm";
            using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 180), IsAntialias = true };
            using var textPaint = new SKPaint { Color = SKColors.White, TextSize = 14f, IsAntialias = true };
            var textWidth = textPaint.MeasureText(label);
            var cx = (float)ActualWidth / 2;
            var cy = (float)ActualHeight - 40;
            e.Surface.Canvas.DrawRoundRect(cx - textWidth / 2 - 10, cy - 12, textWidth + 20, 28, 6, 6, bgPaint);
            e.Surface.Canvas.DrawText(label, cx - textWidth / 2, cy + 4, textPaint);
        }

        // Caixa de seleção (rubber band) — retângulo azul translúcido, como o padrão CAD
        if (_isRubberBanding)
        {
            var p1 = View.WorldToScreen(_rubberBandStart);
            var p2 = View.WorldToScreen(_rubberBandCurrent);
            var rect = SKRect.Create((float)Math.Min(p1.X, p2.X), (float)Math.Min(p1.Y, p2.Y),
                                     (float)Math.Abs(p2.X - p1.X), (float)Math.Abs(p2.Y - p1.Y));
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(47, 155, 255, 60) };
            using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(47, 155, 255), StrokeWidth = 1f };
            e.Surface.Canvas.DrawRect(rect, fill);
            e.Surface.Canvas.DrawRect(rect, stroke);
        }

        // Guias de alinhamento (linhas tracejadas magenta durante arrasto)
        if (_alignmentGuides.Count > 0)
        {
            using var guidePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 0, 255, 180),
                StrokeWidth = 0.8f,
                IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0)
            };
            foreach (var (a, b) in _alignmentGuides)
            {
                var sa = View.WorldToScreen(a);
                var sb = View.WorldToScreen(b);
                e.Surface.Canvas.DrawLine((float)sa.X, (float)sa.Y, (float)sb.X, (float)sb.Y, guidePaint);
            }
        }


    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // ⭐ Zoom ancorado no CURSOR DO MOUSE: o ponto sob o cursor fica fixo e o
        // conteúdo é magnificado ao redor dele. A folha pode se deslocar livremente.

        // Durante criação de arco/arco de elipse: scroll ajusta o ângulo de varredura,
        // Shift+scroll ajusta o ângulo inicial (após o 1º clique e durante a fase de sweep).
        if ((DrawTool is InsertTool.Arc or InsertTool.EllipseArc) && (_drawPoints.Count is 1 or 2))
        {
            var delta = e.Delta > 0 ? 5.0 : -5.0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                _arcStartAnglePreview = (_arcStartAnglePreview + delta + 360) % 360;
            else
                _arcSweepPreview = (_arcSweepPreview + delta + 360) % 360;
            _canvas.InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Para formas selecionadas: scroll redimensiona
        // (mantém comportamento existente)

        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var mousePos = e.GetPosition(_canvas);
        ZoomAt(new Point2D(mousePos.X, mousePos.Y), factor);
        e.Handled = true;
    }

    // ── Win32: posição física do cursor e conversão de coordenadas ──────────
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref System.Drawing.Point lpPoint);

    /// <summary>
    /// Origem (0,0) do canvas em PIXELS FÍSICOS da tela do PC. Robusto: usa ClientToScreen
    /// (Win32 — px físicos do cliente da janela) + posição do canvas dentro da janela (DIPs)
    /// × DPI. NÃO confiamos em PointToScreen para conversão física: em WPF PerMonitorV2 ele
    /// retorna PIXELS FÍSICOS (verificado: (GetCursorPos − PointToScreen)/dpi == e.GetPosition),
    /// não DIPs — multiplicar por dpi de novo duplicava o fator (offset fantasma no debug).
    /// </summary>
    private (double X, double Y) CanvasOriginPhysical()
    {
        var win = System.Windows.Window.GetWindow(_canvas);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_canvas);
        double cx = 0, cy = 0;
        if (win is not null)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var p = new System.Drawing.Point(0, 0);
                ClientToScreen(hwnd, ref p);
                cx = p.X;
                cy = p.Y;
            }
            var inWin = _canvas.TransformToAncestor(win).Transform(new System.Windows.Point(0, 0));
            cx += inWin.X * dpi.DpiScaleX;
            cy += inWin.Y * dpi.DpiScaleY;
        }
        return (cx, cy);
    }

    /// <summary>
    /// [DEBUG] Converte uma posição do mundo para PIXELS FÍSICOS da tela do computador
    /// (mesmo espaço do GetCursorPos — não é coordenada do canvas/SkiaSharp).
    /// </summary>
    private (double X, double Y) WorldToPhysicalScreen(Point2D world)
    {
        var (ox, oy) = CanvasOriginPhysical();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_canvas);
        var elDip = View.WorldToScreen(world);
        double px = ox + elDip.X * dpi.DpiScaleX;
        double py = oy + elDip.Y * dpi.DpiScaleY;
        return (px, py);
    }

    /// <summary>
    /// [DEBUG] Loga na saída do Visual Studio uma linha com as coordenadas em PIXELS
    /// FÍSICOS da tela do PC (espaço do GetCursorPos — não é canvas/SkiaSharp).
    /// Dois comparativos:
    ///   • CORPO_tela vs mouse_tela → onde o DESENHO do motor está vs o cursor (Δcorpo≈0 = sob o cursor).
    ///   • input_drop vs mouse_canvas → o que o e.GetPosition retornou vs a posição real do mouse
    ///     convertida para o espaço do canvas (Δinput≈0 = a leitura do drop está correta).
    /// Se Δinput for grande, o bug está na LEITURA do drop; se só Δcorpo for grande, está no
    /// posicionamento/centro visual. Usa cultura invariante (ponto) para facilitar copiar/colar.
    /// </summary>
    private void DebugLogPlacement(string rotulo, ElementDefinition def, double dropScreenX, double dropScreenY,
        double worldX, double worldY, int mousePxX, int mousePxY)
    {
        static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        static string F2(double v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // (1) Posição do HOTSPOT em pixels físicos da tela.
        var (hotspotPxX, hotspotPxY) = WorldToPhysicalScreen(new Point2D(worldX, worldY));
        double dhx = hotspotPxX - mousePxX, dhy = hotspotPxY - mousePxY;
        double dh = Math.Sqrt(dhx * dhx + dhy * dhy);

        // (2) Leitura do drop: e.GetPosition vs mouse real (GetCursorPos) → espaço do canvas.
        var (oxP, oyP) = CanvasOriginPhysical();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_canvas);
        double mouseCanvasX = (mousePxX - oxP) / dpi.DpiScaleX;
        double mouseCanvasY = (mousePxY - oyP) / dpi.DpiScaleY;

        System.Diagnostics.Debug.WriteLine(
            $"[POSICIONAMENTO] {rotulo}: " +
            $"HOTSPOT_tela=({F(hotspotPxX)},{F(hotspotPxY)})px  mouse_tela=({mousePxX},{mousePxY})px  " +
            $"Δhotspot=({F(dhx)},{F(dhy)})px dist={F(dh)}px | " +
            $"input_drop=({F(dropScreenX)},{F(dropScreenY)})DIP  mouse_canvas=({F(mouseCanvasX)},{F(mouseCanvasY)})DIP  " +
            $"Δinput=({F(dropScreenX - mouseCanvasX)},{F(dropScreenY - mouseCanvasY)})DIP | " +
            $"mundo_hotspot=({F2(worldX)},{F2(worldY)})mm  " +
            $"dpi=({F(dpi.DpiScaleX)},{F(dpi.DpiScaleY)})  origin=({F(oxP)},{F(oyP)})px");
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Foca o canvas: garante que o Del/teclado funcionem após clicar na folha.
        _canvas.Focus();
        _mouseCanvasPos = e.GetPosition(_canvas);

        // ── Edição de condutor existente ────────────────────────────────
        if (e.ChangedButton == MouseButton.Left && _editingConductor is not null && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));

            // Clicar em um vértice → iniciar arraste
            var vi = HitTestConductorVertex(world, 6.0 / View.Scale);
            if (vi >= 0)
            {
                _editingVertexIndex = vi;
                _isDraggingVertex = true;
                _canvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // Clicar em um segmento → inserir novo vértice
            var si = HitTestConductorSegment(world, 3.0 / View.Scale);
            if (si >= 0)
            {
                InsertConductorVertex(si, world);
                e.Handled = true;
                return;
            }

            // Clique em área vazia → sair do modo de edição
            ExitConductorEditMode();
            e.Handled = true;
            return;
        }

        // Posicionamento pendente: texto independente
        if (e.ChangedButton == MouseButton.Left && PendingText is not null && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));
            // Texto posicionado LIVREMENTE (ignora a grade), apenas limitado à área de desenho.
            var clamped = ClampPointToDrawingArea(world);
            var item = new TextItem
            {
                Text = PendingText.Text,
                X = clamped.X,
                Y = clamped.Y,
                FontSizePt = PendingText.FontSizePt,
                FontFamily = PendingText.FontFamily,
                Bold = PendingText.Bold,
                Color = PendingText.Color
            };
            _page.Add(item);
            UndoStack?.Push(new AddObjectsCommand(_page, new[] { item }, "Adicionar texto"));
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            PendingText = null;
            Cursor = Cursors.Arrow;
            _canvas.InvalidateVisual();
            InsertionChanged?.Invoke(this, EventArgs.Empty);
            // Inicia edição inline imediatamente
            BeginInlineTextItemEdit(item);
            e.Handled = true;
            return;
        }

        // Posicionamento pendente: imagem
        if (e.ChangedButton == MouseButton.Left && PendingImage is not null && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));
            var (sx, sy) = SnapToGrid(world);
            var clamped = ClampPointToDrawingArea(new Point2D(sx, sy));
            var item = new ImageItem
            {
                FilePath = PendingImage.FilePath,
                X = clamped.X,
                Y = clamped.Y,
                Width = PendingImage.Width,
                Height = PendingImage.Height
            };
            _page.Add(item);
            UndoStack?.Push(new AddObjectsCommand(_page, new[] { item }, "Adicionar imagem"));
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            PendingImage = null;
            Cursor = Cursors.Arrow;
            _canvas.InvalidateVisual();
            InsertionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Posicionamento pendente: plano de bornes
        if (e.ChangedButton == MouseButton.Left && PendingTerminalStrip is not null && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));
            var (sx, sy) = SnapToGrid(world);
            AddTerminalStripItem(PendingTerminalStrip, ClampPointToDrawingArea(new Point2D(sx, sy)));
            PendingTerminalStrip = null;
            Cursor = Cursors.Arrow;
            _canvas.InvalidateVisual();
            InsertionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Ferramenta de desenho de formas (linha, retângulo, elipse, polilinha)
        if (e.ChangedButton == MouseButton.Left && DrawTool != InsertTool.None && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));
            if (e.ClickCount >= 2 && DrawTool is InsertTool.Line or InsertTool.Curve)
            {
                if (DrawTool == InsertTool.Curve)
                    FinishCurve();
                else
                    FinishPolygon();
            }
            else
            {
                HandleDrawToolClick(world);
            }
            e.Handled = true;
            return;
        }

        // Inserção de elemento da paleta (fluxo CADe-SIMU: o símbolo centraliza sob
        // o cursor e a ferramenta PERMANECE ATIVA para colocar cópias repetidas;
        // Esc ou clique com o botão direito libera a ferramenta).
        if (e.ChangedButton == MouseButton.Left && PendingElement is not null && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));
            // ⭐ Hotspot no cursor (sem VisualCenterOffset).
            var (px, py) = ComputePlacement(PendingElement, world);
            var placed = new PlacedElement(PendingElement) { X = px, Y = py };
            if (PendingElement.HasLabelField && string.IsNullOrEmpty(placed.Label))
                placed.Label = _page.NextLabel(ElementLabels.Prefix(PendingElement));
            _page.AddElement(placed);
            UndoStack?.Push(new AddObjectsCommand(_page, new[] { placed }, "Inserir elemento"));
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            _canvas.InvalidateVisual();

            // Log de debug no VS Output (hotspot vs mouse).
            GetCursorPos(out var mousePt);
            DebugLogPlacement("clique", PendingElement, _mouseCanvasPos.X, _mouseCanvasPos.Y, px, py, mousePt.X, mousePt.Y);

            e.Handled = true;
            return;
        }

        // Ferramenta de condutor (rota livre multi-segmentos)
        if (e.ChangedButton == MouseButton.Left && ConductorToolActive && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));
            // Usa o hover terminal para snap (já detectado no MouseMove)
            var hit = _hoverTerminal ?? TerminalGeometry.HitTestTerminal(_page, world, 6.0 / View.Scale);

            // Verifica snap em condutor existente (para T-connections)
            var conductorHit = FindNearestConductorPoint(world, 3.0 / View.Scale);

            // Duplo clique conclui a rota (com snap no terminal sob o cursor, se houver).
            if (e.ClickCount >= 2)
            {
                if (hit is not null && _conductorPoints.Count > 0)
                    SetEndTerminal(hit.Value);
                else if (conductorHit is { } ch && _conductorPoints.Count > 0)
                    _conductorPoints.Add(ch.Point);
                FinishConductor();
                e.Handled = true;
                return;
            }

            if (_conductorPoints.Count == 0)
            {
                // Ponto inicial: snap em terminal se houver (usa hover para melhor UX)
                var startHit = _hoverTerminal ?? hit;
                if (startHit is not null)
                {
                    _conductorStartRef = new ElementTerminalRef(startHit.Value.Element.Id, startHit.Value.Index);
                    _conductorStartOrient = TerminalGeometry.WorldOrientation(
                        startHit.Value.Element, startHit.Value.Element.Definition.Terminals[startHit.Value.Index]);
                    _conductorPoints.Add(startHit.Value.Pos);
                }
                else if (conductorHit is { } ch)
                {
                    // Snap no condutor existente (T-connection)
                    _conductorPoints.Add(ch.Point);
                }
                else
                {
                    var (sx, sy) = SnapToGrid(world);
                    _conductorPoints.Add(ClampPointToDrawingArea(new Point2D(sx, sy)));
                }
                _conductorCursor = _conductorPoints[^1];
                _canvas.InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Pontos intermediários / terminal de destino (snap).
            if (hit is not null)
            {
                var isSameAsStart = _conductorStartRef is { } sr &&
                                    sr.ElementId == hit.Value.Element.Id &&
                                    sr.TerminalIndex == hit.Value.Index;
                if (!isSameAsStart)
                {
                    SetEndTerminal(hit.Value);
                    _conductorPoints.Add(hit.Value.Pos);
                }
            }
            else if (conductorHit is { } ch)
            {
                // Snap no condutor existente (T-connection)
                _conductorPoints.Add(ch.Point);
            }
            else
            {
                var (sx, sy) = SnapToGrid(world);
                _conductorPoints.Add(ClampPointToDrawingArea(new Point2D(sx, sy)));
            }
            _conductorCursor = CursorWorld;
            _canvas.InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Seleção / mover / duplo-clique (clique esquerdo, sem ferramentas ativas)
        if (e.ChangedButton == MouseButton.Left && _page is not null)
        {
            var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));

            // As ALÇAS (vértice, redimensionar, rotacionar) só valem com UM objeto selecionado.
            // Com um CONJUNTO selecionado, arrastar tem de MOVER o conjunto: antes o primeiro
            // objeto da seleção capturava o arrasto e "esticava" um traço (a alça de vértice ficava
            // sobre o traço inteiro, porque os traços importados são curtos).
            var selectionCount = _page.Objects.Count(o => o.IsSelected);

            // Alças de redimensionar/rotacionar de um texto já selecionado
            var selectedText = selectionCount == 1
                ? _page.Objects.OfType<TextItem>().FirstOrDefault(t => t.IsSelected)
                : null;
            if (selectedText is not null && e.ClickCount == 1)
            {
                var screen = new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y);
                var kind = HitTestTextHandle(selectedText, screen);
                if (kind == TextHandleKind.Resize)
                {
                    _resizingText = selectedText;
                    _resizeBefore = TextItemSnapshot.Of(selectedText);
                    _resizeStartFont = selectedText.FontSizePt;
                    _resizeAnchor = new Point2D(selectedText.X, selectedText.Y);
                    _resizeStartDist = Math.Max(1.0, _resizeAnchor.DistanceTo(world));
                    _isTextResizing = true;
                    _canvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                if (kind == TextHandleKind.Rotate)
                {
                    _rotatingText = selectedText;
                    _rotateBefore = TextItemSnapshot.Of(selectedText);
                    _rotateCenter = new Point2D(selectedText.X, selectedText.Y);
                    _isTextRotating = true;
                    _canvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            var selectedShape = selectionCount == 1
                ? _page.Objects.OfType<ShapeItem>().FirstOrDefault(s => s.IsSelected)
                : null;
            if (selectedShape is not null && e.ClickCount == 1)
            {
                // Hit-test para handles de ângulo de arco (antes dos vértices)
                if (selectedShape.Kind is ShapeKind.Arc or ShapeKind.EllipseArc)
                {
                    var arcHandle = HitTestArcAngleHandles(selectedShape, world, 8.0 / View.Scale);
                    if (arcHandle >= 0)
                    {
                        _editingArcAngle = selectedShape;
                        _arcAngleEditBefore = ShapeItemSnapshot.Of(selectedShape);
                        _arcAngleEditCenter = selectedShape.Points[0];
                        _arcAngleEditRadius = _arcAngleEditCenter.DistanceTo(selectedShape.Points[1]);
                        _arcAngleEditInitialAngle = Math.Atan2(world.Y - _arcAngleEditCenter.Y, world.X - _arcAngleEditCenter.X) * 180.0 / Math.PI;
                        if (arcHandle == 0)
                            _isDraggingArcStartAngle = true;
                        else
                            _isDraggingArcEndAngle = true;
                        _canvas.CaptureMouse();
                        e.Handled = true;
                        return;
                    }
                }

                // Hit-test para handles de resize/rotação de forma
                var shapeHandle = HitTestShapeHandle(selectedShape, View.WorldToScreen(world));
                if (shapeHandle == ShapeHandleKind.Resize)
                {
                    _resizingShape = selectedShape;
                    _resizeShapeBefore = ShapeItemSnapshot.Of(selectedShape);
                    _resizeShapeOrigBounds = selectedShape.Bounds;
                    _resizeShapeAnchor = new Point2D(
                        (_resizeShapeOrigBounds.MinX + _resizeShapeOrigBounds.MaxX) / 2,
                        (_resizeShapeOrigBounds.MinY + _resizeShapeOrigBounds.MaxY) / 2);
                    // Determina qual handle foi clicado
                    var b = ShapeRenderer.ComputeBounds(selectedShape);
                    var tl = View.WorldToScreen(new Point2D(b.MinX, b.MinY));
                    var br = View.WorldToScreen(new Point2D(b.MaxX, b.MaxY));
                    var screenPos = View.WorldToScreen(world);
                    var handles = new[]
                    {
                        new Point2D(tl.X, tl.Y), new Point2D((tl.X + br.X) / 2, tl.Y),
                        new Point2D(br.X, tl.Y), new Point2D(br.X, (tl.Y + br.Y) / 2),
                        new Point2D(br.X, br.Y), new Point2D((tl.X + br.X) / 2, br.Y),
                        new Point2D(tl.X, br.Y), new Point2D(tl.X, (tl.Y + br.Y) / 2)
                    };
                    _resizeShapeHandleIndex = 0;
                    var minDist = double.MaxValue;
                    for (var i = 0; i < handles.Length; i++)
                    {
                        var d = screenPos.DistanceTo(handles[i]);
                        if (d < minDist) { minDist = d; _resizeShapeHandleIndex = i; }
                    }
                    _isShapeResizing = true;
                    _canvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                if (shapeHandle == ShapeHandleKind.Rotate)
                {
                    _rotatingShape = selectedShape;
                    _rotateShapeBefore = ShapeItemSnapshot.Of(selectedShape);
                    var rb = selectedShape.Bounds;
                    _rotateShapeCenter = new Point2D(
                        (rb.MinX + rb.MaxX) / 2, (rb.MinY + rb.MaxY) / 2);
                    _isShapeRotating = true;
                    _canvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }

                var vertexIndex = HitTestShapeVertex(selectedShape, world, 6.0 / View.Scale);
                if (vertexIndex >= 0)
                {
                    _editingShape = selectedShape;
                    _editingShapeVertexIndex = vertexIndex;
                    _editingShapeOriginalPoints = selectedShape.SnapshotPoints();
                    _isDraggingShapeVertex = true;
                    _canvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            if (e.ClickCount >= 2)
            {
                // Duplo-clique em um TextItem → edição inline
                var hitText = HitTestTextItem(world);
                if (hitText is not null)
                {
                    SelectOnly(hitText);
                    BeginInlineTextItemEdit(hitText);
                    e.Handled = true;
                    return;
                }

                var el = HitTestElement(world);
                if (el is not null)
                {
                    SelectOnly(el);

                    // Verifica se clicou em um texto dinâmico → edição inline
                    var textIdx = HitTestDynamicText(el, world);
                    if (textIdx >= 0)
                    {
                        BeginInlineTextEdit(el, textIdx);
                        e.Handled = true;
                        return;
                    }

                    ElementActivated?.Invoke(el);
                    e.Handled = true;
                    return;
                }

                // Duplo-clique no legenda → "Propriedades da folha" (como o padrão CAD)
                var tbBounds = DrawingFrameRenderer.TitleBlockWorldBounds(_page);
                if (tbBounds is not null && tbBounds.Value.Contains(world))
                {
                    if (DrawingFrameRenderer.HitTestTitleBlockField(_page, world) is { } field)
                    {
                        TitleBlockFieldActivated?.Invoke(_page, field);
                        e.Handled = true;
                        return;
                    }
                    TitleBlockActivated?.Invoke(_page);
                    e.Handled = true;
                    return;
                }

                // Duplo-clique em um condutor → propriedades do condutor
                if (HitTestConductor(world, 2.5) is { } hitCond)
                {
                    ConductorActivated?.Invoke(hitCond);
                    e.Handled = true;
                    return;
                }

                // Duplo-clique em um borne → abre o editor do borne (como o padrão CAD)
                foreach (var obj in _page.Objects)
                {
                    if (obj is TerminalStripItem stripItem && TerminalStrips is not null)
                    {
                        var strip = TerminalStrips.FirstOrDefault(s => s.Uuid == stripItem.StripUuid);
                        if (strip is null)
                            continue;
                        if (stripItem.Bounds(strip.PhysicalTerminals.Count).Contains(world))
                        {
                            TerminalStripActivated?.Invoke(strip);
                            e.Handled = true;
                            return;
                        }
                    }
                }
            }

            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // Hit-test para ShapeItem — geométrico (sobre traço/preenchimento), com topo e
            // ciclagem de formas sobrepostas ao clicar repetidamente no mesmo ponto.
            var shapeCandidates = HitTestShapeCandidates(world);
            if (shapeCandidates.Count > 0)
            {
                ShapeItem? hitShape;
                if (!shift && shapeCandidates.Count > 1 && _cycleEnabled
                    && world.DistanceTo(_cycleWorld) <= 3.0 / View.Scale
                    && _cycleShape is { } prevCycle
                    && IndexOf(shapeCandidates, prevCycle.Id) is { } prevIdx)
                {
                    // clique repetido no mesmo ponto → cicla para o próximo objeto abaixo.
                    hitShape = shapeCandidates[(prevIdx + 1) % shapeCandidates.Count];
                }
                else
                {
                    hitShape = ResolveShapeHit(world, shapeCandidates);
                }

                if (hitShape is not null)
                {
                    // Clique = 1 primitiva (o traço clicado); Ctrl = objeto do PDF inteiro.
                    if (!ShouldPreserveMultiSelectionOnClick(hitShape, shift))
                        SelectObjectOrGroup(hitShape, shift);
                    if (hitShape.IsSelected)
                    {
                        _cycleShape = hitShape;
                        _cycleWorld = world;
                        _cycleEnabled = true;
                        if (IsMouseRotationEnabled)
                        {
                            // rotação com o mouse: gira a SELEÇÃO inteira de forma rígida
                            CaptureAllRotationOrigins(SelectionRotationCenter(), world);
                        }
                        else
                        {
                            _dragStartWorld = world;
                            CaptureAllDragOrigins();
                            _isDragging = true;
                        }
                        _canvas.CaptureMouse();
                    }
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                }
            }

            // Hit-test para TextItem independente
            var hitTextItem = HitTestTextItem(world);
            if (hitTextItem is not null)
            {
                // Texto pertence a um símbolo importado? Seleciona o grupo inteiro.
                if (!ShouldPreserveMultiSelectionOnClick(hitTextItem, shift))
                    SelectObjectOrGroup(hitTextItem, shift);
                if (hitTextItem.IsSelected)
                {
                    if (IsMouseRotationEnabled)
                    {
                        // rotação com o mouse: gira a SELEÇÃO inteira de forma rígida
                        CaptureAllRotationOrigins(SelectionRotationCenter(), world);
                    }
                    else
                    {
                        _dragStartWorld = world;
                        CaptureAllDragOrigins();
                        _isDragging = true;
                    }
                    _canvas.CaptureMouse();
                }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            var hitEl = HitTestElement(world);
            if (hitEl is not null)
            {
                if (!ShouldPreserveMultiSelectionOnClick(hitEl, shift))
                {
                    if (shift)
                        ToggleSelection(hitEl);
                    else
                        SelectOnly(hitEl);
                }
                if (hitEl.IsSelected)
                {
                    if (IsMouseRotationEnabled)
                    {
                        // rotação com o mouse: gira a SELEÇÃO inteira de forma rígida
                        CaptureAllRotationOrigins(SelectionRotationCenter(), world);
                    }
                    else
                    {
                        _dragStartWorld = world;
                        CaptureAllDragOrigins();
                        _isDragging = true;
                    }
                    _canvas.CaptureMouse();
                }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            if (HitTestConductor(world, 2.5) is { } hitC)
            {
                if (!ShouldPreserveMultiSelectionOnClick(hitC, shift))
                {
                    if (shift)
                        ToggleSelection(hitC);
                    else
                        SelectOnly(hitC);
                }
                if (hitC.IsSelected)
                {
                    if (IsMouseRotationEnabled)
                    {
                        // rotação com o mouse: gira a SELEÇÃO inteira de forma rígida
                        CaptureAllRotationOrigins(SelectionRotationCenter(), world);
                    }
                    else
                    {
                        _dragStartWorld = world;
                        CaptureAllDragOrigins();
                        _isDragging = true;
                    }
                    _canvas.CaptureMouse();
                }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            // Clique no vazio: inicia a caixa de seleção (rubber band), como o padrão CAD
            if (!shift)
                ClearSelection();
            _isRubberBanding = true;
            _rubberBandShift = shift;
            _rubberBandStart = world;
            _rubberBandCurrent = world;
            _canvas.CaptureMouse();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Pan (botão do meio do mouse — padrão CAD)
        if (e.ChangedButton == MouseButton.Middle)
        {
            _isPanning = true;
            _panStartScreen = new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y);
            _canvas.CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        // Botão direito: menu de contexto / cancelar ferramenta
        if (e.ChangedButton == MouseButton.Right)
        {
            // Botão direito com objeto sob o cursor: o MENU DE CONTEXTO tem prioridade,
            // mesmo com ferramentas pendentes (fluxo CADe-SIMU — a ferramenta de inserção
            // permanece ativa após colocar, mas o clique direito no elemento abre o menu).
            if (e.ChangedButton == MouseButton.Right && _page is not null)
            {
                var world = View.ScreenToWorld(new Point2D(e.GetPosition(_canvas).X, e.GetPosition(_canvas).Y));
                var obj = HitTestElement(world) ?? HitTestConductor(world, 2.5) as DocumentObject
                          ?? HitTestTextItem(world) as DocumentObject
                          ?? HitTestShape(world) as DocumentObject;

                // Sem objeto exatamente sob o cursor, mas COM seleção: o menu é sobre a SELEÇÃO.
                // (Antes era preciso acertar o traço fino para o menu abrir — era essa a
                // dificuldade de acessar "Rotacionar" no clique direito.)
                obj ??= _page.Objects.FirstOrDefault(o => o.IsSelected);

                if (obj is not null)
                {
                    CancelInsert();
                    ClearConductorDrawing();
                    // Preserva a seleção MÚLTIPLA: só seleciona se o objeto ainda não estava
                    // selecionado (senão girar/excluir pelo menu afetaria apenas um traço).
                    if (!obj.IsSelected)
                        SelectObjectOrGroup(obj, additive: false);
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    ObjectContextRequested?.Invoke(obj);
                    e.Handled = true;
                    return;
                }
                // Espaço vazio: abre as Propriedades da folha direto no clique direito,
                // desde que não haja ferramenta pendente (com ferramenta ativa, libera a ferramenta).
                var hasPendingTool = PendingElement is not null || PendingText is not null ||
                                     PendingImage is not null || PendingTerminalStrip is not null ||
                                     DrawTool != InsertTool.None || _conductorPoints.Count > 0 ||
                                     ConductorToolActive;
                if (!hasPendingTool)
                {
                    EmptySpacePropertiesRequested?.Invoke();
                    e.Handled = true;
                    return;
                }
            }
            if (e.ChangedButton == MouseButton.Right && PendingElement is not null)
            {
                CancelInsert();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && PendingText is not null)
            {
                PendingText = null;
                Cursor = Cursors.Arrow;
                _canvas.InvalidateVisual();
                InsertionChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && PendingImage is not null)
            {
                PendingImage = null;
                Cursor = Cursors.Arrow;
                _canvas.InvalidateVisual();
                InsertionChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && PendingTerminalStrip is not null)
            {
                PendingTerminalStrip = null;
                Cursor = Cursors.Arrow;
                _canvas.InvalidateVisual();
                InsertionChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && DrawTool is InsertTool.Line && _drawPoints.Count > 0)
            {
                _drawPoints.RemoveAt(_drawPoints.Count - 1);
                _canvas.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && DrawTool is InsertTool.Triangle or InsertTool.Curve && _drawPoints.Count > 0)
            {
                _drawPoints.RemoveAt(_drawPoints.Count - 1);
                _canvas.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && DrawTool is InsertTool.Arc or InsertTool.EllipseArc && _drawPoints.Count > 0)
            {
                _drawPoints.RemoveAt(_drawPoints.Count - 1);
                _arcSweepPreview = 90;
                _arcStartAnglePreview = 0;
                _canvas.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && DrawTool != InsertTool.None)
            {
                EndDrawTool();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && _conductorPoints.Count > 0)
            {
                // Cancela a rota em construção (mantém a ferramenta ativa).
                ClearConductorDrawing();
                _canvas.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && ConductorToolActive)
            {
                EndConductorTool();
                e.Handled = true;
                return;
            }
            // ⭐ Folha fixa — sem pano com botão do meio/direito.
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        _mouseCanvasPos = pos;
        CursorWorld = View.ScreenToWorld(new Point2D(pos.X, pos.Y));
        CursorMoved?.Invoke(this, EventArgs.Empty);

        // ── Pan (botão do meio) ─────────────────────────────────────
        if (_isPanning)
        {
            var dx = pos.X - _panStartScreen.X;
            var dy = pos.Y - _panStartScreen.Y;
            View = View.Pan(dx, dy);
            _panStartScreen = new Point2D(pos.X, pos.Y);
            _viewUserAdjusted = true;
            NotifyViewChanged();
            _canvas.InvalidateVisual();
            return;
        }

        // ⭐ Redesenha a cada movimento para o crosshair acompanhar o mouse (estilo AutoCAD),
        // mesmo sem botão pressionado nem elemento sob o cursor.
        _canvas.InvalidateVisual();

        // ── Redimensionar texto (alça de canto) ──────────────────────────
        if (_isTextResizing && _resizingText is not null)
        {
            var dist = Math.Max(1.0, _resizeAnchor.DistanceTo(CursorWorld));
            var newFont = _resizeStartFont * (dist / _resizeStartDist);
            _resizingText.FontSizePt = Math.Clamp(newFont, 1, 200);
            _canvas.InvalidateVisual();
            return;
        }

        // ── Rotacionar texto (alça de rotação) ───────────────────────────
        if (_isTextRotating && _rotatingText is not null)
        {
            var angle = AngleDeg(CursorWorld, _rotateCenter);
            _rotatingText.Rotation = (angle + 360) % 360;
            _canvas.InvalidateVisual();
            return;
        }

        // ── Redimensionar forma (alça de canto/borda) ──────────────────
        if (_isShapeResizing && _resizingShape is not null && _resizeShapeBefore is not null)
        {
            var origB = _resizeShapeOrigBounds;
            var handleIdx = _resizeShapeHandleIndex;
            // Mapa do handle para as bordas que ele controla (0=esq-cima, 1=centro-cima, etc.)
            var scaleX = 1.0; var scaleY = 1.0;
            var screenWorld = CursorWorld;

            // Calcula a escala relativa ao centro da bounding box original
            if (handleIdx is 0 or 6 or 7) // borda esquerda
                scaleX = (screenWorld.X - origB.CenterX) / ((origB.MinX - origB.CenterX) == 0 ? 1 : (origB.MinX - origB.CenterX));
            else if (handleIdx is 2 or 3 or 4) // borda direita
                scaleX = (screenWorld.X - origB.CenterX) / ((origB.MaxX - origB.CenterX) == 0 ? 1 : (origB.MaxX - origB.CenterX));

            if (handleIdx is 0 or 1 or 2) // borda superior
                scaleY = (screenWorld.Y - origB.CenterY) / ((origB.MaxY - origB.CenterY) == 0 ? 1 : (origB.MaxY - origB.CenterY));
            else if (handleIdx is 4 or 5 or 6) // borda inferior
                scaleY = (screenWorld.Y - origB.CenterY) / ((origB.MinY - origB.CenterY) == 0 ? 1 : (origB.MinY - origB.CenterY));

            // Limita escala para não inverter nem "voltar a crescer" quando a alça
            // cruza o centro da forma (crosshair em x/y negativos): engatilha no
            // tamanho mínimo em vez de espelhar e inflar a forma.
            scaleX = scaleX <= 0 ? MinResizeScale : Math.Max(MinResizeScale, Math.Abs(scaleX));
            scaleY = scaleY <= 0 ? MinResizeScale : Math.Max(MinResizeScale, Math.Abs(scaleY));

            // Shift = escala proporcional
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var avg = (Math.Abs(scaleX) + Math.Abs(scaleY)) / 2.0;
                scaleX = Math.Sign(scaleX) * avg;
                scaleY = Math.Sign(scaleY) * avg;
            }

            // Aplica escala aos pontos da forma (em relação ao centro original)
            var beforePts = _resizeShapeBefore.Points;
            _resizingShape.Points.Clear();
            Point2D ScalePt(Point2D pt)
            {
                var dx = pt.X - origB.CenterX;
                var dy = pt.Y - origB.CenterY;
                return new Point2D(origB.CenterX + dx * scaleX, origB.CenterY + dy * scaleY);
            }
            foreach (var pt in beforePts)
                _resizingShape.Points.Add(ScalePt(pt));
            // Curvas Bézier: escala os pontos de controle junto (sem isso, o traço deforma).
            if (_resizeShapeBefore.ControlPoints is { } bcp && _resizingShape.ControlPoints is { } cp)
            {
                for (var i = 0; i < cp.Count && i < bcp.Count; i++)
                    cp[i] = (ScalePt(bcp[i].C1), ScalePt(bcp[i].C2));
            }
            // Para arcos, ajusta também o raio
            _canvas.InvalidateVisual();
            return;
        }

        // ── Rotacionar forma (alça de rotação) ───────────────────────────
        if (_isShapeRotating && _rotatingShape is not null && _rotateShapeBefore is not null)
        {
            var angle = AngleDeg(CursorWorld, _rotateShapeCenter);
            var deltaAngle = angle - AngleDeg(new Point2D(_rotateShapeCenter.X + 1, _rotateShapeCenter.Y), _rotateShapeCenter);
            // Transformação rígida: restaura o estado original (âncoras + controles Bézier) e gira.
            _rotateShapeBefore.Apply(_rotatingShape);
            _rotatingShape.RotateAround(_rotateShapeCenter, deltaAngle);
            _canvas.InvalidateVisual();
            return;
        }

        // ── Edição de ângulo de arco ──────────────────────────────
        if ((_isDraggingArcStartAngle || _isDraggingArcEndAngle) && _editingArcAngle is not null)
        {
            var currentAngle = Math.Atan2(CursorWorld.Y - _arcAngleEditCenter.Y, CursorWorld.X - _arcAngleEditCenter.X) * 180.0 / Math.PI;
            var deltaAngle = currentAngle - _arcAngleEditInitialAngle;
            if (_isDraggingArcStartAngle)
            {
                var newStart = _arcAngleEditBefore!.StartAngle + deltaAngle;
                _editingArcAngle.StartAngle = (newStart % 360 + 360) % 360;
            }
            else
            {
                var newEnd = (_arcAngleEditBefore!.StartAngle + _arcAngleEditBefore.SweepAngle) + deltaAngle;
                var endAngle = (newEnd % 360 + 360) % 360;
                var startAngle = _editingArcAngle.StartAngle;
                var sweep = endAngle - startAngle;
                if (sweep < 0) sweep += 360;
                _editingArcAngle.SweepAngle = sweep;
            }
            _canvas.InvalidateVisual();
            return;
        }

        // ── Edição de condutor: arraste de vértice ──────────────────────
        if (_isDraggingShapeVertex && _editingShape is not null)
        {
            var (sx, sy) = SnapToGrid(CursorWorld);
            _editingShape.Points[_editingShapeVertexIndex] = new Point2D(sx, sy);
            _canvas.InvalidateVisual();
            return;
        }
        if (_isDraggingVertex && _editingConductor is not null)
        {
            var (sx, sy) = SnapToGrid(CursorWorld);
            _editingConductor.Path[_editingVertexIndex] = new Point2D(sx, sy);
            _canvas.InvalidateVisual();
            return;
        }

        // ── Edição de condutor: hover em vértices/segmentos ─────────────
        if (_editingConductor is not null && !_isDraggingVertex)
        {
            var newHoverVertex = HitTestConductorVertex(CursorWorld, 6.0 / View.Scale);
            var newHoverSegment = HitTestConductorSegment(CursorWorld, 3.0 / View.Scale);
            if (newHoverVertex != _editingHoverVertex || newHoverSegment != _editingHoverSegment)
            {
                _editingHoverVertex = newHoverVertex;
                _editingHoverSegment = newHoverSegment;
                _canvas.InvalidateVisual();
            }
            return;
        }

        // Hover detection: terminal e elemento sob o cursor
        if (_page is not null && PendingElement is null && DrawTool == InsertTool.None
            && PendingText is null && PendingImage is null && PendingTerminalStrip is null)
        {
            var newHoverTerminal = TerminalGeometry.HitTestTerminal(_page, CursorWorld, 6.0 / View.Scale);
            var newHoverElement = HitTestElementForHover(CursorWorld);
            var newHoverText = HitTestTextItem(CursorWorld);

            if (!newHoverTerminal.HasValue && _hoverTerminal.HasValue)
            {
                _hoverTerminal = null;
                _canvas.InvalidateVisual();
            }
            else if (newHoverTerminal.HasValue && (!_hoverTerminal.HasValue ||
                newHoverTerminal.Value.Element.Id != _hoverTerminal.Value.Element.Id ||
                newHoverTerminal.Value.Index != _hoverTerminal.Value.Index))
            {
                _hoverTerminal = newHoverTerminal;
                _canvas.InvalidateVisual();
            }

            if (newHoverElement is not null && (_hoverElement is null || _hoverElement.Id != newHoverElement.Id))
            {
                _hoverElement = newHoverElement;
                _canvas.InvalidateVisual();
            }
            else if (newHoverElement is null && _hoverElement is not null)
            {
                _hoverElement = null;
                _canvas.InvalidateVisual();
            }

            if (!ReferenceEquals(newHoverText, _hoverText))
            {
                _hoverText = newHoverText;
                _canvas.InvalidateVisual();
            }
        }
        else
        {
            if (_hoverTerminal is not null || _hoverElement is not null || _hoverText is not null)
            {
                _hoverTerminal = null;
                _hoverElement = null;
                _hoverText = null;
                _canvas.InvalidateVisual();
            }
        }

        if (PendingElement is not null)
        {
            _ghostWorld = CursorWorld;
            _canvas.InvalidateVisual();
        }

        if (PendingText is not null || PendingImage is not null || PendingTerminalStrip is not null)
        {
            _pendingCursor = CursorWorld;
            _canvas.InvalidateVisual();
        }

        if (DrawTool != InsertTool.None)
        {
            // Mesma regra do clique: snap a objetos primeiro; a LINHA não passa pela grade
            // (é puxada do centro do crosshair); restrição angular só sem snap.
            var (point, snapped) = DrawPoint(CursorWorld, 5.0 / View.Scale, angularToLast: true);
            _drawCursor = point;
            _drawSnapActive = snapped;
            _canvas.InvalidateVisual();
        }

        if (_conductorPoints.Count > 0)
        {
            _conductorCursor = CursorWorld;
            _canvas.InvalidateVisual();
        }

        if (_isDragging && _page is not null)
        {
            var dx = CursorWorld.X - _dragStartWorld.X;
            var dy = CursorWorld.Y - _dragStartWorld.Y;
            // Detecta alinhamento com outros objetos (snap de alinhamento) usando o primeiro shape
            var adjDx = 0.0;
            var adjDy = 0.0;
            if (_dragShapeOrigins.Count > 0)
            {
                var firstShape = _dragShapeOrigins[0].Shape;
                var movingBounds = firstShape.Bounds;
                var proposedPos = new Point2D(movingBounds.CenterX, movingBounds.CenterY);
                var aligned = DetectAlignmentGuides(movingBounds, proposedPos, AlignmentSnapTolerance / View.Scale);
                if (_alignmentGuides.Count > 0)
                {
                    adjDx = aligned.X - movingBounds.CenterX;
                    adjDy = aligned.Y - movingBounds.CenterY;
                }
            }

            // Delta bruto do grupo — ÚNICO para todos os tipos de objeto (preserva distâncias).
            var gdx = dx + adjDx;
            var gdy = dy + adjDy;

            // Snap de grade aplicado UMA VEZ ao grupo (objeto de referência), nunca por objeto,
            // para não destravar posições relativas entre desenho, textos e elementos.
            (double X, double Y)? refPt = null;
            if (_dragShapeOrigins.Count > 0 && _dragShapeOrigins[0].Before.Points.Count > 0)
                refPt = (_dragShapeOrigins[0].Before.Points[0].X, _dragShapeOrigins[0].Before.Points[0].Y);
            else if (_dragOrigins.Count > 0)
                refPt = (_dragOrigins[0].Ox, _dragOrigins[0].Oy);
            else if (_dragTextOrigins.Count > 0)
                refPt = (_dragTextOrigins[0].Ox, _dragTextOrigins[0].Oy);
            else if (_dragConductorOrigins.Count > 0 && _dragConductorOrigins[0].OrigPath.Count > 0)
                refPt = (_dragConductorOrigins[0].OrigPath[0].X, _dragConductorOrigins[0].OrigPath[0].Y);

            var tx = gdx;
            var ty = gdy;
            // Texto puro move-se LIVREMENTE (ignora a grade). O snap só é aplicado quando há
            // pelo menos um objeto "grudável" (desenho/elemento/condutor) na seleção.
            var hasSnappable = _dragShapeOrigins.Count > 0 || _dragOrigins.Count > 0
                               || _dragConductorOrigins.Count > 0;
            if (refPt is { } rp && hasSnappable)
            {
                var (sx, sy) = SnapToGrid(new Point2D(rp.X + gdx, rp.Y + gdy));
                tx = sx - rp.X;
                ty = sy - rp.Y;
            }

            // Translada TODOS os objetos pelo mesmo delta (transformação rígida).
            foreach (var (el, ox, oy) in _dragOrigins)
            {
                el.X = ox + tx;
                el.Y = oy + ty;
                // ⭐ Nunca deixa o elemento sair da área de desenho durante o arraste.
                ClampPlacedElement(el);
            }
            foreach (var (txt, ox, oy) in _dragTextOrigins)
            {
                txt.X = ox + tx;
                txt.Y = oy + ty;
            }
            // Restaura o estado original (incluindo pontos de controle Bézier) e translada:
            // sem isso, curvas importadas de PDF deformam ao serem arrastadas.
            foreach (var (shape, before) in _dragShapeOrigins)
            {
                before.Apply(shape);
                shape.Translate(tx, ty);
            }
            // Restaura e traduz condutores preservados (desenhados à mão) selecionados no grupo
            foreach (var (cond, origPath) in _dragConductorOrigins)
            {
                cond.Path.Clear();
                foreach (var op in origPath)
                    cond.Path.Add(new Point2D(op.X + tx, op.Y + ty));
            }
            _page.RecomputeConductors();
            _canvas.InvalidateVisual();
        }

        if (_isRotating && _page is not null)
        {
            var delta = AngleDeg(CursorWorld, _rotCenter) - _rotStartAngleDeg;
            // Elementos: posição orbita o centro E conteúdo gira (rotação rígida do grupo)
            foreach (var (el, before, ox, oy) in _rotOrigins)
            {
                var p = RotatePointAround(new Point2D(ox, oy), _rotCenter, delta);
                el.X = p.X;
                el.Y = p.Y;
                el.Rotation = NormalizeAngle(before + delta);
            }
            // Textos: posição orbita + rotação
            foreach (var (txt, before) in _rotTextOrigins)
            {
                var p = RotatePointAround(new Point2D(before.X, before.Y), _rotCenter, delta);
                txt.X = p.X;
                txt.Y = p.Y;
                txt.Rotation = NormalizeAngle(before.Rotation + delta);
            }
            // Formas: rotação rígida da geometria (âncoras + controles Bézier + ângulo de arco)
            foreach (var (shape, before) in _rotShapeOrigins)
            {
                before.Apply(shape);
                shape.RotateAround(_rotCenter, delta);
            }
            // Condutores preservados (desenhados à mão)
            foreach (var (cond, origPath) in _rotConductorOrigins)
            {
                cond.Path.Clear();
                foreach (var op in origPath)
                    cond.Path.Add(RotatePointAround(op, _rotCenter, delta));
            }
            _page.RecomputeConductors();
            _canvas.InvalidateVisual();
        }

        if (_isRubberBanding)
        {
            _rubberBandCurrent = CursorWorld;
            _canvas.InvalidateVisual();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // ── Redimensionar/rotacionar texto (alças) ──────────────────────
        if (e.ChangedButton == MouseButton.Left && (_isTextResizing || _isTextRotating))
        {
            if (_resizingText is not null &&
                Math.Abs(_resizeStartFont - _resizingText.FontSizePt) > 0.01)
            {
                UndoStack?.Push(new ChangeTextItemPropertiesCommand(
                    _resizingText, _resizeBefore, TextItemSnapshot.Of(_resizingText)));
                EditStateChanged?.Invoke(this, EventArgs.Empty);
            }
            if (_rotatingText is not null &&
                Math.Abs(NormalizeAngle(_rotateBefore.Rotation) - NormalizeAngle(_rotatingText.Rotation)) > 0.5)
            {
                UndoStack?.Push(new ChangeTextItemPropertiesCommand(
                    _rotatingText, _rotateBefore, TextItemSnapshot.Of(_rotatingText)));
                EditStateChanged?.Invoke(this, EventArgs.Empty);
            }
            _resizingText = null;
            _rotatingText = null;
            _isTextResizing = false;
            _isTextRotating = false;
            _canvas.ReleaseMouseCapture();
            _canvas.InvalidateVisual();
            return;
        }

        // ── Redimensionar/rotacionar forma (alças) ──────────────────────
        if (e.ChangedButton == MouseButton.Left && (_isShapeResizing || _isShapeRotating))
        {
            if (_resizingShape is not null && _resizeShapeBefore is not null)
            {
                var after = ShapeItemSnapshot.Of(_resizingShape);
                var beforePts = _resizeShapeBefore.Points;
                var afterPts = after.Points;
                if (beforePts.Count == afterPts.Count && !beforePts.SequenceEqual(afterPts))
                {
                    UndoStack?.Push(new ChangeShapePropertiesCommand(
                        _resizingShape, _resizeShapeBefore, after));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            if (_rotatingShape is not null && _rotateShapeBefore is not null)
            {
                var after = ShapeItemSnapshot.Of(_rotatingShape);
                var beforePts = _rotateShapeBefore.Points;
                var afterPts = after.Points;
                if (beforePts.Count == afterPts.Count && !beforePts.SequenceEqual(afterPts))
                {
                    UndoStack?.Push(new ChangeShapePropertiesCommand(
                        _rotatingShape, _rotateShapeBefore, after));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            _resizingShape = null;
            _resizeShapeBefore = null;
            _isShapeResizing = false;
            _rotatingShape = null;
            _rotateShapeBefore = null;
            _isShapeRotating = false;
            _canvas.ReleaseMouseCapture();
            _canvas.InvalidateVisual();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && (_isDraggingArcStartAngle || _isDraggingArcEndAngle))
        {
            if (_editingArcAngle is not null && _arcAngleEditBefore is not null)
            {
                var after = ShapeItemSnapshot.Of(_editingArcAngle);
                if (_arcAngleEditBefore.StartAngle != after.StartAngle || _arcAngleEditBefore.SweepAngle != after.SweepAngle)
                {
                    UndoStack?.Push(new ChangeShapePropertiesCommand(
                        _editingArcAngle, _arcAngleEditBefore, after));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            _editingArcAngle = null;
            _arcAngleEditBefore = null;
            _isDraggingArcStartAngle = false;
            _isDraggingArcEndAngle = false;
            _canvas.ReleaseMouseCapture();
            _canvas.InvalidateVisual();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _isDraggingShapeVertex)
        {
            if (_editingShape is not null && _editingShapeOriginalPoints is not null &&
                !_editingShape.Points.SequenceEqual(_editingShapeOriginalPoints))
            {
                UndoStack?.Push(new ChangeShapePropertiesCommand(
                    _editingShape,
                    ShapeItemSnapshot.Of(_editingShape, _editingShapeOriginalPoints),
                    ShapeItemSnapshot.Of(_editingShape)));
                EditStateChanged?.Invoke(this, EventArgs.Empty);
            }
            _editingShape = null;
            _editingShapeOriginalPoints = null;
            _editingShapeVertexIndex = -1;
            _isDraggingShapeVertex = false;
            _canvas.ReleaseMouseCapture();
            _canvas.InvalidateVisual();
            return;
        }

        // ── Edição de condutor: finalizar arraste de vértice ────────────
        if (e.ChangedButton == MouseButton.Left && _isDraggingVertex && _editingConductor is not null)
        {
            _isDraggingVertex = false;
            _editingVertexIndex = -1;
            _canvas.ReleaseMouseCapture();
            _canvas.InvalidateVisual();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _isDragging)
        {
            _isDragging = false;
            var moved = _dragOrigins
                .Select(entry => (entry.El, entry.Ox, entry.Oy, entry.El.X, entry.El.Y))
                .Where(m => Math.Abs(m.Item2 - m.Item4) > 1e-9 || Math.Abs(m.Item3 - m.Item5) > 1e-9)
                .ToList();
            if (moved.Count > 0 || _dragConductorOrigins.Count > 0)
            {
                var dx = CursorWorld.X - _dragStartWorld.X;
                var dy = CursorWorld.Y - _dragStartWorld.Y;
                var conductorMoves = _dragConductorOrigins
                    .Where(c => c.OrigPath.Count > 0)
                    .Select(c => {
                        var newPath = c.OrigPath
                            .Select(p => new Point2D(p.X + dx, p.Y + dy))
                            .ToList();
                        return (c.Cond, OldPath: c.OrigPath, NewPath: newPath);
                    })
                    .ToList();
                UndoStack?.Push(new MoveElementsCommand(_page!, moved, "Mover elementos", conductorMoves));
                EditStateChanged?.Invoke(this, EventArgs.Empty);
            }
            // Undo para TextItems arrastados
            if (_dragTextOrigins.Count > 0)
            {
                var textMoved = _dragTextOrigins
                    .Select(entry => (entry.Txt, entry.Ox, entry.Oy, entry.Txt.X, entry.Txt.Y))
                    .Where(m => Math.Abs(m.Item2 - m.Item4) > 1e-9 || Math.Abs(m.Item3 - m.Item5) > 1e-9)
                    .ToList();
                if (textMoved.Count > 0)
                {
                    var cmds = textMoved.Select(m =>
                        (IUndoableCommand)new ChangeTextItemPositionCommand(m.Txt, m.Item2, m.Item3, m.Item4, m.Item5)).ToList();
                    UndoStack?.Push(new CompositeCommand("Mover textos", cmds));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            // Undo para Shapes arrastados
            if (_dragShapeOrigins.Count > 0)
            {
                var shapeMoved = _dragShapeOrigins
                    .Where(s => !s.Shape.Points.SequenceEqual(s.Before.Points))
                    .ToList();
                if (shapeMoved.Count > 0)
                {
                    var cmds = shapeMoved.Select(s =>
                        (IUndoableCommand)new ChangeShapePropertiesCommand(s.Shape,
                            s.Before,
                            ShapeItemSnapshot.Of(s.Shape))).ToList();
                    UndoStack?.Push(new CompositeCommand("Mover formas", cmds));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            _dragOrigins.Clear();
            _dragTextOrigins.Clear();
            _dragShapeOrigins.Clear();
            _dragConductorOrigins.Clear();
            _alignmentGuides.Clear();
            _canvas.ReleaseMouseCapture();
        }
        if (e.ChangedButton == MouseButton.Left && _isRotating)
        {
            _isRotating = false;
            var cmds = new List<IUndoableCommand>();

            // Elementos: posição orbitada + rotação do conteúdo
            var elPos = _rotOrigins
                .Select(r => (r.El, r.Ox, r.Oy, r.El.X, r.El.Y))
                .Where(m => Math.Abs(m.Item2 - m.Item4) > 1e-9 || Math.Abs(m.Item3 - m.Item5) > 1e-9)
                .Select(m => m)
                .ToList();
            var elRot = _rotOrigins
                .Select(r => (r.El, r.Before, r.El.Rotation))
                .Where(c => Math.Abs(NormalizeAngle(c.Before) - NormalizeAngle(c.El.Rotation)) > 0.5)
                .ToList();
            if (elPos.Count > 0)
                cmds.Add(new MoveElementsCommand(_page!, elPos, "Girar seleção"));
            if (elRot.Count > 0)
                cmds.Add(new ChangeElementRotationCommand(_page!, elRot));

            // Textos: posição + rotação
            var textCmds = _rotTextOrigins
                .Where(t => Math.Abs(t.Before.X - t.Txt.X) > 1e-9 || Math.Abs(t.Before.Y - t.Txt.Y) > 1e-9
                            || Math.Abs(NormalizeAngle(t.Before.Rotation) - NormalizeAngle(t.Txt.Rotation)) > 0.5)
                .Select(t => (IUndoableCommand)new ChangeTextItemPropertiesCommand(t.Txt, t.Before, TextItemSnapshot.Of(t.Txt)))
                .ToList();
            cmds.AddRange(textCmds);

            // Formas: rotação rígida da geometria
            var shapeCmds = _rotShapeOrigins
                .Where(s => !s.Shape.Points.SequenceEqual(s.Before.Points))
                .Select(s => (IUndoableCommand)new ChangeShapePropertiesCommand(s.Shape, s.Before, ShapeItemSnapshot.Of(s.Shape)))
                .ToList();
            cmds.AddRange(shapeCmds);

            // Condutores preservados (desenhados à mão)
            var condMoves = _rotConductorOrigins
                .Where(c => c.OrigPath.Count > 0)
                .Select(c => (c.Cond, c.OrigPath, newPath: c.Cond.Path.ToList()))
                .ToList();
            if (condMoves.Count > 0)
                cmds.Add(new MoveElementsCommand(_page!,
                    Array.Empty<(PlacedElement, double, double, double, double)>(), "Girar seleção", condMoves));

            if (cmds.Count > 0)
            {
                UndoStack?.Push(new CompositeCommand("Girar seleção", cmds));
                EditStateChanged?.Invoke(this, EventArgs.Empty);
            }
            _rotOrigins.Clear();
            _rotShapeOrigins.Clear();
            _rotTextOrigins.Clear();
            _rotConductorOrigins.Clear();
            _canvas.ReleaseMouseCapture();
        }
        if (e.ChangedButton == MouseButton.Left && _isRubberBanding)
        {
            _isRubberBanding = false;
            _canvas.ReleaseMouseCapture();
            FinalizeRubberBand();
        }
        if (e.ChangedButton == MouseButton.Middle && _isPanning)
        {
            _isPanning = false;
            _canvas.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
        if (e.ChangedButton == MouseButton.Right)
        {
            _canvas.ReleaseMouseCapture();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // ── Edição de condutor: teclas específicas ──────────────────────
        if (_editingConductor is not null)
        {
            if (e.Key == Key.Escape)
            {
                ExitConductorEditMode();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete && _editingHoverVertex >= 0)
            {
                RemoveConductorVertex(_editingHoverVertex);
                _editingHoverVertex = -1;
                _canvas.InvalidateVisual();
                e.Handled = true;
                return;
            }
            // Outras teclas ignoradas no modo de edição de condutor
            return;
        }

        // ── Atalhos globais ────────────────────────────────────────────
        if (e.Key == Key.N)
        {
            ToggleSnap();
            e.Handled = true;
            return;
        }
        // A: ferramenta Arco
        if (e.Key == Key.A && DrawTool == InsertTool.None && PendingElement is null
            && PendingText is null && !ConductorToolActive)
        {
            BeginDrawTool(InsertTool.Arc);
            DrawToolChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        // M: ferramenta Régua
        if (e.Key == Key.M && DrawTool == InsertTool.None && PendingElement is null
            && PendingText is null && !ConductorToolActive)
        {
            BeginDrawTool(InsertTool.Measure);
            DrawToolChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // ── Enter com condutor selecionado → entrar no modo de edição ───
        if (e.Key == Key.Enter && _page is not null)
        {
            var cond = SelectedConductor;
            if (cond is not null)
            {
                EnterConductorEditMode(cond);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F2)
        {
            // F2: abre diálogo de cor para o condutor selecionado
            var cond = SelectedConductor;
            if (cond is not null)
            {
                System.Windows.Media.Color? initialColor = null;
                try
                {
                    var brush = new System.Windows.Media.BrushConverter().ConvertFromString(cond.Color) as System.Windows.Media.Brush;
                    if (brush is System.Windows.Media.SolidColorBrush solid)
                        initialColor = solid.Color;
                }
                catch { }

                var dialog = new Views.ColorPickerWindow(initialColor)
                {
                    Owner = Window.GetWindow(this)
                };
                if (dialog.ShowDialog() == true)
                {
                    var c = dialog.SelectedColor;
                    cond.Color = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                    _canvas.InvalidateVisual();
                }
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            DeleteSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back && _page is not null)
        {
            var selectedShape = _page.Objects.OfType<ShapeItem>().FirstOrDefault(s => s.IsSelected);
            if (selectedShape is { Points.Count: > 2 })
            {
                var before = ShapeItemSnapshot.Of(selectedShape);
                selectedShape.Points.RemoveAt(selectedShape.Points.Count - 1);
                var after = ShapeItemSnapshot.Of(selectedShape);
                UndoStack?.Push(new ChangeShapePropertiesCommand(selectedShape, before, after));
                _canvas.InvalidateVisual();
                EditStateChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Space)
        {
            RotateSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && DrawTool is InsertTool.Line or InsertTool.Curve)
        {
            if (DrawTool == InsertTool.Curve)
                FinishCurve();
            else
                FinishPolygon();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (DrawTool is InsertTool.Arc or InsertTool.EllipseArc) && _drawPoints.Count == 2)
        {
            FinishAngleArc();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && ConductorToolActive && _conductorPoints.Count >= 2)
        {
            FinishConductor();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape)
        {
            // ── Setas: mover / redimensionar ───────────────────────
            if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            {
                var (dx, dy) = e.Key switch
                {
                    // Mundo CAD é Y para cima; tela é Y para baixo (WorldToScreen inverte Y).
                    Key.Up    => (0.0,  1.0),
                    Key.Down  => (0.0, -1.0),
                    Key.Left  => (-1.0, 0.0),
                    Key.Right => ( 1.0, 0.0),
                    _ => (0.0, 0.0)
                };
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    ResizeSelectedShapes(dx, dy);
                else
                    MoveSelectionByArrow(dx, dy);
                e.Handled = true;
            }
            return;
        }
        if (PendingElement is not null)
        {
            CancelInsert();
            e.Handled = true;
        }
        else if (PendingText is not null || PendingImage is not null || PendingTerminalStrip is not null)
        {
            CancelPendingPlacement();
            e.Handled = true;
        }
        else if (DrawTool != InsertTool.None)
        {
            _measureResult = -1;
            EndDrawTool();
            e.Handled = true;
        }
        else if (_conductorPoints.Count > 0)
        {
            ClearConductorDrawing();
            _canvas.InvalidateVisual();
            e.Handled = true;
        }
        else if (ConductorToolActive)
        {
            EndConductorTool();
            e.Handled = true;
        }
    }

    /// <summary>Exclui os objetos selecionados (e condutores ligados a elementos excluídos).</summary>
    public void DeleteSelection()
    {
        if (_page is null)
            return;
        var removed = new List<DocumentObject>();
        foreach (var obj in _page.Objects.ToList())
        {
            if (obj.IsSelected && !IsLocked(obj))
            {
                _page.RemoveObject(obj);
                removed.Add(obj);
            }
        }
        if (removed.Count > 0)
        {
            UndoStack?.Push(new RemoveObjectsCommand(_page, removed, "Excluir seleção"));
            EditStateChanged?.Invoke(this, EventArgs.Empty);
        }
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool SelectSelectedImportGroup()
    {
        if (_page is null)
            return false;

        var selected = _page.Objects.FirstOrDefault(obj => obj.IsSelected);
        var groupId = ImportedGroupId(selected);
        if (groupId is null)
            return false;

        ClearSelection();
        foreach (var obj in _page.Objects)
        {
            if (ImportedGroupId(obj) == groupId)
                obj.IsSelected = true;
        }
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool ToggleSelectedImportGroupLock()
    {
        if (_page is null)
            return false;

        var selected = _page.Objects.FirstOrDefault(obj => obj.IsSelected);
        var groupId = ImportedGroupId(selected);
        if (groupId is null)
            return false;

        var members = _page.Objects.Where(obj => ImportedGroupId(obj) == groupId).ToList();
        var lockGroup = members.Any(obj => !IsLocked(obj));
        foreach (var obj in members)
            SetLocked(obj, lockGroup);
        if (lockGroup)
            ClearSelection();
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Seleciona o objeto — ou o GRUPO inteiro a que pertence (símbolo importado / conjunto colado).
    /// <list type="bullet">
    /// <item><b>Clique</b>: o grupo inteiro (ou o objeto, quando não há grupo).</item>
    /// <item><b>Ctrl+clique</b>: SOMENTE esta primitiva — escapa do grupo, permitindo apagar/
    /// editar um rótulo de dentro de um conjunto colado.</item>
    /// <item><b>Shift+clique</b>: soma/remove da seleção (com Ctrl: uma primitiva por vez).</item>
    /// <item><b>Alt+clique</b>: o OBJETO DO PDF inteiro (todos os traços do mesmo path do arquivo).</item>
    /// </list>
    /// </summary>
    private void SelectObjectOrGroup(DocumentObject obj, bool additive)
    {
        if (_page is null)
            return;

        var modifiers = Keyboard.Modifiers;
        var isolate = modifiers.HasFlag(ModifierKeys.Control);
        var wholePdfObject = modifiers.HasFlag(ModifierKeys.Alt) && !isolate;

        // Alt: todos os traços que vieram do MESMO path do PDF (identidade do arquivo).
        if (wholePdfObject &&
            obj is ShapeItem { PdfObjectIndex: { } objectIndex } &&
            _page.Objects.OfType<ShapeItem>().Count(s => s.PdfObjectIndex == objectIndex) > 1)
        {
            var pdfMembers = _page.Objects.OfType<ShapeItem>()
                .Where(s => s.PdfObjectIndex == objectIndex).ToList();
            SelectMembers(pdfMembers, obj, additive);
            return;
        }

        // Ctrl isola: age só sobre o objeto clicado. Sem Ctrl, age sobre o grupo (se houver).
        if (isolate || ObjectGroups.GroupIdOf(obj) is not { } gid)
        {
            SelectMembers(new List<DocumentObject> { obj }, obj, additive);
            return;
        }

        SelectMembers(ObjectGroups.Members(_page, gid), obj, additive);
    }

    /// <summary>Aplica a seleção a um conjunto de membros (com ou sem Ctrl/Shift).</summary>
    private void SelectMembers(IReadOnlyList<DocumentObject> members, DocumentObject clicked, bool additive)
    {
        if (members.Count == 0)
            return;

        if (additive)
        {
            var target = !clicked.IsSelected;
            foreach (var member in members)
                member.IsSelected = target;
            return;
        }

        if (!members.All(m => m.IsSelected))
        {
            ClearSelection();
            foreach (var member in members)
                member.IsSelected = true;
        }
    }

    /// <summary>
    /// SEPARA os objetos selecionados nos agrupamentos de contornos que o PDF traz num único path
    /// (ex.: TODOS os rótulos de uma linha num só objeto). Serve para desenho JÁ importado e salvo:
    /// depois de separar, cada rótulo pode ser selecionado, editado, movido ou excluído sozinho.
    /// Contornos aninhados (o furo de uma letra) permanecem juntos.
    /// </summary>
    public int SplitSelectedShapes()
    {
        if (_page is null)
            return 0;

        var targets = _page.Objects.OfType<ShapeItem>()
            .Where(s => s.IsSelected && s.ContourStarts is { Count: > 1 })
            .ToList();
        if (targets.Count == 0)
            return 0;

        var gap = new PdfImportOptions().LabelClusterGapMm;
        var removed = new List<DocumentObject>();
        var added = new List<DocumentObject>();

        foreach (var shape in targets)
        {
            var parts = ShapeContourSplitter.SplitByProximity(shape, gap);
            if (parts.Count < 2)
                continue;   // nada a separar (a própria forma foi devolvida)

            _page.RemoveObject(shape);
            removed.Add(shape);

            foreach (var part in parts)
            {
                // As partes deixam o grupo: o propósito do comando é justamente poder tratar
                // cada rótulo separadamente.
                ObjectGroups.SetGroupId(part, null);
                _page.Add(part);
                added.Add(part);
            }
        }

        if (added.Count == 0)
            return 0;

        ClearSelection();
        foreach (var obj in added)
            obj.IsSelected = true;

        UndoStack?.Push(new CompositeCommand("Separar rótulos", new IUndoableCommand[]
        {
            new RemoveObjectsCommand(_page, removed, "Separar rótulos"),
            new AddObjectsCommand(_page, added, "Separar rótulos")
        }));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return added.Count;
    }

    /// <summary>
    /// Agrupa a seleção atual como uma unidade editável (símbolo). Útil para corrigir o
    /// agrupamento automático da importação de PDF.
    /// </summary>
    public bool GroupSelection()
    {
        if (_page is null)
            return false;

        var selected = _page.Objects
            .Where(o => o.IsSelected && ObjectGroups.IsGroupable(o))
            .ToList();
        if (selected.Count < 2)
            return false;

        var groupId = Guid.NewGuid();
        UndoStack?.Push(new SetObjectGroupCommand(selected, groupId, "Agrupar seleção"));
        foreach (var obj in selected)
            ObjectGroups.SetGroupId(obj, groupId);

        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Desagrupa a seleção (remove o vínculo de grupo dos objetos selecionados).</summary>
    public bool UngroupSelection()
    {
        if (_page is null)
            return false;

        var groupIds = _page.Objects
            .Where(o => o.IsSelected)
            .Select(ObjectGroups.GroupIdOf)
            .Where(g => g is not null)
            .Distinct()
            .ToList();
        if (groupIds.Count == 0)
            return false;

        var affected = groupIds
            .SelectMany(g => ObjectGroups.Members(_page, g!.Value))
            .Distinct()
            .ToList();

        UndoStack?.Push(new SetObjectGroupCommand(affected, null, "Desagrupar seleção"));
        foreach (var obj in affected)
            ObjectGroups.SetGroupId(obj, null);

        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static Guid? ImportedGroupId(DocumentObject? obj) => ObjectGroups.GroupIdOf(obj);

    private static bool IsLocked(DocumentObject obj) => ObjectGroups.IsLockedOf(obj);

    private static void SetLocked(DocumentObject obj, bool value) => ObjectGroups.SetLockedOf(obj, value);

    /// <summary>
    /// MUDANÇA DE ESCALA da seleção (Ctrl++ / Ctrl+-), como o SCALE do CAD: multiplica a
    /// geometria E O TRAÇADO das primitivas por <paramref name="factor"/> — o desenho inteiro
    /// cresce/encolhe proporcional, sem esticar pontos nem mexer na grade.
    /// Alcance: elementos .opcad, formas/linhas (desenhadas à mão e importadas de PDF) e textos.
    /// Cada objeto escala NO LUGAR (não vagueia pela folha): símbolo em volta do próprio hotspot,
    /// texto em volta da própria âncora e UMA forma em volta do próprio centro. Com VÁRIAS formas
    /// selecionadas, elas escalam em bloco em torno do centro comum — escalar cada letra de um
    /// rótulo vetorizado em volta do próprio centro amassaria o desenho.
    /// </summary>
    /// <returns>Mensagem para a barra de status, ou null se não há nada selecionado para escalar.</returns>
    public string? ScaleSelection(double factor)
    {
        if (_page is null || factor <= 0 || Math.Abs(factor - 1.0) < 1e-9)
            return null;

        var elements = _page.Objects.OfType<PlacedElement>().Where(el => el.IsSelected).ToList();
        var shapes = _page.Objects.OfType<ShapeItem>().Where(s => s.IsSelected && !s.IsLocked).ToList();
        var texts = _page.Objects.OfType<TextItem>().Where(t => t.IsSelected && !t.IsLocked).ToList();
        if (elements.Count == 0 && shapes.Count == 0 && texts.Count == 0)
            return null;

        var changedElements = new List<(PlacedElement Element, double Before, double After)>();
        foreach (var el in elements)
        {
            var before = el.Scale;
            if (el.ScaleBy(factor))
                changedElements.Add((el, before, el.Scale));
        }

        var pivot = ShapeScalePivot(shapes);
        var changedShapes = new List<(ShapeItem Shape, ShapeItemSnapshot Before, ShapeItemSnapshot After)>();
        foreach (var s in shapes)
        {
            var before = ShapeItemSnapshot.Of(s);
            if (!s.ScaleAround(pivot, factor))
                continue;
            changedShapes.Add((s, before, ShapeItemSnapshot.Of(s)));
        }

        var changedTexts = new List<(TextItem Text, TextItemSnapshot Before, TextItemSnapshot After)>();
        foreach (var t in texts)
        {
            var before = TextItemSnapshot.Of(t);
            if (!t.ScaleFont(factor))
                continue;
            changedTexts.Add((t, before, TextItemSnapshot.Of(t)));
        }

        if (changedElements.Count + changedShapes.Count + changedTexts.Count == 0)
            return "Escala no limite: não há mais como aumentar/diminuir a seleção.";

        UndoStack?.Push(new ScaleObjectsCommand(_page, $"Escala {factor:0.###}×",
            changedElements, changedShapes, changedTexts));
        _page.RecomputeConductors();
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();

        var parts = new List<string>();
        if (changedElements.Count > 0)
            parts.Add(changedElements.Count == 1
                ? $"símbolo agora {changedElements[0].After:0.###}×"
                : $"{changedElements.Count} símbolos");
        if (changedShapes.Count > 0)
            parts.Add($"{changedShapes.Count} traço(s)");
        if (changedTexts.Count > 0)
            parts.Add($"{changedTexts.Count} texto(s)");
        return $"Escala {factor:0.###}× aplicada: {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// Centro de escala das formas selecionadas: uma forma escala em volta do próprio centro;
    /// várias escalam em bloco em torno do centro comum (senão cada letra de um rótulo
    /// vetorizado cresceria em cima das outras).
    /// </summary>
    private static Point2D ShapeScalePivot(IReadOnlyList<ShapeItem> shapes)
    {
        if (shapes.Count == 0)
            return new Point2D(0, 0);
        if (shapes.Count == 1)
        {
            var b = shapes[0].Bounds;
            return new Point2D(b.CenterX, b.CenterY);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in shapes)
        {
            foreach (var p in s.Points)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
        }
        if (minX > maxX || minY > maxY)
            return new Point2D(0, 0);
        return new Point2D((minX + maxX) / 2.0, (minY + maxY) / 2.0);
    }

    /// <summary>
    /// Gira a seleção pelo ângulo informado (padrão 90°) em torno do centro da seleção,
    /// incluindo formas e textos — antes só os elementos eram girados, o que impedia girar
    /// um símbolo importado de PDF (composto de formas).
    /// </summary>
    public void RotateSelection(double angleDegrees = 90)
    {
        if (_page is null)
            return;
        if (!_page.Objects.Any(o => o.IsSelected))
            return;
        RotateSelectionFree(angleDegrees);
    }

    /// <summary>
    /// Define a rotação ABSOLUTA da seleção para um dos ângulos fixos (0/90/180/270°),
    /// em torno do próprio centro de cada elemento.
    /// </summary>
    public void RotateSelectionTo(double angleDegrees)
    {
        if (_page is null)
            return;
        var target = NormalizeAngle(angleDegrees);
        var selected = _page.Objects.OfType<PlacedElement>().Where(el => el.IsSelected).ToList();
        if (selected.Count == 0)
            return;
        var changes = selected
            .Select(el => (el, el.Rotation, target))
            .ToList();
        foreach (var el in selected)
            el.Rotation = target;
        _page.RecomputeConductors();
        UndoStack?.Push(new ChangeElementRotationCommand(_page, changes));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>Ângulo (graus) do ponto p em relação ao centro, no mundo (y para cima).</summary>
    private static double AngleDeg(Point2D p, Point2D center)
        => Math.Atan2(p.Y - center.Y, p.X - center.X) * 180.0 / Math.PI;

    /// <summary>Normaliza um ângulo para [0, 360).</summary>
    private static double NormalizeAngle(double angle)
        => (angle % 360.0 + 360.0) % 360.0;

    /// <summary>Espelha horizontalmente (inverte X) os objetos selecionados em torno do centro da seleção.</summary>
    public void MirrorSelectionHorizontal()
    {
        if (_page is null) return;
        var selected = _page.Objects.Where(o => o.IsSelected).ToList();
        if (selected.Count == 0) return;

        var bounds = ComputeSelectionBounds(selected);
        var cx = bounds.CenterX;
        var before = CaptureObjectPositions();

        foreach (var obj in selected)
        {
            switch (obj)
            {
                case PlacedElement el:
                    el.X = 2 * cx - el.X;
                    el.Rotation = NormalizeAngle(180 - el.Rotation);
                    break;
                case TextItem t:
                    t.X = 2 * cx - t.X;
                    t.Rotation = NormalizeAngle(180 - t.Rotation);
                    break;
                case ShapeItem s:
                    s.Mirror(cx, horizontal: true);
                    break;
                case Conductor c:
                    for (var i = 0; i < c.Path.Count; i++)
                        c.Path[i] = new Point2D(2 * cx - c.Path[i].X, c.Path[i].Y);
                    break;
            }
        }
        _page.RecomputeConductors();
        var after = CaptureObjectPositions();
        var moves = before
            .Where(m => after.TryGetValue(m.Key, out var a) && (a.X != m.Value.X || a.Y != m.Value.Y))
            .Select(m => (m.Value.Obj, m.Value.X, m.Value.Y,
                          m.Value.ShapePoints, after[m.Key].X, after[m.Key].Y, after[m.Key].ShapePoints,
                          m.Value.ControlPoints, after[m.Key].ControlPoints))
            .ToList();
        if (moves.Count > 0)
            UndoStack?.Push(new MoveObjectsCommand(_page, moves, "Espelhar horizontal"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>Espelha verticalmente (inverte Y) os objetos selecionados em torno do centro da seleção.</summary>
    public void MirrorSelectionVertical()
    {
        if (_page is null) return;
        var selected = _page.Objects.Where(o => o.IsSelected).ToList();
        if (selected.Count == 0) return;

        var bounds = ComputeSelectionBounds(selected);
        var cy = bounds.CenterY;
        var before = CaptureObjectPositions();

        foreach (var obj in selected)
        {
            switch (obj)
            {
                case PlacedElement el:
                    el.Y = 2 * cy - el.Y;
                    el.Rotation = NormalizeAngle(-el.Rotation);
                    break;
                case TextItem t:
                    t.Y = 2 * cy - t.Y;
                    t.Rotation = NormalizeAngle(-t.Rotation);
                    break;
                case ShapeItem s:
                    s.Mirror(cy, horizontal: false);
                    break;
                case Conductor c:
                    for (var i = 0; i < c.Path.Count; i++)
                        c.Path[i] = new Point2D(c.Path[i].X, 2 * cy - c.Path[i].Y);
                    break;
            }
        }
        _page.RecomputeConductors();
        var after = CaptureObjectPositions();
        var moves = before
            .Where(m => after.TryGetValue(m.Key, out var a) && (a.X != m.Value.X || a.Y != m.Value.Y))
            .Select(m => (m.Value.Obj, m.Value.X, m.Value.Y,
                          m.Value.ShapePoints, after[m.Key].X, after[m.Key].Y, after[m.Key].ShapePoints,
                          m.Value.ControlPoints, after[m.Key].ControlPoints))
            .ToList();
        if (moves.Count > 0)
            UndoStack?.Push(new MoveObjectsCommand(_page, moves, "Espelhar vertical"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>Gira os objetos selecionados por um ângulo livre (diálogo).</summary>
    public void RotateSelectionFree(double angleDegrees)
    {
        if (_page is null) return;
        var selectedElements = _page.Objects.OfType<PlacedElement>().Where(el => el.IsSelected).ToList();
        var selectedTexts = _page.Objects.OfType<TextItem>().Where(t => t.IsSelected).ToList();
        var selectedShapes = _page.Objects.OfType<ShapeItem>().Where(s => s.IsSelected && !s.IsLocked).ToList();

        if (selectedElements.Count == 0 && selectedTexts.Count == 0 && selectedShapes.Count == 0)
            return;

        // Calcula centro da seleção para rotação
        var allObjs = new List<DocumentObject>();
        allObjs.AddRange(selectedElements);
        allObjs.AddRange(selectedTexts);
        allObjs.AddRange(selectedShapes);
        var bounds = ComputeSelectionBounds(allObjs);
        var cx = bounds.CenterX;
        var cy = bounds.CenterY;

        var before = CaptureObjectPositions();

        foreach (var el in selectedElements)
        {
            var rad = angleDegrees * Math.PI / 180.0;
            var nx = cx + (el.X - cx) * Math.Cos(rad) - (el.Y - cy) * Math.Sin(rad);
            var ny = cy + (el.X - cx) * Math.Sin(rad) + (el.Y - cy) * Math.Cos(rad);
            el.X = nx;
            el.Y = ny;
            el.Rotation = NormalizeAngle(el.Rotation + angleDegrees);
        }
        foreach (var t in selectedTexts)
        {
            var rad = angleDegrees * Math.PI / 180.0;
            var nx = cx + (t.X - cx) * Math.Cos(rad) - (t.Y - cy) * Math.Sin(rad);
            var ny = cy + (t.X - cx) * Math.Sin(rad) + (t.Y - cy) * Math.Cos(rad);
            t.X = nx;
            t.Y = ny;
            t.Rotation = NormalizeAngle(t.Rotation + angleDegrees);
        }
        foreach (var s in selectedShapes)
            s.RotateAround(new Point2D(cx, cy), angleDegrees);

        _page.RecomputeConductors();
        var after = CaptureObjectPositions();
        var moves = before
            .Where(m => after.TryGetValue(m.Key, out var a) && (a.X != m.Value.X || a.Y != m.Value.Y))
            .Select(m => (m.Value.Obj, m.Value.X, m.Value.Y,
                          m.Value.ShapePoints, after[m.Key].X, after[m.Key].Y, after[m.Key].ShapePoints,
                          m.Value.ControlPoints, after[m.Key].ControlPoints))
            .ToList();
        if (moves.Count > 0)
            UndoStack?.Push(new MoveObjectsCommand(_page, moves, $"Girar {angleDegrees}°"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>Calcula o retângulo envolvente dos objetos selecionados.</summary>
    private Rect2D ComputeSelectionBounds(List<DocumentObject> objects)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var obj in objects)
        {
            switch (obj)
            {
                case PlacedElement el:
                    minX = Math.Min(minX, el.X); maxX = Math.Max(maxX, el.X);
                    minY = Math.Min(minY, el.Y); maxY = Math.Max(maxY, el.Y);
                    break;
                case TextItem t:
                    minX = Math.Min(minX, t.X); maxX = Math.Max(maxX, t.X);
                    minY = Math.Min(minY, t.Y); maxY = Math.Max(maxY, t.Y);
                    break;
                case ShapeItem s:
                    foreach (var pt in s.Points)
                    {
                        minX = Math.Min(minX, pt.X); maxX = Math.Max(maxX, pt.X);
                        minY = Math.Min(minY, pt.Y); maxY = Math.Max(maxY, pt.Y);
                    }
                    break;
            }
        }
        if (minX > maxX) return new Rect2D(0, 0, 0, 0);
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Gira os textos selecionados pelo ângulo informado (padrão 90°).</summary>
    public void RotateSelectedTexts(double angleDegrees = 90)
    {
        if (_page is null)
            return;
        var selected = _page.Objects.OfType<TextItem>().Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
            return;
        var before = selected.Select(t => (t, TextItemSnapshot.Of(t))).ToList();
        foreach (var t in selected)
            t.Rotation = (t.Rotation + angleDegrees) % 360;
        var commands = before
            .Select(p => (IUndoableCommand)new ChangeTextItemPropertiesCommand(p.t, p.Item2, TextItemSnapshot.Of(p.t)))
            .ToList();
        UndoStack?.Push(new CompositeCommand("Girar textos", commands));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>Move a seleção com as setas do teclado (passo do grid), como o padrão CAD.</summary>
    public void MoveSelectionByArrow(double dx, double dy)
    {
        if (_page is null)
            return;
        var step = Math.Max(Grid.Spacing, 1.0);
        var area = DrawingAreaBounds();
        var before = CaptureObjectPositions();
        foreach (var obj in _page.Objects)
        {
            switch (obj)
            {
                case PlacedElement el when el.IsSelected:
                    el.X += dx * step;
                    el.Y += dy * step;
                    ClampPlacedElement(el);
                    break;
                case TextItem t when t.IsSelected && !t.IsLocked:
                    t.X = Math.Clamp(t.X + dx * step, area.MinX, area.MaxX);
                    t.Y = Math.Clamp(t.Y + dy * step, area.MinY, area.MaxY);
                    break;
                case ShapeItem s when s.IsSelected && !s.IsLocked:
                    // Translate move pontos E pontos de controle Bézier — mover só os pontos
                    // DEFORMARIA as curvas importadas (as âncoras andavam, os controles ficavam).
                    s.Translate(dx * step, dy * step);
                    break;
                case TerminalStripItem item when item.IsSelected:
                    item.X = Math.Clamp(item.X + dx * step, area.MinX, area.MaxX);
                    item.Y = Math.Clamp(item.Y + dy * step, area.MinY, area.MaxY);
                    break;
            }
        }
        _page.RecomputeConductors();
        var after = CaptureObjectPositions();
        var moves = before
            .Where(m => after.TryGetValue(m.Key, out var a) && (a.X != m.Value.X || a.Y != m.Value.Y))
            .Select(m => (m.Value.Obj, m.Value.X, m.Value.Y,
                          m.Value.ShapePoints, after[m.Key].X, after[m.Key].Y, after[m.Key].ShapePoints,
                          m.Value.ControlPoints, after[m.Key].ControlPoints))
            .ToList();
        if (moves.Count > 0)
            UndoStack?.Push(new MoveObjectsCommand(_page, moves, "Mover seleção"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>Redimensiona as formas selecionadas (Shift+seta): move o último ponto.</summary>
    public void ResizeSelectedShapes(double dx, double dy)
    {
        if (_page is null)
            return;

        // Só com UM objeto selecionado: com um CONJUNTO, Shift+seta deformava todos os traços
        // (esticava cada um pela última ponta) em vez de ajustar uma peça.
        if (_page.Objects.Count(o => o.IsSelected) != 1)
            return;

        var step = Math.Max(Grid.Spacing, 1.0);
        var before = CaptureObjectPositions();
        foreach (var obj in _page.Objects)
        {
            if (obj is ShapeItem s && s.IsSelected && s.Points.Count > 0)
            {
                var last = s.Points[^1];
                s.Points[^1] = new Point2D(last.X + dx * step, last.Y + dy * step);

                // Curva Bézier: o ponto de controle do último segmento anda junto (senão a curva
                // entorta ao esticar o traço).
                if (s.ControlPoints is { Count: > 0 } cp)
                {
                    var lastPair = cp[^1];
                    cp[^1] = (lastPair.C1,
                        new Point2D(lastPair.C2.X + dx * step, lastPair.C2.Y + dy * step));
                }
            }
        }
        var after = CaptureObjectPositions();
        var moves = before
            .Where(m => after.TryGetValue(m.Key, out var a) && (a.X != m.Value.X || a.Y != m.Value.Y))
            .Select(m => (m.Value.Obj, m.Value.X, m.Value.Y,
                          m.Value.ShapePoints, after[m.Key].X, after[m.Key].Y, after[m.Key].ShapePoints,
                          m.Value.ControlPoints, after[m.Key].ControlPoints))
            .ToList();
        if (moves.Count > 0)
            UndoStack?.Push(new MoveObjectsCommand(_page, moves, "Redimensionar forma"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    private sealed record PosEntry(
        DocumentObject Obj, double X, double Y, List<Point2D> ShapePoints,
        List<(Point2D C1, Point2D C2)>? ControlPoints);

    private Dictionary<Guid, PosEntry> CaptureObjectPositions()
    {
        var map = new Dictionary<Guid, PosEntry>();
        if (_page is null)
            return map;
        foreach (var obj in _page.Objects)
        {
            if (!obj.IsSelected)
                continue;
            switch (obj)
            {
                case PlacedElement el:
                    map[el.Id] = new PosEntry(el, el.X, el.Y, new(), null);
                    break;
                case TextItem t:
                    map[t.Id] = new PosEntry(t, t.X, t.Y, new(), null);
                    break;
                case ShapeItem s:
                    // Formas sem pontos (degeneradas) não têm posição capturável.
                    if (s.Points.Count == 0)
                        break;
                    map[s.Id] = new PosEntry(s, s.Points[0].X, s.Points[0].Y,
                        s.Points.Select(p => new Point2D(p.X, p.Y)).ToList(),
                        s.SnapshotControlPoints());
                    break;
                case TerminalStripItem item:
                    map[item.Id] = new PosEntry(item, item.X, item.Y, new(), null);
                    break;
            }
        }
        return map;
    }

    /// <summary>Inverte a seleção (seleciona o não selecionado e vice-versa), como o padrão CAD.</summary>
    public void InvertSelection()
    {
        if (_page is null)
            return;
        foreach (var obj in _page.Objects)
            obj.IsSelected = !obj.IsSelected;
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Centraliza e seleciona um objeto (ex.: localizar resultado de busca), como o padrão CAD.</summary>
    public void SelectAndCenter(DocumentObject obj)
    {
        if (_page is null)
            return;
        foreach (var o in _page.Objects)
            o.IsSelected = ReferenceEquals(o, obj);
        // centraliza no objeto
        var c = obj switch
        {
            PlacedElement el => new Point2D(el.X, el.Y),
            TextItem t => new Point2D(t.X, t.Y),
            _ => new Point2D(0, 0)
        };
        var center = new Point2D(ActualWidth / 2, ActualHeight / 2);
        var world = View.ScreenToWorld(center);
        View = new ViewTransform(View.Scale, center.X - c.X, center.Y + c.Y);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adiciona um item de borne (tabela) aa folha na posição dada (barra Inserir).</summary>
    public void AddTerminalStripItem(OperCAD.Core.TerminalStrips.TerminalStrip strip, Point2D worldPos)
    {
        if (_page is null)
            return;
        var item = new TerminalStripItem
        {
            StripUuid = strip.Uuid,
            X = worldPos.X,
            Y = worldPos.Y
        };
        _page.Add(item);
        UndoStack?.Push(new AddObjectsCommand(_page, new[] { item }, "Adicionar borne"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// Ponto de desenho para uma posição do cursor — MESMA regra no clique e na
    /// pré-visualização (o que se vê é onde a forma vai ficar):
    ///   1. snap a objetos (vértices, meios de linha, centros de elipse/retângulo/arco) tem
    ///      prioridade — é o que permite começar/terminar exatamente sobre outra linha;
    ///   2. a ferramenta LINHA não passa pela grade: sem objeto próximo o ponto é exatamente
    ///      o centro do crosshair, permitindo puxar uma linha precisamente em cima de outra;
    ///   3. as demais ferramentas seguem a grade (Ctrl durante o clique libera);
    ///   4. a restrição angular de 5° (Shift libera) só vale quando NÃO houve snap, senão
    ///      ela deslocaria o ponto engatado e perderia a precisão.
    /// </summary>
    private (Point2D Point, bool Snapped) DrawPoint(Point2D world, double tolerance, bool angularToLast)
    {
        var snap = FindSnapPoint(world, tolerance);
        var (sx, sy) = SnapToGrid(world);
        var useAngular = angularToLast && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var last = _drawPoints.Count > 0 ? _drawPoints[^1] : default;
        return DrawingPointRules.Resolve(
            world, snap, new Point2D(sx, sy), DrawTool is InsertTool.Line, useAngular, last);
    }

    /// <summary>Processa um clique da ferramenta de desenho de formas (linha/retângulo/elipse/polilinha/arco).</summary>
    private void HandleDrawToolClick(Point2D world)
    {
        if (_page is null)
            return;
        // Snap a objetos e regra da grade ficam em DrawPoint (mesma regra da pré-visualização).
        var point = DrawPoint(world, 3.0 / View.Scale, angularToLast: true).Point;
        var p = ClampPointToDrawingArea(point);

        var props = DrawingProperties;
        var color = props?.SelectedColor ?? "black";
        var penWidth = props?.SelectedPenWidth ?? 0.29;
        var dashStyle = props?.SelectedDashStyle ?? LineDashStyle.Solid;
        // Formas fechadas são VAZADAS por padrão; só preenchem se o painel estiver em
        // "Preenchido (sólido)" (FillingForNewShapes é separado do preenchimento da forma
        // selecionada, para o padrão da ferramenta não ser contaminado pela seleção).
        var filling = props?.FillingForNewShapes ?? "none";

        switch (DrawTool)
        {
        case InsertTool.Line:
            _drawPoints.Add(p);
            break;

        case InsertTool.Curve:
            _drawPoints.Add(p);
            break;

            case InsertTool.Rectangle:
                _drawPoints.Add(p);
                if (_drawPoints.Count == 2)
                {
                    var shape = new ShapeItem
                    {
                        Kind = ShapeKind.Rectangle,
                        Points = new List<Point2D> { _drawPoints[0], _drawPoints[1] },
                        Color = color,
                        PenWidth = penWidth,
                        DashStyle = dashStyle,
                        Filling = filling
                    };
                    _page.Add(shape);
                    UndoStack?.Push(new AddObjectsCommand(_page, new[] { shape }, "Adicionar retângulo"));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                    _drawPoints.Clear();
                    EndDrawTool();
                }
                break;

            case InsertTool.Ellipse:
                _drawPoints.Add(p);
                if (_drawPoints.Count == 2)
                {
                    var shape = new ShapeItem
                    {
                        Kind = ShapeKind.Ellipse,
                        Points = new List<Point2D> { _drawPoints[0], _drawPoints[1] },
                        Color = color,
                        PenWidth = penWidth,
                        DashStyle = dashStyle,
                        Filling = filling
                    };
                    _page.Add(shape);
                    UndoStack?.Push(new AddObjectsCommand(_page, new[] { shape }, "Adicionar elipse"));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                    _drawPoints.Clear();
                    EndDrawTool();
                }
                break;

            case InsertTool.Triangle:
                _drawPoints.Add(p);
                if (_drawPoints.Count == 3)
                {
                    var shape = new ShapeItem
                    {
                        Kind = ShapeKind.Triangle,
                        Points = new List<Point2D> { _drawPoints[0], _drawPoints[1], _drawPoints[2] },
                        Color = color,
                        PenWidth = penWidth,
                        DashStyle = dashStyle,
                        Filling = filling
                    };
                    _page.Add(shape);
                    UndoStack?.Push(new AddObjectsCommand(_page, new[] { shape }, "Adicionar triângulo"));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                    _drawPoints.Clear();
                    EndDrawTool();
                }
                break;

            case InsertTool.Arc:
                // Centro-Raio-Sweep:
                //   1º clique: centro (define start=0, sweep=90)
                //   2º clique: trava o raio (mantém start e sweep atuais)
                //   scroll ajusta sweep, Shift+scroll ajusta o start
                //   3º clique (ou Enter): cria o arco
                _drawPoints.Add(p);
                if (_drawPoints.Count == 1)
                {
                    _arcStartAnglePreview = 0;
                    _arcSweepPreview = 90;
                }
                else if (_drawPoints.Count >= 3)
                {
                    FinishAngleArc();
                }
                break;

            case InsertTool.EllipseArc:
                // Arco de elipse:
                //   1º clique: centro (define start=0, sweep=180)
                //   2º clique: trava rx/ry das extremidades (mantém start e sweep atuais)
                //   scroll ajusta sweep, Shift+scroll ajusta o start
                //   3º clique (ou Enter): cria o arco
                _drawPoints.Add(p);
                if (_drawPoints.Count == 1)
                {
                    _arcStartAnglePreview = 0;
                    _arcSweepPreview = 180;
                }
                else if (_drawPoints.Count >= 3)
                {
                    FinishAngleArc();
                }
                break;

            case InsertTool.RoundedRectangle:
                _drawPoints.Add(p);
                if (_drawPoints.Count == 2)
                {
                    var shape = new ShapeItem
                    {
                        Kind = ShapeKind.RoundedRectangle,
                        Points = new List<Point2D> { _drawPoints[0], _drawPoints[1] },
                        Rx = 2.0,
                        Ry = 2.0,
                        Color = color,
                        PenWidth = penWidth,
                        DashStyle = dashStyle,
                        Filling = filling
                    };
                    _page.Add(shape);
                    UndoStack?.Push(new AddObjectsCommand(_page, new[] { shape }, "Adicionar retângulo arredondado"));
                    EditStateChanged?.Invoke(this, EventArgs.Empty);
                    _drawPoints.Clear();
                    EndDrawTool();
                }
                break;

            case InsertTool.Measure:
                // Ferramenta de medição: dois cliques mostram a distância
                _drawPoints.Add(p);
                if (_drawPoints.Count == 2)
                {
                    var dist = _drawPoints[0].DistanceTo(_drawPoints[1]);
                    // A medição é apenas visual (não adiciona objeto à folha)
                    _measureResult = dist;
                    _drawPoints.Clear();
                    EndDrawTool();
                }
                break;
        }
        _canvas.InvalidateVisual();
    }

    /// <summary>Finaliza a criação do arco/arco de elipse (3º clique ou Enter).</summary>
    private void FinishAngleArc()
    {
        if (_page is null || _drawPoints.Count < 2)
        {
            _drawPoints.Clear();
            EndDrawTool();
            return;
        }
        var props = DrawingProperties;
        var isEllipse = DrawTool == InsertTool.EllipseArc;
        var kind = isEllipse ? ShapeKind.EllipseArc : ShapeKind.Arc;
        var desc = isEllipse ? "Adicionar arco de elipse" : "Adicionar arco";
        var center = _drawPoints[0];
        var shape = new ShapeItem
        {
            Kind = kind,
            Points = new List<Point2D> { center, _drawPoints[1] },
            StartAngle = _arcStartAnglePreview,
            SweepAngle = _arcSweepPreview,
            Color = props?.SelectedColor ?? "black",
            PenWidth = props?.SelectedPenWidth ?? 0.29,
            DashStyle = props?.SelectedDashStyle ?? LineDashStyle.Solid,
            Filling = props?.FillingForNewShapes ?? "none"
        };
        _page.Add(shape);
        UndoStack?.Push(new AddObjectsCommand(_page, new[] { shape }, desc));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _drawPoints.Clear();
        _arcSweepPreview = 90;
        _arcStartAnglePreview = 0;
        EndDrawTool();
    }

    /// <summary>Finaliza a polilinha (Enter ou duplo-clique).</summary>
    private void FinishPolygon()
    {
        if (_page is null || _drawPoints.Count < 2)
        {
            _drawPoints.Clear();
            EndDrawTool();
            return;
        }
        var props = DrawingProperties;
        var kind = _drawPoints.Count == 2 ? ShapeKind.Line : ShapeKind.Polygon;
        var desc = kind == ShapeKind.Line ? "Adicionar linha" : "Adicionar polilinha";
        var shape = new ShapeItem
        {
            Kind = kind,
            Points = _drawPoints.ToList(),
            Closed = false,
            Color = props?.SelectedColor ?? "black",
            PenWidth = props?.SelectedPenWidth ?? 0.29,
            DashStyle = props?.SelectedDashStyle ?? LineDashStyle.Solid
        };
        _page.Add(shape);
        UndoStack?.Push(new AddObjectsCommand(_page, new[] { shape }, desc));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _drawPoints.Clear();
        EndDrawTool();
    }

    private void FinishCurve()
    {
        if (_page is null || _drawPoints.Count < 2)
        {
            _drawPoints.Clear();
            EndDrawTool();
            return;
        }
        var props = DrawingProperties;
        var kind = _drawPoints.Count == 2 ? ShapeKind.Line : ShapeKind.Curve;
        var desc = kind == ShapeKind.Line ? "Adicionar linha" : "Adicionar curva";
        var shape = new ShapeItem
        {
            Kind = kind,
            Points = _drawPoints.ToList(),
            Closed = false,
            Color = props?.SelectedColor ?? "black",
            PenWidth = props?.SelectedPenWidth ?? 0.29,
            DashStyle = props?.SelectedDashStyle ?? LineDashStyle.Solid
        };
        _page.Add(shape);
        UndoStack?.Push(new AddObjectsCommand(_page, new[] { shape }, desc));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _drawPoints.Clear();
        EndDrawTool();
    }

    /// <summary>Notifica observadores de que a seleção mudou (usado pelos comandos de seleção).</summary>
    public void NotifySelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Notifica observadores de que o estado de edição mudou (usado pelos comandos de propriedades).</summary>
    public void NotifyEditStateChanged() => EditStateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Copia os objetos selecionados para a área de transferência interna.</summary>
    public void CopySelection()
    {
        if (_page is null)
            return;
        Clipboard.Copy(_page);
        EditStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Recorta: copia e depois exclui a seleção (um comando de undo).</summary>
    public void CutSelection()
    {
        if (_page is null)
            return;
        var removed = _page.Objects.Where(o => o.IsSelected).ToList();
        if (removed.Count == 0)
            return;
        Clipboard.Copy(_page);
        foreach (var obj in removed)
            _page.RemoveObject(obj);
        UndoStack?.Push(new RemoveObjectsCommand(_page, removed, "Recortar"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cola o conteúdo da área de transferência na posição atual do cursor.</summary>
    public void PasteAtCursor()
    {
        Paste();
    }

    /// <summary>Cola o conteúdo copiado na posição do cursor (ou centro da vista).
    /// Se houver múltiplos itens na área de transferência, abre a janela de colagem múltipla.</summary>
    public void Paste()
    {
        if (_page is null || !Clipboard.HasContent)
            return;

        var target = new Point2D(CursorWorld.X, CursorWorld.Y);
        var created = Clipboard.Paste(_page, target);
        if (created.Count == 0)
            return;
        // ⭐ O CONJUNTO colado é reposicionado por INTEIRO: limitar objeto por objeto desalinhava
        // os traços do símbolo (cada peça ia para um lugar diferente perto da borda).
        ClampPastedSetIntoDrawingArea(created);
        GroupPastedSet(created);
        _page.RecomputeConductors();
        foreach (var o in _page.Objects)
            o.IsSelected = false;
        foreach (var o in created)
            o.IsSelected = true;
        UndoStack?.Push(new AddObjectsCommand(_page, created, "Colar"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cola múltiplas cópias do conteúdo da área de transferência.</summary>
    public void PasteMultiple(int copies, MultiPasteLayout layout, double spacing)
    {
        if (_page is null || !Clipboard.HasContent || copies <= 0)
            return;

        var allCreated = new List<DocumentObject>();
        var positions = ComputePastePositions(copies, layout, spacing);

        foreach (var offset in positions)
        {
            var target = new Point2D(CursorWorld.X + offset.X, CursorWorld.Y + offset.Y);
            var created = Clipboard.Paste(_page, target);
            // ⭐ Cada cópia é reposicionada por INTEIRO (preserva a disposição dos traços).
            ClampPastedSetIntoDrawingArea(created);
            GroupPastedSet(created);
            allCreated.AddRange(created);
        }

        if (allCreated.Count == 0)
            return;

        _page.RecomputeConductors();
        foreach (var o in _page.Objects)
            o.IsSelected = false;
        foreach (var o in allCreated)
            o.IsSelected = true;
        UndoStack?.Push(new AddObjectsCommand(_page, allCreated, $"Colar {copies} cópias"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Calcula os offsets de posição para cada cópia na colagem múltipla.</summary>
    private List<Point2D> ComputePastePositions(int copies, MultiPasteLayout layout, double spacing)
    {
        var positions = new List<Point2D>();

        switch (layout)
        {
            case MultiPasteLayout.Horizontal:
                for (var i = 0; i < copies; i++)
                    positions.Add(new Point2D(i * spacing, 0));
                break;

            case MultiPasteLayout.Vertical:
                for (var i = 0; i < copies; i++)
                    positions.Add(new Point2D(0, i * spacing));
                break;

            case MultiPasteLayout.Grade:
                var cols = (int)Math.Ceiling(Math.Sqrt(copies));
                for (var i = 0; i < copies; i++)
                {
                    var row = i / cols;
                    var col = i % cols;
                    positions.Add(new Point2D(col * spacing, row * spacing));
                }
                break;
        }

        return positions;
    }

    /// <summary>Desfaz o último comando e atualiza a seleção (se o comando removeu objetos).</summary>
    public void Undo()
    {
        if (UndoStack is null || !UndoStack.CanUndo)
            return;
        UndoStack.Undo();
        if (_page is not null)
        {
            foreach (var o in _page.Objects)
                o.IsSelected = false;
        }
        _page?.RecomputeConductors();
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        EditStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refaz o próximo comando desfeito.</summary>
    public void Redo()
    {
        if (UndoStack is null || !UndoStack.CanRedo)
            return;
        UndoStack.Redo();
        if (_page is not null)
        {
            foreach (var o in _page.Objects)
                o.IsSelected = false;
        }
        _page?.RecomputeConductors();
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        EditStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Descrições dos comandos aplicados (para o painel Desfazer).</summary>
    public IReadOnlyList<string> UndoDescriptions
        => UndoStack?.Descriptions ?? Array.Empty<string>();

    /// <summary>Trai o objeto selecionado para o topo (z-order).</summary>
    public void BringToFront()
    {
        if (_page is null)
            return;
        var obj = FirstSelected();
        if (obj is null)
            return;
        var old = _page.Objects.IndexOf(obj);
        if (old < 0 || old == _page.Objects.Count - 1)
            return;
        var target = _page.Objects.Count - 1;
        MoveObject(obj, old, target, "Trazer para a frente");
    }

    /// <summary>Sobe um nível o objeto selecionado (z-order).</summary>
    public void Raise()
    {
        if (_page is null)
            return;
        var obj = FirstSelected();
        if (obj is null)
            return;
        var old = _page.Objects.IndexOf(obj);
        if (old < 0 || old >= _page.Objects.Count - 1)
            return;
        MoveObject(obj, old, old + 1, "Subir");
    }

    /// <summary>Desce um nível o objeto selecionado (z-order).</summary>
    public void Lower()
    {
        if (_page is null)
            return;
        var obj = FirstSelected();
        if (obj is null)
            return;
        var old = _page.Objects.IndexOf(obj);
        if (old <= 0)
            return;
        MoveObject(obj, old, old - 1, "Descer");
    }

    /// <summary>Envia o objeto selecionado para o fundo (z-order).</summary>
    public void SendToBack()
    {
        if (_page is null)
            return;
        var obj = FirstSelected();
        if (obj is null)
            return;
        var old = _page.Objects.IndexOf(obj);
        if (old <= 0)
            return;
        MoveObject(obj, old, 0, "Enviar para trás");
    }

    private DocumentObject? FirstSelected()
        => _page?.Objects.FirstOrDefault(o => o.IsSelected);

    private void MoveObject(DocumentObject obj, int oldIndex, int newIndex, string description)
    {
        if (_page is null)
            return;
        _page.Objects.Remove(obj);
        _page.Objects.Insert(Math.Clamp(newIndex, 0, _page.Objects.Count), obj);
        UndoStack?.Push(new ZOrderCommand(_page, obj, oldIndex, newIndex, description));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
    }

    private PlacedElement? HitTestElement(Point2D world)
    {
        if (_page is null)
            return null;
        PlacedElement? best = null;
        double bestArea = double.MaxValue;
        foreach (var obj in _page.Objects)
        {
            if (obj is not PlacedElement el)
                continue;
            // Tolerância de ~8 px na tela, convertida para unidades do MODELO do elemento
            // (o WorldToLocal divide pela escala efetiva do elemento).
            var tolModel = 8.0 / (ElementRenderer.ElementScale(el) * View.Scale);
            var bounds = ElementRenderer.ComputeBounds(el, el.Label);
            var local = TerminalGeometry.WorldToLocal(el, world);
            if (bounds.Inflate(tolModel).Contains(local))
            {
                var area = bounds.Width * bounds.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = el;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Hit-test para hover: retorna o elemento sob o cursor (mais específico).
    /// Usa tolerância menor que o hit-test de seleção.
    /// </summary>
    private PlacedElement? HitTestElementForHover(Point2D world)
    {
        if (_page is null)
            return null;
        PlacedElement? best = null;
        double bestArea = double.MaxValue;
        foreach (var obj in _page.Objects)
        {
            if (obj is not PlacedElement el)
                continue;
            // Tolerância de ~4 px na tela, convertida para unidades do MODELO do elemento.
            var tolModel = 4.0 / (ElementRenderer.ElementScale(el) * View.Scale);
            var bounds = ElementRenderer.ComputeBounds(el, el.Label);
            var local = TerminalGeometry.WorldToLocal(el, world);
            if (bounds.Inflate(tolModel).Contains(local))
            {
                var area = bounds.Width * bounds.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = el;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Hit-test para textos dinâmicos de um elemento: retorna o índice do texto
    /// dinâmico sob o cursor, ou -1 se nenhum.
    /// </summary>
    private int HitTestDynamicText(PlacedElement el, Point2D world)
    {
        var local = TerminalGeometry.WorldToLocal(el, world);
        var def = el.Definition;

        for (var i = 0; i < def.Primitives.Count; i++)
        {
            if (def.Primitives[i] is ElementDynamicText dt)
            {
                var effective = dt;
                var text = dt.Text;
                if (dt.TextFrom == "ElementInfo")
                {
                    if (!string.IsNullOrEmpty(el.Label))
                        text = el.Label;
                    else
                        continue;
                }
                else if (el.GetTextOverride(i) is { } ov)
                {
                    text = ov;
                    effective = dt with { Text = ov };
                }
                else if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var box = ElementRenderer.DynamicTextBounds(effective, el.Label);
                if (box is not null && box.Value.Inflate(1.5).Contains(local))
                    return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Inicia a edição inline de um texto dinâmico de um elemento.
    /// Cria um TextBox WPF sobreposed no canvas na posição do texto.
    /// </summary>
    public void BeginInlineTextEdit(PlacedElement el, int dynamicTextIndex)
    {
        if (_page is null || dynamicTextIndex < 0)
            return;

        var def = el.Definition;
        if (dynamicTextIndex >= def.Primitives.Count)
            return;
        if (def.Primitives[dynamicTextIndex] is not ElementDynamicText dt)
            return;

        var text = dt.Text;
        if (dt.TextFrom == "ElementInfo")
            text = el.Label;
        else if (el.GetTextOverride(dynamicTextIndex) is { } overrideText)
            text = overrideText;

        // Posição do texto no mundo
        var textWorld = TerminalGeometry.LocalToWorld(el, new Point2D(dt.X, dt.Y));
        var textScreen = View.WorldToScreen(textWorld);

        // Remove TextBox anterior se existir
        RemoveInlineTextBox();

        // Cria o TextBox
        var dynFontSize = Math.Max(10, 12 * View.Scale);
        _inlineTextBox = new System.Windows.Controls.TextBox
        {
            Text = text ?? string.Empty,
            FontSize = dynFontSize,
            MinWidth = 60,
            MaxWidth = 300,
            Width = Math.Max(60, Math.Min(300, dynFontSize * (text?.Length ?? 4) * 0.6)),
            MinHeight = dynFontSize + 4,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215)),
            BorderThickness = new System.Windows.Thickness(2),
            Padding = new System.Windows.Thickness(2),
            Tag = (el, dynamicTextIndex, dt),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            RenderTransform = new System.Windows.Media.TranslateTransform(textScreen.X, textScreen.Y - 15)
        };

        Children.Add(_inlineTextBox);
        _inlineTextBox.Focus();
        _inlineTextBox.SelectAll();

        _inlineTextBox.KeyDown += OnInlineTextKeyDown;
        _inlineTextBox.LostFocus += OnInlineTextLostFocus;
    }

    private void OnInlineTextKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Return)
        {
            CommitInlineTextEdit();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            RemoveInlineTextBox();
            e.Handled = true;
        }
    }

    private void OnInlineTextLostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        CommitInlineTextEdit();
    }

    private void CommitInlineTextEdit()
    {
        if (_inlineTextBox?.Tag is (PlacedElement el, int index, ElementDynamicText dt))
        {
            var newText = _inlineTextBox.Text;
            if (dt.TextFrom == "ElementInfo")
            {
                el.Label = newText;
            }
            else
            {
                // Sobrescreve o texto apenas NESTA instância (não altera a definição do símbolo).
                var idx = el.Definition.Primitives.IndexOf(dt);
                if (idx >= 0)
                    el.SetTextOverride(idx, newText);
            }
            _canvas.InvalidateVisual();
        }
        RemoveInlineTextBox();
    }

    private void RemoveInlineTextBox()
    {
        if (_inlineTextBox is not null)
        {
            _inlineTextBox.KeyDown -= OnInlineTextKeyDown;
            _inlineTextBox.LostFocus -= OnInlineTextLostFocus;
            _inlineTextBox.KeyDown -= OnInlineTextItemKeyDown;
            _inlineTextBox.PreviewLostKeyboardFocus -= OnInlineTextItemLostFocus;
            _inlineTextBox.KeyDown -= OnInlineTitleBlockKeyDown;
            _inlineTextBox.PreviewLostKeyboardFocus -= OnInlineTitleBlockLostFocus;
            Children.Remove(_inlineTextBox);
            _inlineTextBox = null;
        }
    }

    #region Inline TextItem editing (independent text on sheet)

    private TextItem? _editingTextItem;
    private bool _inlineTextItemCommitted;

    /// <summary>Hit-test para ShapeItem na folha.</summary>
    /// <summary>
    /// Hit-test de formas com prioridade geométrica: retorna a forma (da frente para trás)
    /// que está realmente "sob" o ponto — sobre o traço ou dentro do preenchimento. Se o
    /// ponto está apenas no interior vazio de um contorno (ex.: retângulo tracejado), escolhe
    /// o contorno mais à frente como fallback, para ainda dar para mover caixas vazias.
    /// </summary>
    public ShapeItem? HitTestShape(Point2D world)
        => ResolveShapeHit(world, HitTestShapeCandidates(world));

    /// <summary>Candidatas sob o ponto (da frente para trás), incluídas pelo retângulo envolvente.</summary>
    private List<ShapeItem> HitTestShapeCandidates(Point2D world)
    {
        var result = new List<ShapeItem>();
        if (_page is null) return result;
        var inflate = 2.0 / View.Scale;
        for (var i = _page.Objects.Count - 1; i >= 0; i--)
        {
            if (_page.Objects[i] is not ShapeItem s || s.IsLocked) continue;
            if (IsObjectLockedOrHidden(s)) continue;
            if (ShapeRenderer.ComputeBounds(s).Inflate(inflate).Contains(world))
                result.Add(s);
        }
        return result;
    }

    /// <summary>Verifica se um objeto está em uma camada invisível ou bloqueada.</summary>
    private bool IsObjectLockedOrHidden(DocumentObject obj)
    {
        if (obj.LayerId is not { } layerId || _page is null)
            return false;
        var layer = _page.Layers.FirstOrDefault(l => l.Id == layerId);
        return layer is not null && (layer.IsLocked || !layer.IsVisible);
    }

    /// <summary>
    /// Resolve qual candidata selecionar: a da frente que está "tocada" (traço/preenchimento)
    /// ou dentro da região fechada (interior geométrico). O retângulo envolvente é usado apenas
    /// como pré-filtro em <see cref="HitTestShapeCandidates"/> — nunca como área de seleção.
    /// </summary>
    private ShapeItem? ResolveShapeHit(Point2D world, List<ShapeItem> candidates)
    {
        var tol = 3.0 / View.Scale;
        foreach (var s in candidates)
            if (IsShapeTouched(s, world, tol) || ShapeGeometry.PointInRegion(s, world))
                return s;
        return null;
    }

    private static bool IsShapeTouched(ShapeItem s, Point2D w, double tol)
        => ShapeGeometry.DistanceToOutline(s, w) <= tol
           || (ShapeGeometry.IsFilled(s) && ShapeGeometry.PointInRegion(s, w));

    private static int? IndexOf(List<ShapeItem> list, Guid id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return null;
    }

    /// <summary>Hit-test para TextItem independente na folha.</summary>
    public TextItem? HitTestTextItem(Point2D world)
    {
        if (_page is null) return null;
        foreach (var obj in _page.Objects)
        {
            if (obj is not TextItem t) continue;
            if (t.IsLocked || IsObjectLockedOrHidden(t)) continue;
            var bounds = TextItemRenderer.ComputeBounds(t);
            var tol = Math.Max(8.0 / View.Scale, 2.0);
            if (bounds.Inflate(tol).Contains(world))
                return t;
        }
        return null;
    }

    private enum TextHandleKind { None, Resize, Rotate }

    private enum ShapeHandleKind { None, Resize, Rotate }

    /// <summary>Hit-test das alças de redimensionar/rotacionar de um texto selecionado (coordenadas de tela).</summary>
    private TextHandleKind HitTestTextHandle(TextItem t, Point2D screenPos)
    {
        var rect = TextItemRenderer.SelectionRect(t, View);
        var handleR = TextItemRenderer.HandleHalf + 4.0;
        foreach (var corner in new[]
        {
            new Point2D(rect.Left, rect.Top),
            new Point2D(rect.Right, rect.Top),
            new Point2D(rect.Left, rect.Bottom),
            new Point2D(rect.Right, rect.Bottom)
        })
        {
            if (screenPos.DistanceTo(corner) <= handleR)
                return TextHandleKind.Resize;
        }
        var rotCenter = new Point2D(rect.MidX, rect.Top - 20);
        if (screenPos.DistanceTo(rotCenter) <= handleR)
            return TextHandleKind.Rotate;
        return TextHandleKind.None;
    }

    /// <summary>Hit-test das alças de redimensionar/rotacionar de uma forma selecionada (coordenadas de tela).</summary>
    private ShapeHandleKind HitTestShapeHandle(ShapeItem shape, Point2D screenPos)
    {
        if (shape.Kind is ShapeKind.Arc or ShapeKind.EllipseArc)
            return ShapeHandleKind.None; // Arcs use angle handles instead
        var b = ShapeRenderer.ComputeBounds(shape);
        var tl = View.WorldToScreen(new Point2D(b.MinX, b.MinY));
        var br = View.WorldToScreen(new Point2D(b.MaxX, b.MaxY));
        var handleR = TextItemRenderer.HandleHalf + 4.0;

        // Rotation handle
        var rotCenter = new Point2D((tl.X + br.X) / 2, tl.Y - 20);
        if (screenPos.DistanceTo(rotCenter) <= handleR)
            return ShapeHandleKind.Rotate;

        // Traço com alças nas extremidades: o clique na extremidade deve ESTICAR a linha
        // (arrastar o vértice), não escalar a caixa envolvente — como no editor de PDF.
        if (ShapeGeometry.UsesVertexHandles(shape))
            return ShapeHandleKind.None;

        // Resize handles (8 corners/edges)
        foreach (var corner in new[]
        {
            new Point2D(tl.X, tl.Y),
            new Point2D((tl.X + br.X) / 2, tl.Y),
            new Point2D(br.X, tl.Y),
            new Point2D(br.X, (tl.Y + br.Y) / 2),
            new Point2D(br.X, br.Y),
            new Point2D((tl.X + br.X) / 2, br.Y),
            new Point2D(tl.X, br.Y),
            new Point2D(tl.X, (tl.Y + br.Y) / 2)
        })
        {
            if (screenPos.DistanceTo(corner) <= handleR)
                return ShapeHandleKind.Resize;
        }
        return ShapeHandleKind.None;
    }

    /// <summary>Inicia a edição inline de um TextItem independente na folha.</summary>
    public void BeginInlineTextItemEdit(TextItem item)
    {
        RemoveInlineTextBox();
        _editingTextItem = item;
        _inlineTextItemCommitted = false;

        var pos = View.WorldToScreen(new Point2D(item.X, item.Y));
        var sizeMm = item.FontSizePt * 0.3528;
        var screenFontSize = Math.Max(10, sizeMm * View.Scale);

        _inlineTextBox = new System.Windows.Controls.TextBox
        {
            Text = item.Text,
            FontSize = screenFontSize,
            MinWidth = 80,
            MaxWidth = 300,
            MinHeight = screenFontSize + 4,
            MaxHeight = screenFontSize * 6,
            Width = Math.Max(80, Math.Min(300, screenFontSize * item.Text.Length * 0.6)),
            Background = System.Windows.Media.Brushes.White,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black),
            BorderBrush = System.Windows.Media.Brushes.Red,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(2, 1, 2, 1),
            Tag = item,
            AcceptsReturn = false,
            TextWrapping = System.Windows.TextWrapping.NoWrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            RenderTransform = new System.Windows.Media.TranslateTransform(pos.X, pos.Y - screenFontSize - 4)
        };
        Children.Add(_inlineTextBox);
        _inlineTextBox.Focus();
        _inlineTextBox.SelectAll();

        _inlineTextBox.KeyDown += OnInlineTextItemKeyDown;
        _inlineTextBox.PreviewLostKeyboardFocus += OnInlineTextItemLostFocus;
    }

    private void OnInlineTextItemKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Return)
        {
            CommitInlineTextItemEdit();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            RemoveInlineTextBox();
            e.Handled = true;
        }
    }

    private void OnInlineTextItemLostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        CommitInlineTextItemEdit();
    }

    private void CommitInlineTextItemEdit()
    {
        if (_inlineTextItemCommitted) return;
        _inlineTextItemCommitted = true;

        if (_inlineTextBox?.Tag is TextItem item)
        {
            var newText = _inlineTextBox.Text;
            if (!string.IsNullOrWhiteSpace(newText) && newText != item.Text)
            {
                var snapshot = TextItemSnapshot.Of(item);
                item.Text = newText;
                UndoStack?.Push(new ChangeTextItemPropertiesCommand(item, snapshot, TextItemSnapshot.Of(item)));
            }
            _canvas.InvalidateVisual();
        }
        RemoveInlineTextBox();
        _editingTextItem = null;
    }

    private void RemoveInlineTextItemEdit()
    {
        if (_inlineTextBox is not null)
        {
            _inlineTextBox.KeyDown -= OnInlineTextItemKeyDown;
            _inlineTextBox.PreviewLostKeyboardFocus -= OnInlineTextItemLostFocus;
            _inlineTextBox.KeyDown -= OnInlineTextKeyDown;
            _inlineTextBox.LostFocus -= OnInlineTextLostFocus;
            _inlineTextBox.KeyDown -= OnInlineTitleBlockKeyDown;
            _inlineTextBox.PreviewLostKeyboardFocus -= OnInlineTitleBlockLostFocus;
            Children.Remove(_inlineTextBox);
            _inlineTextBox = null;
        }
        _editingTextItem = null;
    }

    #endregion

    #region Inline TitleBlock editing

    public void BeginInlineTitleBlockEdit(Page page, string field, Rect2D worldBounds, string currentValue, int maxLength, bool multiline, OperCAD.Core.Documents.TitleBlockFieldStyle style)
    {
        RemoveInlineTextBox();

        var screenTopLeft = View.WorldToScreen(new Point2D(worldBounds.MinX, worldBounds.MaxY));
        var screenBottomRight = View.WorldToScreen(new Point2D(worldBounds.MaxX, worldBounds.MinY));
        var screenW = Math.Max(20, screenBottomRight.X - screenTopLeft.X);
        var screenH = Math.Max(10, screenBottomRight.Y - screenTopLeft.Y);

        double screenFontSize;
        if (style.FontSizePt is { } customPt)
            screenFontSize = customPt * 0.3528 * View.Scale; // pt -> mm -> screen
        else
            screenFontSize = Math.Clamp((worldBounds.Height * 0.42) * View.Scale, 10, 50);

        _inlineTextBox = new System.Windows.Controls.TextBox
        {
            Text = currentValue,
            MaxLength = maxLength > 0 ? maxLength : 0,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? System.Windows.TextWrapping.Wrap : System.Windows.TextWrapping.NoWrap,
            Width = screenW,
            Height = screenH,
            FontSize = screenFontSize,
            FontFamily = new System.Windows.Media.FontFamily(style.FontFamily ?? "Arial"),
            FontWeight = (style.Bold == true) ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
            FontStyle = (style.Italic == true) ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 255, 255, 255)),
            BorderBrush = System.Windows.Media.Brushes.Blue,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(2),
            RenderTransform = new System.Windows.Media.TranslateTransform(screenTopLeft.X, screenTopLeft.Y),
            Tag = new ValueTuple<string, Page, string, OperCAD.Core.Documents.TitleBlockFieldStyle>("TitleBlock", page, field, style)
        };

        Children.Add(_inlineTextBox);
        _inlineTextBox.Focus();
        _inlineTextBox.SelectAll();

        _inlineTextBox.KeyDown += OnInlineTitleBlockKeyDown;
        _inlineTextBox.PreviewLostKeyboardFocus += OnInlineTitleBlockLostFocus;
    }

    public void UpdateInlineTitleBlockStyle(double? fontSizePt, bool? bold, bool? italic, string? fontFamily)
    {
        if (_inlineTextBox?.Tag is ValueTuple<string, Page, string, OperCAD.Core.Documents.TitleBlockFieldStyle> tag && tag.Item1 == "TitleBlock")
        {
            if (fontSizePt.HasValue)
            {
                tag.Item4.FontSizePt = fontSizePt.Value;
                _inlineTextBox.FontSize = Math.Max(10, fontSizePt.Value * 0.3528 * View.Scale);
            }
            if (bold.HasValue)
            {
                tag.Item4.Bold = bold.Value;
                _inlineTextBox.FontWeight = bold.Value ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
            }
            if (italic.HasValue)
            {
                tag.Item4.Italic = italic.Value;
                _inlineTextBox.FontStyle = italic.Value ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal;
            }
            if (fontFamily != null)
            {
                tag.Item4.FontFamily = fontFamily;
                _inlineTextBox.FontFamily = new System.Windows.Media.FontFamily(fontFamily);
            }
            _inlineTextBox.Tag = tag;
        }
    }

    private void OnInlineTitleBlockKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Return)
        {
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) && _inlineTextBox is { AcceptsReturn: true })
                return; // Shift+Enter to newline
            CommitInlineTitleBlockEdit();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            RemoveInlineTextBox();
            e.Handled = true;
        }
    }

    private void OnInlineTitleBlockLostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        CommitInlineTitleBlockEdit();
    }

    private void CommitInlineTitleBlockEdit()
    {
        if (_inlineTextBox?.Tag is ValueTuple<string, Page, string, OperCAD.Core.Documents.TitleBlockFieldStyle> tag && tag.Item1 == "TitleBlock")
        {
            var newText = _inlineTextBox.Text;
            var style = tag.Item4;
            TitleBlockFieldCommitted?.Invoke(this, new ValueTuple<string, string, OperCAD.Core.Documents.TitleBlockFieldStyle>(tag.Item3, newText, style));
        }
        RemoveInlineTextBox();
    }

    #endregion

    private Conductor? HitTestConductor(Point2D world, double tolMm)
    {
        if (_page is null)
            return null;
        foreach (var obj in _page.Objects)
        {
            if (obj is not Conductor c)
                continue;
            for (var i = 1; i < c.Path.Count; i++)
            {
                if (DistanceToSegment(world, c.Path[i - 1], c.Path[i]) <= tolMm)
                    return c;
            }
        }
        return null;
    }

    private static double DistanceToSegment(Point2D p, Point2D a, Point2D b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lenSq = abx * abx + aby * aby;
        if (lenSq < 1e-9)
            return p.DistanceTo(a);
        var t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq;
        t = Math.Clamp(t, 0, 1);
        var px = a.X + t * abx;
        var py = a.Y + t * aby;
        return new Point2D(px, py).DistanceTo(p);
    }

    /// <summary>
    /// Encontra o ponto mais próximo na segmento de condutor mais próximo do ponto dado.
    /// Retorna o condutor, o ponto projetado e a distância. Usado para snap em T-connections.
    /// </summary>
    private (Conductor Conductor, Point2D Point, double Distance)? FindNearestConductorPoint(Point2D world, double toleranceMm)
    {
        if (_page is null)
            return null;

        Conductor? bestConductor = null;
        Point2D bestPoint = default;
        double bestDist = toleranceMm;

        foreach (var obj in _page.Objects)
        {
            if (obj is not Conductor c)
                continue;
            for (var i = 1; i < c.Path.Count; i++)
            {
                var a = c.Path[i - 1];
                var b = c.Path[i];
                var dist = DistanceToSegment(world, a, b);
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    bestConductor = c;
                    // Ponto projetado no segmento
                    var abx = b.X - a.X;
                    var aby = b.Y - a.Y;
                    var lenSq = abx * abx + aby * aby;
                    if (lenSq < 1e-9)
                        bestPoint = a;
                    else
                    {
                        var t = ((world.X - a.X) * abx + (world.Y - a.Y) * aby) / lenSq;
                        t = Math.Clamp(t, 0, 1);
                        bestPoint = new Point2D(a.X + t * abx, a.Y + t * aby);
                    }
                }
            }
        }

        return bestConductor is not null
            ? (bestConductor, bestPoint, bestDist)
            : null;
    }

    private void ClearSelection()
    {
        if (_page is null)
            return;
        foreach (var obj in _page.Objects)
            obj.IsSelected = false;
        _cycleEnabled = false;
        _cycleShape = null;
        _canvas.InvalidateVisual();
    }

    public void SelectOnly(DocumentObject obj)
    {
        if (_page is null)
            return;
        foreach (var o in _page.Objects)
            o.IsSelected = o == obj;
        _canvas.InvalidateVisual();
    }

    private void ToggleSelection(DocumentObject obj)
    {
        if (_page is null)
            return;
        obj.IsSelected = !obj.IsSelected;
        _canvas.InvalidateVisual();
    }

    /// <summary>Finaliza a caixa de seleção: seleciona elementos cuja área toca o retângulo.</summary>
    private void FinalizeRubberBand()
    {
        if (_page is null)
            return;
        var rect = new Rect2D(
            Math.Min(_rubberBandStart.X, _rubberBandCurrent.X),
            Math.Min(_rubberBandStart.Y, _rubberBandCurrent.Y),
            Math.Abs(_rubberBandCurrent.X - _rubberBandStart.X),
            Math.Abs(_rubberBandCurrent.Y - _rubberBandStart.Y));
        if (!_rubberBandShift)
            ClearSelection();
        foreach (var obj in _page.Objects)
        {
            // Formas e condutores usam a GEOMETRIA real (contorno/traço): a caixa envolvente
            // selecionava traços distantes cuja caixa cruzava o laço — era isso que trazia
            // "outros traços na mesma linha" junto na cópia.
            if (obj is PlacedElement el && RectIntersects(rect, ElementWorldBounds(el)))
                el.IsSelected = true;
            else if (obj is TextItem t && RectIntersects(rect, TextItemRenderer.ComputeBounds(t)))
                t.IsSelected = true;
            else if (obj is ShapeItem s && s.Points.Count > 0 && ShapeGeometry.TouchesRect(s, rect))
                s.IsSelected = true;
            else if (obj is TerminalStripItem ts && rect.Contains(new Point2D(ts.X, ts.Y)))
                ts.IsSelected = true;
            else if (obj is Conductor c && ShapeGeometry.PolylineTouchesRect(ConductorSegments(c), rect))
                c.IsSelected = true;
        }

        // Expande para os GRUPOS completos: laçar parte de um símbolo importado (ex.: 1 dos
        // 5 contatos do K7.1) seleciona o símbolo inteiro. Ctrl mantém a seleção isolada.
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var groups = _page.Objects
                .Where(o => o.IsSelected)
                .Select(ObjectGroups.GroupIdOf)
                .Where(g => g is not null)
                .Distinct()
                .ToList();
            foreach (var groupId in groups)
                foreach (var member in ObjectGroups.Members(_page, groupId!.Value))
                    member.IsSelected = true;
        }

        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Segmentos do caminho de um condutor, para o teste de laço por geometria.</summary>
    private static List<(Point2D A, Point2D B)> ConductorSegments(Conductor c)
    {
        var segments = new List<(Point2D A, Point2D B)>(Math.Max(0, c.Path.Count - 1));
        for (var i = 1; i < c.Path.Count; i++)
            segments.Add((c.Path[i - 1], c.Path[i]));
        return segments;
    }

    private static Rect2D ElementWorldBounds(PlacedElement el)
        => PageClamping.ElementWorldBounds(el);

    private static bool RectIntersects(Rect2D a, Rect2D b)
        => a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;

    private static int HitTestShapeVertex(ShapeItem shape, Point2D world, double tolerance)
    {
        for (var i = 0; i < shape.Points.Count; i++)
        {
            if (world.DistanceTo(shape.Points[i]) <= tolerance)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Hit-test para handles de ângulo de arco: 0 = início, 1 = fim, -1 = nenhum.
    /// Suporta arco circular e arco de elipse.
    /// </summary>
    private static int HitTestArcAngleHandles(ShapeItem shape, Point2D world, double tolerance)
    {
        if (shape.Kind is not (ShapeKind.Arc or ShapeKind.EllipseArc) || shape.Points.Count < 2)
            return -1;
        var center = shape.Points[0];
        double rx, ry;
        if (shape.Kind == ShapeKind.Arc)
        {
            var r = center.DistanceTo(shape.Points[1]);
            rx = ry = r;
        }
        else
        {
            var q = shape.Points[1];
            rx = Math.Abs(q.X - center.X);
            ry = Math.Abs(q.Y - center.Y);
        }
        var startRad = shape.StartAngle * Math.PI / 180.0;
        var endRad = (shape.StartAngle + shape.SweepAngle) * Math.PI / 180.0;
        var startPos = new Point2D(center.X + rx * Math.Cos(startRad), center.Y + ry * Math.Sin(startRad));
        var endPos = new Point2D(center.X + rx * Math.Cos(endRad), center.Y + ry * Math.Sin(endRad));
        if (world.DistanceTo(startPos) <= tolerance)
            return 0;
        if (world.DistanceTo(endPos) <= tolerance)
            return 1;
        return -1;
    }

    /// <summary>Arredonda a posição mundial para o espaçamento do grid configurado.</summary>
    public (double X, double Y) SnapToGrid(Point2D world)
    {
        if (!IsSnapEnabled)
            return (world.X, world.Y);

        // Ctrl pressionado → posicionamento livre (ignora a grade).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return (world.X, world.Y);

        var grid = Math.Max(Grid.Spacing, 0.1);
        // Ancora o snap ao centro do papel (consistente com a grade visual simétrica).
        var full = _page?.Settings.FullBounds ?? new Rect2D(0, 0, 297, 210);
        var cx = full.MinX + full.Width / 2.0;
        var cy = full.MinY + full.Height / 2.0;
        return (cx + Math.Round((world.X - cx) / grid) * grid,
                cy + Math.Round((world.Y - cy) / grid) * grid);
    }

    /// <summary>
    /// Detecta alinhamentos entre uma bounding box em movimento e outros objetos na folha.
    /// Retorna guias de alinhamento (linhas) e o ponto de snap ajustado.
    /// </summary>
    private Point2D DetectAlignmentGuides(Rect2D movingBounds, Point2D proposedPos, double tolerance)
    {
        _alignmentGuides.Clear();
        if (_page is null) return proposedPos;

        var snapX = proposedPos.X;
        var snapY = proposedPos.Y;
        var bestDx = double.MaxValue;
        var bestDy = double.MaxValue;

        var mCx = movingBounds.CenterX;
        var mCy = movingBounds.CenterY;

        foreach (var obj in _page.Objects)
        {
            if (obj is not ShapeItem s || s.IsLocked) continue;
            if (s.IsSelected) continue; // não alinha com objetos selecionados
            var b = s.Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;

            // Pontos de referência da forma alvo: centros e bordas
            var targetRefsX = new[] { b.CenterX, b.MinX, b.MaxX };
            var targetRefsY = new[] { b.CenterY, b.MinY, b.MaxY };

            // Verifica alinhamento horizontal (centro X)
            foreach (var tx in targetRefsX)
            {
                var d = Math.Abs(mCx - tx);
                if (d < tolerance && d < bestDx)
                {
                    bestDx = d;
                    snapX = proposedPos.X + (tx - mCx);
                    _alignmentGuides.Clear();
                    _alignmentGuides.Add((new Point2D(tx, Math.Min(b.MinY, movingBounds.MinY) - 5),
                                           new Point2D(tx, Math.Max(b.MaxY, movingBounds.MaxY) + 5)));
                }
            }

            // Verifica alinhamento vertical (centro Y)
            foreach (var ty in targetRefsY)
            {
                var d = Math.Abs(mCy - ty);
                if (d < tolerance && d < bestDy)
                {
                    bestDy = d;
                    snapY = proposedPos.Y + (ty - mCy);
                    _alignmentGuides.Clear();
                    _alignmentGuides.Add((new Point2D(Math.Min(b.MinX, movingBounds.MinX) - 5, ty),
                                           new Point2D(Math.Max(b.MaxX, movingBounds.MaxX) + 5, ty)));
                }
            }
        }

        return new Point2D(snapX, snapY);
    }

    /// <summary>
    /// Snap a endpoints de linhas/formas e centros de elipses/retângulos/arcos.
    /// Retorna o ponto mais próximo dentro da tolerância, ou null se nenhum snap encontrado.
    /// </summary>
    private Point2D? FindSnapPoint(Point2D world, double tolerance)
    {
        if (_page is null) return null;
        Point2D? best = null;
        double bestDist = tolerance;

        foreach (var obj in _page.Objects)
        {
            if (obj is ShapeItem s && !s.IsLocked)
            {
                // Endpoint snap: vértices de linhas/polilinhas/formas
                foreach (var pt in s.Points)
                {
                    var d = world.DistanceTo(pt);
                    if (d < bestDist) { bestDist = d; best = pt; }
                }

                // Center snap: centro de elipses, retângulos, retângulos arredondados
                if (s.Kind is ShapeKind.Ellipse or ShapeKind.Rectangle or ShapeKind.RoundedRectangle && s.Points.Count >= 2)
                {
                    var a = s.Points[0]; var b = s.Points[1];
                    var center = new Point2D((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
                    var d = world.DistanceTo(center);
                    if (d < bestDist) { bestDist = d; best = center; }
                }

                // Center snap: arco e arco de elipse (centro armazenado no primeiro ponto)
                if (s.Kind is ShapeKind.Arc or ShapeKind.EllipseArc && s.Points.Count >= 1)
                {
                    var center = s.Points[0];
                    var d = world.DistanceTo(center);
                    if (d < bestDist) { bestDist = d; best = center; }
                }

                // Midpoint snap: ponto médio de linhas
                if (s.Kind == ShapeKind.Line && s.Points.Count >= 2)
                {
                    var mid = new Point2D((s.Points[0].X + s.Points[1].X) / 2.0,
                                          (s.Points[0].Y + s.Points[1].Y) / 2.0);
                    var d = world.DistanceTo(mid);
                    if (d < bestDist) { bestDist = d; best = mid; }
                }
            }

            // Endpoint snap: terminais de elementos
            if (obj is PlacedElement el)
            {
                foreach (var t in el.Definition.Terminals)
                {
                    var pos = TerminalGeometry.WorldPosition(el, t);
                    var d = world.DistanceTo(pos);
                    if (d < bestDist) { bestDist = d; best = pos; }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Aplica restrição angular (snap de 5°) a um ponto em relação a uma origem.
    /// Usado durante desenho de linhas e arcos para manter ângulos precisos.
    /// Segure Shift para desenhar livremente (sem restrição).
    /// </summary>
    private Point2D ApplyAngularConstraint(Point2D origin, Point2D current)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return current;

        return DrawingPointRules.SnapAngle(origin, current, DrawingPointRules.AngleStepDegrees);
    }

    // ── Área de desenho (espaço INTERNO da moldura) ─────────────────────
    //
    // A folha tem: a página inteira (papel), a moldura (recuada da borda) e a ÁREA DE
    // DESENHO — a parte interna da moldura, descontando as faixas de letras/números e a
    // legenda. Os elementos só podem viver na ÁREA DE DESENHO: nunca nas margens externas
    // (além da moldura) nem sobre a própria moldura. Todo posicionamento, arraste, colagem
    // e movimentação é limitado a esse espaço.

    /// <summary>Área de desenho efetiva da folha (espaço interno da moldura).</summary>
    private Rect2D DrawingAreaBounds()
    {
        if (_page is null)
            return new Rect2D(0, 0, 297, 210);
        return PageClamping.DrawingArea(_page.Settings);
    }

    /// <summary>Limita um ponto (ancoragem) à área de desenho da folha.</summary>
    private Point2D ClampPointToDrawingArea(Point2D p)
        => PageClamping.ClampPoint(DrawingAreaBounds(), p);

    /// <summary>
    /// FONTE ÚNICA de verdade para a posição de colocação de um elemento:
    /// O cursor controla diretamente o HOTSPOT do símbolo (âncora).
    /// O hotspot é limitado à área de desenho e, em seguida, alinhado ao grid.
    /// </summary>
    private (double X, double Y) ComputePlacement(ElementDefinition def, Point2D world)
    {
        var area = DrawingAreaBounds();
        double clampedX = Math.Clamp(world.X, area.MinX, area.MaxX);
        double clampedY = Math.Clamp(world.Y, area.MinY, area.MaxY);
        return SnapToGrid(new Point2D(clampedX, clampedY));
    }

    /// <summary>
    /// Posição do HOTSPOT no ponto dado, SEM limitar à área de desenho — usado pelo
    /// FANTASMA. O hotspot fica no cursor mesmo que esteja sobre a moldura.
    /// </summary>
    private (double X, double Y) ComputePlacementUnclamped(ElementDefinition def, Point2D world)
    {
        return SnapToGrid(world);
    }

    /// <summary>
    /// Limita apenas o HOTSPOT (âncora) do elemento à área de desenho — o corpo pode
    /// se estender além das bordas. O clamping anterior
    /// forçava o corpo INTEIRO a ficar dentro, impedindo posicionamento perto das bordas.
    /// </summary>
    private (double X, double Y) ClampHotspotToDrawingArea(double x, double y)
    {
        var area = DrawingAreaBounds();
        return (Math.Clamp(x, area.MinX, area.MaxX), Math.Clamp(y, area.MinY, area.MaxY));
    }

    /// <summary>
    /// Reposiciona um elemento JÁ colocado para que o hotspot fique dentro da área de
    /// desenho (apenas o hotspot é limitado, não o corpo).
    /// Usado ao arrastar/mover/colar.
    /// </summary>
    private void ClampPlacedElement(PlacedElement el)
    {
        var area = DrawingAreaBounds();
        var nx = Math.Clamp(el.X, area.MinX, area.MaxX);
        var ny = Math.Clamp(el.Y, area.MinY, area.MaxY);
        if (Math.Abs(nx - el.X) > 1e-9 || Math.Abs(ny - el.Y) > 1e-9)
        {
            el.X = nx;
            el.Y = ny;
        }
    }

    /// <summary>
    /// Traz o conjunto colado para dentro da ÁREA DE DESENHO deslocando-o por INTEIRO — nunca
    /// objeto por objeto. Mover peças isoladas deformava a disposição relativa dos traços (o
    /// conjunto copiado serve para reutilizar um desenho: copiar, colar, girar e mover).
    /// </summary>
    private void ClampPastedSetIntoDrawingArea(IReadOnlyList<DocumentObject> created)
    {
        if (created.Count == 0)
            return;

        if (BoundsOfSet(created) is not { } bounds)
            return;

        var a = DrawingAreaBounds();
        var dx = bounds.MinX < a.MinX ? a.MinX - bounds.MinX
            : bounds.MaxX > a.MaxX ? a.MaxX - bounds.MaxX : 0;
        var dy = bounds.MinY < a.MinY ? a.MinY - bounds.MinY
            : bounds.MaxY > a.MaxY ? a.MaxY - bounds.MaxY : 0;

        if (dx == 0 && dy == 0)
            return;

        foreach (var obj in created)
            TranslateObject(obj, dx, dy);
    }

    /// <summary>
    /// O conjunto colado passa a ser UM GRUPO quando os objetos não pertenciam a nenhum — assim o
    /// símbolo colado pode ser selecionado, movido e girado inteiro, sem perder a forma. Grupos
    /// vindos da origem são preservados (o clipboard já os copia como grupos novos).
    /// </summary>
    private static void GroupPastedSet(IReadOnlyList<DocumentObject> created)
    {
        var groupable = created.Where(ObjectGroups.IsGroupable).ToList();
        if (groupable.Count < 2 || groupable.Any(o => ObjectGroups.GroupIdOf(o) is not null))
            return;

        var groupId = Guid.NewGuid();
        foreach (var obj in groupable)
            ObjectGroups.SetGroupId(obj, groupId);
    }

    /// <summary>Retângulo envolvente de um conjunto de objetos de tipos diferentes.</summary>
    private Rect2D? BoundsOfSet(IReadOnlyList<DocumentObject> objects)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var any = false;

        void Add(double x, double y, double w, double h)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + w);
            maxY = Math.Max(maxY, y + h);
            any = true;
        }

        foreach (var obj in objects)
        {
            switch (obj)
            {
                case ShapeItem s when s.Points.Count > 0:
                    var b = s.Bounds;
                    Add(b.MinX, b.MinY, b.Width, b.Height);
                    break;
                case TextItem t:
                    Add(t.X, t.Y, 0, 0);
                    break;
                case PlacedElement el:
                    Add(el.X, el.Y, 0, 0);
                    break;
                case ImageItem img:
                    Add(img.X, img.Y, 0, 0);
                    break;
                case TerminalStripItem item:
                    Add(item.X, item.Y, 0, 0);
                    break;
                case Conductor c when c.Path.Count > 0:
                    var cx = c.Path.Min(p => p.X);
                    var cy = c.Path.Min(p => p.Y);
                    Add(cx, cy, c.Path.Max(p => p.X) - cx, c.Path.Max(p => p.Y) - cy);
                    break;
            }
        }

        return any ? new Rect2D(minX, minY, maxX - minX, maxY - minY) : null;
    }

    /// <summary>Transladar um objeto de qualquer tipo (usado na colagem em conjunto).</summary>
    private static void TranslateObject(DocumentObject obj, double dx, double dy)
    {
        switch (obj)
        {
            case ShapeItem s:
                s.Translate(dx, dy);
                break;
            case TextItem t:
                t.X += dx;
                t.Y += dy;
                break;
            case PlacedElement el:
                el.X += dx;
                el.Y += dy;
                break;
            case ImageItem img:
                img.X += dx;
                img.Y += dy;
                break;
            case TerminalStripItem item:
                item.X += dx;
                item.Y += dy;
                break;
            case Conductor c:
                for (var i = 0; i < c.Path.Count; i++)
                    c.Path[i] = new Point2D(c.Path[i].X + dx, c.Path[i].Y + dy);
                break;
        }
    }

    /// <summary>Agrupa os textos selecionados em um único elemento de texto.</summary>
    public void GroupSelectedTexts()
    {
        if (_page is null)
            return;
        var selectedTexts = _page.Objects.OfType<TextItem>().Where(t => t.IsSelected).ToList();
        if (selectedTexts.Count < 2)
            return;

        // Combina os textos com quebra de linha e usa a posição do primeiro texto
        var combinedText = string.Join(Environment.NewLine, selectedTexts.Select(t => t.Text));
        var first = selectedTexts[0];
        var groupedText = new TextItem
        {
            Text = combinedText,
            X = first.X,
            Y = first.Y,
            FontSizePt = first.FontSizePt,
            FontFamily = first.FontFamily,
            Bold = first.Bold,
            Color = first.Color
        };

        // Remove os textos selecionados e adiciona o texto agrupado
        foreach (var t in selectedTexts)
            _page.RemoveObject(t);
        _page.Add(groupedText);
        groupedText.IsSelected = true;

        UndoStack?.Push(new AddObjectsCommand(_page, new[] { groupedText }, "Agrupar textos"));
        EditStateChanged?.Invoke(this, EventArgs.Empty);
        _canvas.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adiciona uma coluna à grade da folha.</summary>
    public void AddGridColumn()
    {
        if (_page is not null)
        {
            _page.Settings.ColumnsCount++;
            _canvas.InvalidateVisual();
            NotifyViewChanged();
        }
    }

    /// <summary>Remove uma coluna da grade da folha.</summary>
    public void RemoveGridColumn()
    {
        if (_page is not null && _page.Settings.ColumnsCount > 1)
        {
            _page.Settings.ColumnsCount--;
            _canvas.InvalidateVisual();
            NotifyViewChanged();
        }
    }

    /// <summary>Adiciona uma linha à grade da folha.</summary>
    public void AddGridRow()
    {
        if (_page is not null)
        {
            _page.Settings.RowsCount++;
            _canvas.InvalidateVisual();
            NotifyViewChanged();
        }
    }

    /// <summary>Remove uma linha da grade da folha.</summary>
    public void RemoveGridRow()
    {
        if (_page is not null && _page.Settings.RowsCount > 1)
        {
            _page.Settings.RowsCount--;
            _canvas.InvalidateVisual();
            NotifyViewChanged();
        }
    }

    private void NotifyViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);
}
