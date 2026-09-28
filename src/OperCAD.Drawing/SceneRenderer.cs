using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using OperCAD.Core.TerminalStrips;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>Modo do cursor retículo (menu Exibir → Cursor): linhas inteiras pela área de
/// desenho ou semi-retas curtas (discretas) ao redor do cursor.</summary>
public enum CrosshairMode { Full, Short }

/// <summary>Transformação de visualização (escala e deslocamento) entre mundo e tela.</summary>
public readonly record struct ViewTransform(double Scale, double OffsetX, double OffsetY)
{
    public const double MinScale = 0.001;
    public const double MaxScale = 1000.0;

    public static ViewTransform Identity => new(1, 0, 0);

    public Point2D WorldToScreen(Point2D world)
        => new(OffsetX + world.X * Scale, OffsetY - world.Y * Scale);

    public Point2D ScreenToWorld(Point2D screen)
        => new((screen.X - OffsetX) / Scale, (OffsetY - screen.Y) / Scale);

    public ViewTransform Pan(double screenDx, double screenDy)
        => new(Scale, OffsetX + screenDx, OffsetY + screenDy);

    public ViewTransform ZoomAt(double factor, Point2D screenAnchor)
    {
        var newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - Scale) < 1e-9)
            return this;
        var world = ScreenToWorld(screenAnchor);
        return new ViewTransform(newScale, screenAnchor.X - world.X * newScale, screenAnchor.Y + world.Y * newScale);
    }

    public ViewTransform FitToView(Rect2D worldBounds, double viewportWidth, double viewportHeight, double padding)
    {
        var usableW = Math.Max(viewportWidth - padding * 2, 1);
        var usableH = Math.Max(viewportHeight - padding * 2, 1);
        var scale = Math.Clamp(Math.Min(usableW / worldBounds.Width, usableH / worldBounds.Height), MinScale, MaxScale);
        var offsetX = viewportWidth / 2 - worldBounds.CenterX * scale;
        var offsetY = viewportHeight / 2 + worldBounds.CenterY * scale;
        return new ViewTransform(scale, offsetX, offsetY);
    }
}

/// <summary>
/// Renderizador da cena: limpa a área, desenha o grid, a folha (página branca com
/// sombra e borda) e a moldura técnica com a legenda.
/// Grid de um nível (10mm).
/// </summary>
public static class SceneRenderer
{
    private static readonly SKPaint GridFinePaint = CreatePaint("#C8C8CC", 0.5f, stroke: true);
    private static readonly SKPaint GridFineDotPaint = CreatePaint("#1A1A1A", 0, fill: true);
    private static readonly SKPaint PageShadowPaint = CreatePaint("#26000000", 0, fill: true);
    private static readonly SKPaint PageBorderPaint = CreatePaint("#B0B0B0", 1.5f, stroke: true);

    /// <summary>Opções de renderização.</summary>
    public sealed record RenderOptions(
        bool DrawFrame = true,
        bool DrawTitleBlock = true,
        bool ColoredConductors = true,
        bool DrawTerminals = true,
        bool DrawSelectionFrames = true,
        float MinStrokePx = ShapeRenderer.DefaultMinStrokePx);

    public static void Render(
        SKCanvas canvas,
        Page? page,
        ViewTransform view,
        GridSettings grid,
        double viewportWidth,
        double viewportHeight,
        IReadOnlyList<Page>? projectPages = null,
        RenderOptions? options = null,
        IReadOnlyList<TerminalStrip>? terminalStrips = null,
        string? folhaAutoNumFormula = null,
        bool greyBackground = false)
    {
        canvas.Clear(SKColors.White);

        if (page is null)
            return;

        ElementRenderer.UpdateScale(page.Settings.Width, page.Settings.ElementScale);

        var opts = options ?? new RenderOptions();

        var pageRect = ToScreen(page.Settings.FullBounds, view);
        var shadow = pageRect;
        shadow.Offset(4, 4);
        canvas.DrawRect(shadow, PageShadowPaint);
        var fillHex = greyBackground ? "#D9D9D9" : page.Settings.BackgroundColor;
        using var pageFillPaint = new SKPaint { Color = SKColor.Parse(fillHex), IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawRect(pageRect, pageFillPaint);
        // A página importada já contém sua moldura como geometria editável;
        // não desenhar uma segunda borda nativa sobre ela.
        if (page.Settings.ShowFrame)
            canvas.DrawRect(pageRect, PageBorderPaint);

        // Grid multi-nível (depois do preenchimento da folha)
        if (grid.IsVisible)
            DrawGridMultiLevel(canvas, page, view, grid, grid.Style);

        if (opts.DrawFrame)
            DrawingFrameRenderer.DrawFrame(canvas, page, view, projectPages, folhaAutoNumFormula);
        if (opts.DrawTitleBlock)
            DrawingFrameRenderer.DrawTitleBlock(canvas, page, view, projectPages, folhaAutoNumFormula);

        // ⭐ Clipping visual: elementos ficam dentro da ÁREA DE DESENHO (espaço interno
        // da moldura, descontando faixas de letras e bloco de legenda).
        var drawingArea = PageClamping.DrawingArea(page.Settings);
        var drawingScreenBounds = ToScreen(drawingArea, view);

        canvas.Save();
        canvas.ClipRect(drawingScreenBounds, SKClipOperation.Intersect, true);

        // Renderiza elementos, formas, textos e imagens DENTRO do clipping (dentro da folha)
        // Respeita visibilidade de camadas: objetos em camadas invisíveis não são renderizados.
        foreach (var obj in page.Objects)
        {
            if (obj.LayerId is { } layerId)
            {
                var layer = page.Layers.FirstOrDefault(l => l.Id == layerId);
                if (layer is not null && !layer.IsVisible)
                    continue;
            }

            switch (obj)
            {
                case PlacedElement placed:
                    ElementRenderer.RenderPlaced(canvas, placed, view);
                    break;
                case ShapeItem shape:
                    ShapeRenderer.Render(canvas, shape, view, opts.MinStrokePx);
                    break;
                case TextItem text:
                    TextItemRenderer.Render(canvas, text, view);
                    break;
                case ImageItem image:
                    ImageItemRenderer.Render(canvas, image, view);
                    break;
            }
        }

        // Condutores também dentro do clipping
        var conductors = page.Objects.OfType<Conductor>().ToList();

        // Pré-calcula todos os jumps para cada condutor (cruzamentos sem conexão)
        var allSegments = ConductorRenderer.BuildSegments(conductors);
        var jumpsByConductor = new Dictionary<int, List<ConductorRenderer.JumpInfo>>();
        for (int ci = 0; ci < conductors.Count; ci++)
        {
            var jumps = ConductorRenderer.FindJumpsForConductor(ci, conductors, allSegments);
            if (jumps.Count > 0)
                jumpsByConductor[ci] = jumps;
        }

        // Renderiza cada condutor com seus jumps
        for (int ci = 0; ci < conductors.Count; ci++)
        {
            var cond = conductors[ci];
            jumpsByConductor.TryGetValue(ci, out var jumps);
            ConductorRenderer.RenderConductor(canvas, cond, view,
                forceBlack: !opts.ColoredConductors,
                fromSnap: TerminalWorldPosition(page, cond.From),
                toSnap: TerminalWorldPosition(page, cond.To),
                jumps: jumps);
        }

        ConductorRenderer.RenderJunctions(canvas, conductors, view);

        canvas.Restore();  // ← Remove clipping

        // Terminal strips (bornes) podem ficar fora do clipping se necessário
        if (opts.DrawTerminals && terminalStrips is not null)
        {
            foreach (var obj in page.Objects)
            {
                if (obj is TerminalStripItem item)
                {
                    var strip = terminalStrips.FirstOrDefault(s => s.Uuid == item.StripUuid);
                    TerminalStripItemRenderer.Render(canvas, item, strip, view);
                }
            }
        }

        if (opts.DrawSelectionFrames)
        {
            // Caixa/alças só com UMA forma selecionada; em seleção múltipla destaca-se apenas a
            // própria geometria (evita a aglomeração de caixas quando há muitas linhas juntas).
            var selectedShapeCount = page.Objects.OfType<ShapeItem>().Count(s => s.IsSelected);
            foreach (var obj in page.Objects)
            {
                if (obj is PlacedElement placed && placed.IsSelected)
                    ElementRenderer.RenderSelectionFrame(canvas, placed, view);
                else if (obj is ShapeItem shape && shape.IsSelected)
                    ShapeRenderer.RenderSelectionFrame(canvas, shape, view,
                        showHandles: selectedShapeCount == 1);
                else if (obj is TextItem text && text.IsSelected)
                    TextItemRenderer.RenderSelectionFrame(canvas, text, view);
            }
        }

        foreach (var obj in page.Objects)
        {
            if (obj is PlacedElement pe && pe.IsReport && !string.IsNullOrEmpty(pe.Label))
            {
                var text = projectPages is null
                    ? page.ConvertPosition(new Point2D(pe.X, pe.Y))
                    : ProjectXref.ReferenceText(projectPages, pe);
                ElementRenderer.RenderFolioRef(canvas, pe, view, ElementNameTranslator.Translate(text));
            }
        }
    }

    /// <summary>
    /// Desenha o grid multi-nível adaptativo: fino (2.5mm), médio (10mm), grosso (50mm).
    /// Níveis são mostrados/ocultados conforme o zoom para manter legibilidade.
    /// </summary>
    private static void DrawGridMultiLevel(SKCanvas canvas, Page page, ViewTransform view, GridSettings grid, GridVisualStyle style)
    {
        var bounds = page.Settings.FolhaTemplate is { } tpl
            ? FolhaTemplateRenderer.DrawingArea(tpl, page.Settings)
            : page.Settings.DrawingBounds;

        // Centro do papel: ancora a grade simetricamente (margens iguais em lados opostos).
        var full = page.Settings.FullBounds;
        var center = new Point2D(full.MinX + full.Width / 2.0, full.MinY + full.Height / 2.0);

        var clipTL = view.WorldToScreen(new Point2D(bounds.MinX, bounds.MaxY));
        var clipBR = view.WorldToScreen(new Point2D(bounds.MaxX, bounds.MinY));
        var clipRect = SKRect.Create(
            (float)clipTL.X, (float)clipTL.Y,
            (float)(clipBR.X - clipTL.X), (float)(clipBR.Y - clipTL.Y));

        canvas.Save();
        canvas.ClipRect(clipRect, SKClipOperation.Intersect, true);

        // Grid único (nível fino, 10mm), com pontos bem visíveis.
        var step = grid.FineSpacing;
        var pixels = step * view.Scale;

        // Oculta o grid apenas em zoom muito afastado (abaixo de ~0.5x).
        if (pixels >= 5.0)
        {
            if (style == GridVisualStyle.Dots)
                DrawGridLevelDots(canvas, bounds, center, view, step, GridFineDotPaint, 0.5f);
            else
                DrawGridLevelLines(canvas, bounds, center, view, step, GridFinePaint);
        }

        canvas.Restore();
    }

    /// <summary>Desenha pontos do grid em um nível específico (centralizado no centro do papel).</summary>
    private static void DrawGridLevelDots(SKCanvas canvas, Rect2D bounds, Point2D center, ViewTransform view, double step, SKPaint paint, float baseRadius)
    {
        // Pontinhos pretos, pequenos e sempre visíveis: tamanho constante em pixels de
        // tela (independente do zoom), como os nós do elemento "Condutor".
        var dotRadius = Math.Max(0.5f, baseRadius);
        var startX = center.X - Math.Floor((center.X - bounds.MinX) / step) * step;
        var startY = center.Y - Math.Floor((center.Y - bounds.MinY) / step) * step;
        for (var x = startX; x <= bounds.MaxX + step * 1e-6; x += step)
        {
            for (var y = startY; y <= bounds.MaxY + step * 1e-6; y += step)
            {
                var sp = view.WorldToScreen(new Point2D(x, y));
                canvas.DrawCircle((float)sp.X, (float)sp.Y, (float)dotRadius, paint);
            }
        }
    }

    /// <summary>Desenha linhas do grid em um nível específico (centralizado no centro do papel).</summary>
    private static void DrawGridLevelLines(SKCanvas canvas, Rect2D bounds, Point2D center, ViewTransform view, double step, SKPaint paint)
    {
        var clipTL = view.WorldToScreen(new Point2D(bounds.MinX, bounds.MaxY));
        var clipBR = view.WorldToScreen(new Point2D(bounds.MaxX, bounds.MinY));
        var left = clipTL.X;
        var right = clipBR.X;
        var top = clipTL.Y;
        var bottom = clipBR.Y;

        var startX = center.X - Math.Floor((center.X - bounds.MinX) / step) * step;
        for (var x = startX; x <= bounds.MaxX + step * 1e-6; x += step)
        {
            var sx = (float)view.WorldToScreen(new Point2D(x, 0)).X;
            canvas.DrawLine(sx, (float)top, sx, (float)bottom, paint);
        }
        var startY = center.Y - Math.Floor((center.Y - bounds.MinY) / step) * step;
        for (var y = startY; y <= bounds.MaxY + step * 1e-6; y += step)
        {
            var sy = (float)view.WorldToScreen(new Point2D(0, y)).Y;
            canvas.DrawLine((float)left, sy, (float)right, sy, paint);
        }
    }

    /// <summary>
    /// Desenha um retículo (reticle) no cursor: quatro braços curtos com gap central
    /// e um círculo vazio no centro. Não cruza a folha toda — fica discreto em prints.
    /// </summary>
    public static void DrawCrosshair(SKCanvas canvas, Point2D cursorWorld, ViewTransform view, Rect2D worldBounds)
    {
        var screen = view.WorldToScreen(cursorWorld);
        float cx = (float)screen.X;
        float cy = (float)screen.Y;

        // Dimensões fixas em pixels de tela (independem do zoom)
        const float Gap     = 7f;   // raio do gap entre o círculo e o início dos braços
        const float ArmLen  = 14f;  // comprimento de cada braço além do gap
        const float CircleR = 4f;   // raio do círculo central
        const float StrokeW = 1.2f;

        using var paint = new SKPaint
        {
            Color        = new SKColor(30, 130, 230, 210),
            StrokeWidth  = StrokeW,
            IsAntialias  = true,
            Style        = SKPaintStyle.Stroke
        };

        // Braço superior
        canvas.DrawLine(cx, cy - Gap, cx, cy - Gap - ArmLen, paint);
        // Braço inferior
        canvas.DrawLine(cx, cy + Gap, cx, cy + Gap + ArmLen, paint);
        // Braço esquerdo
        canvas.DrawLine(cx - Gap, cy, cx - Gap - ArmLen, cy, paint);
        // Braço direito
        canvas.DrawLine(cx + Gap, cy, cx + Gap + ArmLen, cy, paint);
        // Círculo central
        canvas.DrawCircle(cx, cy, CircleR, paint);
    }

    /// <summary>Desenha o retículo conforme o modo: <see cref="CrosshairMode.Full"/> cruza a área
    /// de desenho inteira com duas linhas; <see cref="CrosshairMode.Short"/> usa semi-retas curtas
    /// (comportamento padrão, discreto).</summary>
    public static void DrawCrosshair(SKCanvas canvas, Point2D cursorWorld, ViewTransform view,
        Rect2D worldBounds, CrosshairMode mode)
    {
        if (mode == CrosshairMode.Full)
        {
            var screen = view.WorldToScreen(cursorWorld);
            float cx = (float)screen.X;
            float cy = (float)screen.Y;
            var tl = view.WorldToScreen(new Point2D(worldBounds.MinX, worldBounds.MaxY));
            var br = view.WorldToScreen(new Point2D(worldBounds.MaxX, worldBounds.MinY));

            using var paint = new SKPaint
            {
                Color       = new SKColor(30, 130, 230, 210),
                StrokeWidth = 1.0f,
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke
            };
            canvas.DrawLine((float)tl.X, cy, (float)br.X, cy, paint);
            canvas.DrawLine(cx, (float)tl.Y, cx, (float)br.Y, paint);
            canvas.DrawCircle(cx, cy, 4f, paint);
            return;
        }

        DrawCrosshair(canvas, cursorWorld, view, worldBounds);
    }


    /// <summary>
    /// Desenha highlight no terminal sob o cursor: círculo alaranjado com anel externo.
    /// </summary>
    public static void DrawTerminalHighlight(SKCanvas canvas, Point2D terminalScreenPos, ViewTransform view, bool isStartTerminal = false)
    {
        var radius = Math.Max(5.0f, (float)(3.0 * view.Scale));
        var ringWidth = Math.Max(1.5f, (float)(1.0 * view.Scale));

        using var ring = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = isStartTerminal ? new SKColor(76, 175, 80) : new SKColor(255, 152, 0),
            StrokeWidth = ringWidth,
            IsAntialias = true
        };
        using var dot = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = isStartTerminal ? new SKColor(76, 175, 80) : new SKColor(255, 152, 0),
            IsAntialias = true
        };

        canvas.DrawCircle((float)terminalScreenPos.X, (float)terminalScreenPos.Y, radius, ring);
        canvas.DrawCircle((float)terminalScreenPos.X, (float)terminalScreenPos.Y, radius * 0.35f, dot);
    }

    /// <summary>
    /// Desenha highlight de hover em um elemento: retângulo azul translúcido.
    /// </summary>
    public static void DrawElementHover(SKCanvas canvas, PlacedElement el, ViewTransform view)
    {
        var b = ElementRenderer.ComputeBounds(el, el.Label).Inflate(1.0);
        var corners = new[]
        {
            new Point2D(b.MinX, b.MinY), new Point2D(b.MaxX, b.MinY),
            new Point2D(b.MinX, b.MaxY), new Point2D(b.MaxX, b.MaxY)
        };

        var matrix = ElementRenderer.BuildMatrixPublic(el.Definition, el.X, el.Y, el.Rotation, view);
        canvas.Save();
        canvas.Concat(ref matrix);

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0, 120, 215, 25),
            IsAntialias = true
        };
        var rect = SKRect.Create((float)b.MinX, (float)b.MinY, (float)b.Width, (float)b.Height);
        canvas.DrawRect(rect, paint);

        canvas.Restore();
    }

    private static Point2D ToScreen(Point2D world, ViewTransform view) => view.WorldToScreen(world);

    /// <summary>
    /// Posição (mundo) EXATA do terminal de um elemento ao qual o condutor está conectado.
    /// Retorna null quando a ponta é livre (sem terminal). Usado para desenhar o condutor
    /// encostado no terminal (sem "abertura") mesmo se o Path salvo ficou deslocado.
    /// </summary>
    private static Point2D? TerminalWorldPosition(Page page, ElementTerminalRef r)
    {
        if (r is null || !r.IsConnected)
            return null;
        var el = page.Objects.OfType<PlacedElement>().FirstOrDefault(e => e.Id == r.ElementId);
        if (el is null || r.TerminalIndex < 0 || r.TerminalIndex >= el.Definition.Terminals.Count)
            return null;
        return TerminalGeometry.WorldPosition(el, el.Definition.Terminals[r.TerminalIndex]);
    }

    private static SKRect ToScreen(Rect2D world, ViewTransform view)
    {
        var topLeft = view.WorldToScreen(new Point2D(world.MinX, world.MaxY));
        var bottomRight = view.WorldToScreen(new Point2D(world.MaxX, world.MinY));
        return SKRect.Create((float)topLeft.X, (float)topLeft.Y, (float)(bottomRight.X - topLeft.X), (float)(bottomRight.Y - topLeft.Y));
    }

    private static SKPaint CreatePaint(string hex, float width, bool stroke = false, bool fill = false)
    {
        var paint = new SKPaint { Color = SKColor.Parse(hex), IsAntialias = true };
        if (stroke) paint.Style = SKPaintStyle.Stroke;
        else if (fill) paint.Style = SKPaintStyle.Fill;
        paint.StrokeWidth = width;
        return paint;
    }
}
