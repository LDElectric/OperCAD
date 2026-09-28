using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>
/// Renderiza elementos da biblioteca em coordenadas locais do modelo OPCAD.
/// As unidades do símbolo já são as unidades do mundo da folha; não há
/// conversão residual de outros formatos para o editor.
/// </summary>
public static class ElementRenderer
{
    /// <summary>1 ponto tipográfico em mm (1pt = 1/72 pol).</summary>
    public const double PtToMm = 25.4 / 72.0;

    /// <summary>
    /// Escala do sistema local do elemento para o mundo da folha. A escala EFETIVA de cada
    /// elemento vem de <see cref="ElementDefinition.Scale"/> (a biblioteca nativa usa
    /// milímetros, 1 unidade = 1 mm). Este fator global é
    /// apenas um multiplicador adicional (sempre 1.0 no estado atual).
    /// </summary>
    public static double LocalToWorldScale { get; private set; } = 1.0;

    /// <summary>Fator DPI do canvas (physical pixels / DIPs). Usado para que o BuildMatrix
    /// produza coordenadas em pixels físicos (SetMatrix substitui a correção DPI feita em
    /// OnPaintSurface, então o elemento ficaria deslocado se o worldToScreen usasse DIPs).</summary>
    public static double CanvasDpiX { get; private set; } = 1.0;
    public static double CanvasDpiY { get; private set; } = 1.0;

    /// <summary>Mínimos em PIXELS DE TELA usados para manter símbolos legíveis em qualquer
    /// escala (equivalentes aos mínimos cosméticos do estilo CAD). O elemento é desenhado em
    /// coordenadas locais e escalado pela matriz; quando a escala efetiva (ElementScale × zoom)
    /// encolhe muito, aplica-se um piso de tamanho de tela para linhas, texto e terminais.</summary>
    private const float MinStrokePixels = 0.6f;
    private const float MinTextPixels = 2.5f;
    private const float MinTerminalPixels = 1.5f;

    /// <summary>Escala efetiva (pixels de tela por unidade local) do elemento sendo renderizado.
    /// Preenchido a cada chamada de <see cref="Render"/>; usado por <see cref="StrokePaint"/> e
    /// <see cref="DrawText"/> para clamar o tamanho mínimo de tela. Seguro porque o render é
    /// executado em thread única (UI).</summary>
    private static double CurrentScreenScale = 1.0;

    /// <summary>Rotação do elemento atual sendo renderizado (graus).
    /// Usado para manter textos dinâmicos com KeepVisualRotation na vertical.</summary>
    private static double CurrentElementRotation = 0.0;

    /// <summary>Atualiza o fator DPI do canvas (chamado de OnPaintSurface).</summary>
    public static void UpdateDpi(double dpiX, double dpiY)
    {
        CanvasDpiX = dpiX;
        CanvasDpiY = dpiY;
    }

    /// <summary>
    /// Atualiza as escalas do sistema para o mundo da folha. A escala EFETIVA de cada
    /// elemento é <see cref="ElementDefinition.GlobalScale"/> × a escala por-definição
    /// (a biblioteca .opcad já é nativa em mm, 1 unidade = 1 mm). O <paramref name="elementScale"/>
    /// vem da configuração da folha (Padrão = 1.0; ex.: 0.8, 1.2) e é aplicado de forma
    /// consistente no render, na geometria dos terminais e nos condutores.
    /// </summary>
    public static void UpdateScale(double pageWidthMm, double elementScale = 1.0)
    {
        LocalToWorldScale = 1.0;
        ElementDefinition.GlobalScale = elementScale;
    }

    /// <summary>Escala efetiva (mm por unidade do arquivo) de uma definição de elemento,
    /// incluindo o ajuste global de tamanho (<see cref="ElementDefinition.GlobalScale"/>).</summary>
    public static double ElementScale(ElementDefinition def)
        => def.Scale * ElementDefinition.GlobalScale * LocalToWorldScale;

    /// <summary>Escala efetiva de uma INSTÂNCIA posicionada, incluindo a escala de inserção
    /// (zoom CAD, <see cref="PlacedElement.Scale"/>) além da escala da definição e do ajuste
    /// global de tamanho. Deve bater com <see cref="TerminalGeometry"/>.</summary>
    public static double ElementScale(PlacedElement el)
        => el.Definition.Scale * el.Scale * ElementDefinition.GlobalScale * LocalToWorldScale;

    // Espessuras de traço (em mm, na unidade do mundo OPCAD): thin→cosmético, normal→0,29, hight→0,57, eleve→1,43.
    private static float LineWidthMm(ElementLineWeight w) => w switch
    {
        ElementLineWeight.Thin => 0.07f,
        ElementLineWeight.Medium => 0.57f,
        ElementLineWeight.Thick => 1.43f,
        _ => 0.29f
    };

    /// <summary>Desenha um elemento posicionado na folha.</summary>
    public static void RenderPlaced(SKCanvas canvas, PlacedElement el, ViewTransform view)
        => Render(canvas, el.Definition, el.X, el.Y, el.Rotation, view, el.Label, el.Scale, el.TextOverrides);

    /// <summary>
    /// Desenha um elemento com o hotspot em (x, y) no mundo, com rotação (graus,
    /// convenção padrão CAD: 90 = ¼ de volta no sentido horário na tela).
    /// </summary>
    public static void Render(
        SKCanvas canvas,
        ElementDefinition def,
        double x,
        double y,
        double rotation,
        ViewTransform view,
        string? label = null,
        double extraScale = 1.0,
        System.Collections.Generic.IReadOnlyDictionary<int, string>? overrides = null)
    {
        if (def.Primitives.Count == 0 && def.Terminals.Count == 0)
            return;

        var matrix = BuildMatrix(def, x, y, rotation, view, extraScale);
        CurrentScreenScale = ElementScale(def) * extraScale * view.Scale;
        CurrentElementRotation = rotation;
        canvas.Save();
        canvas.Concat(ref matrix);

        for (var i = 0; i < def.Primitives.Count; i++)
        {
            var prim = def.Primitives[i];
            if (overrides is { } ov && ov.TryGetValue(i, out var text) &&
                prim is ElementText t && (prim is not ElementDynamicText d || d.TextFrom == "UserText"))
            {
                prim = t with { Text = text };
            }
            DrawPrimitive(canvas, prim, label);
        }

        // Terminais: pequenos círculos nos pontos de conexão (em coord. locais — a escala já está na matriz)
        foreach (var t in def.Terminals)
        {
            var dotRadius = TerminalDotRadiusUnits;
            // Clamp de tela: garante um piso visível mesmo quando o símbolo é muito pequeno.
            var dotScreenMinLocal = MinTerminalPixels / CurrentScreenScale;
            if (dotRadius < dotScreenMinLocal)
                dotRadius = (float)dotScreenMinLocal;
            using var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColor.Parse("#000000"), IsAntialias = true };
            canvas.DrawCircle((float)t.X, (float)t.Y, dotRadius, paint);
        }

        canvas.Restore();
    }



    /// <summary>Gera um bitmap de preview do elemento (para a paleta).</summary>
    public static SKBitmap RenderPreview(ElementDefinition def, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);

            var bounds = ComputeBounds(def);
            var bw = Math.Max(bounds.MaxX - bounds.MinX, 1);
            var bh = Math.Max(bounds.MaxY - bounds.MinY, 1);
            var scale = (float)Math.Min((width - 12) / bw, (height - 12) / bh);
            if (scale <= 0) scale = 1f;

            var centerX = (bounds.MinX + bounds.MaxX) / 2.0;
            var centerY = (bounds.MinY + bounds.MaxY) / 2.0;
            var view = new ViewTransform(scale, width / 2.0, height / 2.0);
            // Preview usa escala local 1:1 do modelo OPCAD.
            var savedScale = LocalToWorldScale;
            LocalToWorldScale = 1.0;
            Render(canvas, def, def.HotspotX - centerX, def.HotspotY - centerY, 0, view, null);
            LocalToWorldScale = savedScale;
        }
        return bitmap;
    }

    /// <summary>
    /// Caixa delimitadora completa em coordenadas locais do elemento (unidades OPCAD, sem escala),
    /// incluindo primitivas (com o texto medido pela fonte real) e terminais.
    /// Usada para preview, quadro de seleção, hit-test e enquadramento.
    /// Textos dinâmicos sem conteúdo (e sem rótulo) são ignorados — não são visíveis.
    /// </summary>
    public static Rect2D ComputeBounds(ElementDefinition def, string? label = null, double extraScale = 1.0)
        => ComputeBoundsCore(def, label, includeTerminals: true, extraScale);

    /// <summary>Caixa de uma INSTÂNCIA posicionada, incluindo a escala de inserção (zoom CAD).</summary>
    public static Rect2D ComputeBounds(PlacedElement el, string? label = null)
        => ComputeBoundsCore(el.Definition, label, includeTerminals: true, el.Scale, el.TextOverrides);

    /// <summary>
    /// Retângulo delimitador de um texto dinâmico em coordenadas locais do elemento.
    /// Retorna null quando o texto não é visível (vazio e sem rótulo).
    /// </summary>
    public static Rect2D? DynamicTextBounds(ElementDynamicText dt, string? label)
        => TextBoxMm(dt, label);

    /// <summary>
    /// Caixa do CORPO VISÍVEL (primitivas com texto real), sem terminais e sem placeholders
    /// de textos dinâmicos vazios. Usada para o centro visual sob o cursor e para o overlay
    /// de depuração do posicionamento.
    /// </summary>
    public static Rect2D ComputeBodyBounds(ElementDefinition def, string? label = null)
        => ComputeBoundsCore(def, label, includeTerminals: false);

    private static Rect2D ComputeBoundsCore(ElementDefinition def, string? label, bool includeTerminals, double extraScale = 1.0,
        System.Collections.Generic.IReadOnlyDictionary<int, string>? overrides = null)
    {
        // Escala EFETIVA do elemento (por-definição) para milímetros do mundo da folha.
        var scale = ElementScale(def) * extraScale;
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        void Grow(double x, double y)
        {
            x *= scale;  // ← Escala as coordenadas
            y *= scale;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        void GrowBox(Rect2D box)
        {
            Grow(box.MinX, box.MinY);
            Grow(box.MaxX, box.MinY);
            Grow(box.MinX, box.MaxY);
            Grow(box.MaxX, box.MaxY);
        }

        for (var i = 0; i < def.Primitives.Count; i++)
        {
            var p = def.Primitives[i];
            if (overrides is { } ov && ov.TryGetValue(i, out var text) &&
                p is ElementText textPrim && (p is not ElementDynamicText d || d.TextFrom == "UserText"))
            {
                p = textPrim with { Text = text };
            }
            switch (p)
            {
                case ElementLine l:
                    Grow(l.X1, l.Y1); Grow(l.X2, l.Y2);
                    break;
                case ElementRect r:
                    Grow(r.X, r.Y); Grow(r.X + r.Width, r.Y + r.Height);
                    break;
                case ElementEllipse e:
                    Grow(e.X, e.Y); Grow(e.X + e.Width, e.Y + e.Height);
                    break;
                case ElementArc a:
                    Grow(a.X, a.Y); Grow(a.X + a.Width, a.Y + a.Height);
                    break;
                case ElementPolygon pol:
                    foreach (var pt in pol.Points) Grow(pt.X, pt.Y);
                    break;
                case ElementPolyline pl:
                    foreach (var pt in pl.Points) Grow(pt.X, pt.Y);
                    break;
                case ElementText t when t is not ElementDynamicText:
                    if (TextBoxMm(t, label) is { } box)
                        GrowBox(box);
                    break;
            }
        }

        if (includeTerminals)
        {
            foreach (var t in def.Terminals)
                Grow(t.X, t.Y);
        }

        if (minX > maxX) // nenhuma primitiva visível
        {
            minX = -def.HotspotX * scale; minY = -def.HotspotY * scale;
            maxX = def.Width * scale - def.HotspotX * scale; maxY = def.Height * scale - def.HotspotY * scale;
        }
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Retângulo do texto em coordenadas do modelo (mm), respeitando fonte, tamanho,
    /// alinhamento, multi-linha e rotação. Retorna null quando o texto não é visível
    /// (vazio, ou texto dinâmico sem conteúdo e sem rótulo).
    /// </summary>
    private static Rect2D? TextBoxMm(ElementText t, string? label)
    {
        var text = t.Text;
        if (t is ElementDynamicText d)
        {
            text = d.TextFrom == "ElementInfo" && !string.IsNullOrEmpty(label)
                ? label
                : d.Text;
        }
        if (string.IsNullOrEmpty(text))
            return null;

        var lines = text.Split('\n');
        var (family, sizePt, bold) = ParseFont(t.Font);
        var sizeMm = (float)(sizePt * PtToMm);

        using var typeface = SKTypeface.FromFamilyName(family, bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        if (typeface is null)
            return null; // sem métricas confiáveis — ignora o texto
        using var font = new SKFont(typeface, sizeMm) { Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = sizeMm,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        var width = 0f;
        foreach (var line in lines)
            width = Math.Max(width, paint.MeasureText(line));
        if (width <= 0)
            return null;

        var m = font.Metrics;
        var ascent = Math.Abs(m.Ascent);
        var descent = Math.Abs(m.Descent);
        var leading = Math.Abs(m.Leading);
        var lineHeight = ascent + descent + leading;
        var n = lines.Length;

        // Caixa em espaço local Y-down (como no DrawText), relativa à âncora (0,0).
        double topLocal, bottomLocal;
        switch (t.Valign)
        {
            case ElementTextVAlign.Bottom:
                topLocal = -ascent;
                bottomLocal = (n - 1) * lineHeight + descent;
                break;
            case ElementTextVAlign.Center:
                topLocal = -ascent / 2.0;
                bottomLocal = -ascent / 2.0 + (n - 1) * lineHeight + descent;
                break;
            default: // Top
                topLocal = 0;
                bottomLocal = (n - 1) * lineHeight + descent;
                break;
        }

        var dx = t.Halign switch
        {
            ElementTextHAlign.Right => -width,
            ElementTextHAlign.Center => -width / 2.0,
            _ => 0
        };

        // Converte para o modelo Y-up: modelY = t.Y - localY.
        var left = t.X + dx;
        var right = left + width;
        var top = t.Y - topLocal;
        var bottom = t.Y - bottomLocal;

        var box = new Rect2D(left, bottom, right - left, top - bottom);

        if (t.Rotation != 0)
            box = RotatedBoxAabb(box, t.X, t.Y, t.Rotation);

        return box;
    }

    /// <summary>AABB de um retângulo rotacionado em torno de (cx, cy).</summary>
    private static Rect2D RotatedBoxAabb(Rect2D box, double cx, double cy, double rotDeg)
    {
        var a = rotDeg * Math.PI / 180.0;
        var ca = Math.Cos(a);
        var sa = Math.Sin(a);
        var corners = new[]
        {
            new Point2D(box.MinX - cx, box.MinY - cy),
            new Point2D(box.MaxX - cx, box.MinY - cy),
            new Point2D(box.MinX - cx, box.MaxY - cy),
            new Point2D(box.MaxX - cx, box.MaxY - cy)
        };
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var c in corners)
        {
            var rx = cx + c.X * ca - c.Y * sa;
            var ry = cy + c.X * sa + c.Y * ca;
            minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
            minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
        }
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Raio (mm, unidade do mundo OPCAD) dos pontos de terminal desenhados nos símbolos.</summary>
    public const float TerminalDotRadiusUnits = 0.4f;

    /// <summary>
    /// Diâmetro (unidades do mundo da folha) do ponto de terminal, incluindo o ajuste
    /// global de tamanho. Os condutores devem usar esta mesma espessura para ficarem
    /// visualmente iguais aos pontos de conexão dos elementos.
    /// </summary>
    public static double TerminalDotDiameterMm()
        => 2.0 * TerminalDotRadiusUnits * ElementDefinition.GlobalScale * LocalToWorldScale;

    /// <summary>
    /// Deslocamento do centro VISUAL do símbolo em relação ao hotspot, em unidades do MUNDO.
    /// Usado para centralizar o desenho sob o cursor (fantasma e inserção). O centro visual é
    /// o centro da caixa das primitivas de FORMA, EXCLUINDO as "orelhas" de conexão (formas
    /// cujo centro fica perto de um terminal declarado — ex.: marcas do topo do motor) e os
    /// textos/rótulos. Assim o cursor fica sobre o símbolo principal (o círculo do motor),
    /// não puxado pelas marcas de terminal.
    /// </summary>
    public static (double X, double Y) VisualCenterOffset(ElementDefinition def, string? label = null)
    {
        var b = ComputeVisualBodyBounds(def, label);
        var scale = ElementScale(def);
        return (b.CenterX - def.HotspotX * scale, b.CenterY - def.HotspotY * scale);
    }

    /// <summary>
    /// Caixa do CORPO VISUAL do símbolo (mundo): primitivas de FORMA (linha/retângulo/elipse/
    /// arco/polígono) excluindo texto e as "orelhas" de conexão (formas com centro perto de um
    /// terminal declarado). Usado para o centro visual sob o cursor. Fallback: caixa do corpo.
    /// </summary>
    private static Rect2D ComputeVisualBodyBounds(ElementDefinition def, string? label)
    {
        var scale = ElementScale(def);
        var centers = new List<(double Cx, double Cy)>();
        var boxes = new List<Rect2D>();

        void AddShape(double mnx, double mny, double mxx, double mxy)
        {
            mnx *= scale; mny *= scale; mxx *= scale; mxy *= scale;
            var c = new Rect2D(mnx, mny, mxx - mnx, mxy - mny);
            boxes.Add(c);
            centers.Add((c.CenterX, c.CenterY));
        }

        foreach (var prim in def.Primitives)
        {
            switch (prim)
            {
                case ElementLine l:
                    AddShape(Math.Min(l.X1, l.X2), Math.Min(l.Y1, l.Y2), Math.Max(l.X1, l.X2), Math.Max(l.Y1, l.Y2));
                    break;
                case ElementRect r:
                    AddShape(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
                    break;
                case ElementEllipse e:
                    AddShape(e.X, e.Y, e.X + e.Width, e.Y + e.Height);
                    break;
                case ElementArc a:
                    AddShape(a.X, a.Y, a.X + a.Width, a.Y + a.Height);
                    break;
                case ElementPolygon p:
                    if (p.Points.Count > 0)
                    {
                        double mnx = p.Points[0].X, mny = p.Points[0].Y, mxx = p.Points[0].X, mxy = p.Points[0].Y;
                        foreach (var pt in p.Points)
                        {
                            if (pt.X < mnx) mnx = pt.X;
                            if (pt.Y < mny) mny = pt.Y;
                            if (pt.X > mxx) mxx = pt.X;
                            if (pt.Y > mxy) mxy = pt.Y;
                        }
                        AddShape(mnx, mny, mxx, mxy);
                    }
                    break;
                case ElementPolyline pl:
                    if (pl.Points.Count > 0)
                    {
                        double mnx = pl.Points[0].X, mny = pl.Points[0].Y, mxx = pl.Points[0].X, mxy = pl.Points[0].Y;
                        foreach (var pt in pl.Points)
                        {
                            if (pt.X < mnx) mnx = pt.X;
                            if (pt.Y < mny) mny = pt.Y;
                            if (pt.X > mxx) mxx = pt.X;
                            if (pt.Y > mxy) mxy = pt.Y;
                        }
                        AddShape(mnx, mny, mxx, mxy);
                    }
                    break;
                // Texto/rótulos: ignorados — não determinam o centro visual do símbolo.
            }
        }

        if (boxes.Count == 0)
            return ComputeBodyBounds(def, label);

        // Terminais declarados em unidades do mundo.
        double allMinX = double.MaxValue, allMinY = double.MaxValue;
        double allMaxX = double.MinValue, allMaxY = double.MinValue;
        foreach (var b in boxes)
        {
            if (b.MinX < allMinX) allMinX = b.MinX;
            if (b.MinY < allMinY) allMinY = b.MinY;
            if (b.MaxX > allMaxX) allMaxX = b.MaxX;
            if (b.MaxY > allMaxY) allMaxY = b.MaxY;
        }
        double size = Math.Min(allMaxX - allMinX, allMaxY - allMinY);
        // Raio de exclusão das "orelhas" de conexão em mm do mundo. O teto de 20 mm
        // foi calibrado para a escala nativa (GlobalScale = 1.0), onde um símbolo de
        // ~20-35 mm tem orelhas a até ~17 mm dos terminais declarados.
        double radius = Math.Clamp(size * 0.5, 3.0, 20.0);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        int kept = 0;
        for (int i = 0; i < boxes.Count; i++)
        {
            bool nearTerminal = false;
            foreach (var t in def.Terminals)
            {
                double tx = t.X * scale, ty = t.Y * scale;
                double dx = centers[i].Cx - tx, dy = centers[i].Cy - ty;
                if (dx * dx + dy * dy <= radius * radius) { nearTerminal = true; break; }
            }
            if (nearTerminal)
                continue;
            kept++;
            var b = boxes[i];
            if (b.MinX < minX) minX = b.MinX;
            if (b.MinY < minY) minY = b.MinY;
            if (b.MaxX > maxX) maxX = b.MaxX;
            if (b.MaxY > maxY) maxY = b.MaxY;
        }

        if (kept == 0)
            return ComputeBodyBounds(def, label);
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Caixa delimitadora em coordenadas do mundo da folha (1:1 com o modelo OPCAD).</summary>
    public static Rect2D ComputeBoundsWorld(ElementDefinition def)
    {
        var b = ComputeBounds(def);
        return new Rect2D(b.MinX, b.MinY, b.Width, b.Height);
    }

    /// <summary>
    /// Constrói a matriz de transformação mundo→tela para um elemento.
    /// Público para uso externo (hover highlight, debug, etc.).
    /// </summary>
    public static SKMatrix BuildMatrixPublic(ElementDefinition def, double x, double y, double rotation, ViewTransform view)
        => BuildMatrix(def, x, y, rotation, view);

    /// <summary>
    /// Constrói a matriz de transformação elemento-local → pixel lógico (DIP).
    ///
    /// Equivalente matemático a:
    ///   1. T(-hotspot): centralizar hotspot na origem (coord. locais do elemento)
    ///   2. S(scale):    escalar unidades do arquivo → mm do mundo
    ///   3. R(-rot):     rotacionar (graus, sentido horário na tela)
    ///   4. T(x,y):      posicionar no mundo (mm da folha)
    ///   5. W2S:         mundo → tela em coordenadas lógicas (DIPs)
    ///
    /// Como essa matriz é CONCATENADA ao canvas (canvas.Concat), o Skia/Avalonia
    /// cuida automaticamente de aplicar a escala DPI e o offset do controle na tela.
    /// </summary>
    private static SKMatrix BuildMatrix(ElementDefinition def, double x, double y, double rotation, ViewTransform view, double extraScale = 1.0)
    {
        var scale = ElementScale(def) * extraScale;

        // ── 1-4: Coordenadas locais → mundo (mm) ───────────────────────────
        var m = SKMatrix.CreateTranslation((float)(-def.HotspotX), (float)(-def.HotspotY));
        m = m.PostConcat(SKMatrix.CreateScale((float)scale, (float)scale));

        if (rotation != 0)
            m = m.PostConcat(SKMatrix.CreateRotationDegrees((float)-rotation));

        m = m.PostConcat(SKMatrix.CreateTranslation((float)x, (float)y));

        // ── 5: Mundo (mm) → pixel lógico (DIP) da tela ─────────────────────
        var w2s = new SKMatrix(
            (float)view.Scale, 0, (float)view.OffsetX,
            0, (float)-view.Scale, (float)view.OffsetY,
            0, 0, 1);

        return m.PostConcat(w2s);
    }

    /// <summary>
    /// Desenha o retângulo de seleção do elemento (cinza, tracejado-pontilhado,
    /// cosmético) — estilo do padrão CAD.
    /// </summary>
    public static void RenderSelectionFrame(SKCanvas canvas, PlacedElement el, ViewTransform view)
    {
        var def = el.Definition;
        if (def.Primitives.Count == 0 && def.Terminals.Count == 0)
            return;

        var matrix = BuildMatrix(def, el.X, el.Y, el.Rotation, view);
        canvas.Save();
        canvas.Concat(ref matrix);

        var b = ComputeBounds(el, el.Label).Inflate(0.7);
        var rect = SKRect.Create((float)b.MinX, (float)b.MinY, (float)b.Width, (float)b.Height);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColor.Parse("#2F9BFF"), // azul de acento — quadro bem visível
            StrokeWidth = (float)(1.4 / view.Scale), // cosmético, um pouco mais visível
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 8f, 4f, 2f, 4f }, 0)
        };
        var radius = (float)Math.Min(10, Math.Min(b.Width, b.Height) / 2);
        canvas.DrawRoundRect(rect, radius, radius, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Desenha o texto de referência de folha abaixo do elemento (para elementos
    /// mestre/escravo, estilo referência cruzada do padrão CAD). Suporta várias linhas
    /// separadas por '\n' (mestre com vários escravos vinculados).
    /// </summary>
    public static void RenderFolioRef(SKCanvas canvas, PlacedElement el, ViewTransform view, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var lines = text.Split('\n');
        if (lines.Length == 0)
            return;

        var b = ComputeBounds(el);
        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var corners = new[]
        {
            new Point2D(b.MinX, b.MinY), new Point2D(b.MaxX, b.MinY),
            new Point2D(b.MinX, b.MaxY), new Point2D(b.MaxX, b.MaxY)
        };
        foreach (var c in corners)
        {
            var w = OperCAD.Core.Documents.TerminalGeometry.LocalToWorld(el, c);
            minX = Math.Min(minX, w.X);
            maxX = Math.Max(maxX, w.X);
            minY = Math.Min(minY, w.Y);
        }

        var size = 2.5 * view.Scale;
        if (size < 0.6)
            return;
        using var paint = new SKPaint
        {
            Color = SKColor.Parse("#000000"),
            IsAntialias = true,
            TextSize = (float)size,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
        };

        // Centraliza o bloco de linhas horizontalmente e empilha verticalmente.
        var lineHeight = size * 1.3;
        var blockHeight = lineHeight * lines.Length;
        var baseScreen = view.WorldToScreen(new Point2D((minX + maxX) / 2, minY - 3.5));
        var topY = baseScreen.Y - blockHeight;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var measured = paint.MeasureText(line);
            canvas.DrawText(line, (float)(baseScreen.X - measured / 2), (float)topY, paint);
            topY += lineHeight;
        }
    }

    private static void DrawPrimitive(SKCanvas canvas, ElementPrimitive prim, string? label)
    {
        switch (prim)
        {
            case ElementLine l:
                DrawLine(canvas, l);
                break;
            case ElementRect r:
                DrawRect(canvas, r);
                break;
            case ElementEllipse e:
                DrawEllipse(canvas, e);
                break;
            case ElementArc a:
                DrawArc(canvas, a);
                break;
            case ElementPolygon p:
                DrawPolygon(canvas, p);
                break;
            case ElementPolyline pl:
                DrawPolyline(canvas, pl);
                break;
            case ElementDynamicText d:
                DrawText(canvas, d, label);
                break;
            case ElementText t:
                DrawText(canvas, t, label);
                break;
        }
    }

    private static void DrawLine(SKCanvas canvas, ElementLine l)
    {
        using var paint = StrokePaint(l.Style);
        canvas.DrawLine((float)l.X1, (float)l.Y1, (float)l.X2, (float)l.Y2, paint);
        DrawLineEnd(canvas, l, l.End1, l.X1, l.Y1, l.X2, l.Y2, l.Length1);
        DrawLineEnd(canvas, l, l.End2, l.X2, l.Y2, l.X1, l.Y1, l.Length2);
    }

    private static void DrawLineEnd(SKCanvas canvas, ElementLine l, string end, double ex, double ey, double ox, double oy, double len)
    {
        if (end == "none" || len <= 0)
            return;

        var dirX = ex - ox;
        var dirY = ey - oy;
        var norm = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (norm < 1e-6)
            return;
        dirX /= norm;
        dirY /= norm;

        var px = -dirY; // perpendicular
        var py = dirX;
        var half = len * 0.55;
        var apex = new Point2D(ex, ey);

        switch (end)
        {
            case "simple":
            case "triangle":
            {
                var baseC = new Point2D(ex - dirX * len, ey - dirY * len);
                var p1 = new Point2D(baseC.X + px * half, baseC.Y + py * half);
                var p2 = new Point2D(baseC.X - px * half, baseC.Y - py * half);
                using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColor.Parse("#000000"), IsAntialias = true };
                using var stroke = StrokePaint(l.Style);
                var path = new SKPath();
                path.MoveTo((float)apex.X, (float)apex.Y);
                path.LineTo((float)p1.X, (float)p1.Y);
                path.LineTo((float)p2.X, (float)p2.Y);
                path.Close();
                if (end == "simple")
                    canvas.DrawPath(path, fill);
                else
                    canvas.DrawPath(path, stroke);
                break;
            }
            case "circle":
            {
                using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColor.Parse("#000000"), IsAntialias = true };
                canvas.DrawCircle((float)ex, (float)ey, (float)(len * 0.4), fill);
                break;
            }
            case "diamond":
            {
                var fwd = new Point2D(ex + dirX * len * 0.5, ey + dirY * len * 0.5);
                var back = new Point2D(ex - dirX * len * 0.5, ey - dirY * len * 0.5);
                var p1 = new Point2D(ex + px * half, ey + py * half);
                var p2 = new Point2D(ex - px * half, ey - py * half);
                using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColor.Parse("#000000"), IsAntialias = true };
                var path = new SKPath();
                path.MoveTo((float)fwd.X, (float)fwd.Y);
                path.LineTo((float)p1.X, (float)p1.Y);
                path.LineTo((float)back.X, (float)back.Y);
                path.LineTo((float)p2.X, (float)p2.Y);
                path.Close();
                canvas.DrawPath(path, fill);
                break;
            }
        }
    }

    private static void DrawRect(SKCanvas canvas, ElementRect r)
    {
        var rect = SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
        var fill = FillPaint(r.Style);
        var stroke = StrokePaint(r.Style);
        if (fill is not null)
            canvas.DrawRoundRect(rect, (float)r.Rx, (float)r.Ry, fill);
        canvas.DrawRoundRect(rect, (float)r.Rx, (float)r.Ry, stroke);
    }

    private static void DrawEllipse(SKCanvas canvas, ElementEllipse e)
    {
        var rect = SKRect.Create((float)e.X, (float)e.Y, (float)e.Width, (float)e.Height);
        var fill = FillPaint(e.Style);
        var stroke = StrokePaint(e.Style);
        if (fill is not null)
            canvas.DrawOval(rect, fill);
        canvas.DrawOval(rect, stroke);
    }

    private static void DrawArc(SKCanvas canvas, ElementArc a)
    {
        var rect = SKRect.Create((float)a.X, (float)a.Y, (float)a.Width, (float)a.Height);
        using var path = new SKPath();
        path.AddArc(rect, (float)a.Start, (float)a.Angle);
        canvas.DrawPath(path, StrokePaint(a.Style));
    }

    private static void DrawPolygon(SKCanvas canvas, ElementPolygon p)
    {
        if (p.Points.Count < 2)
            return;
        using var path = new SKPath();
        path.MoveTo((float)p.Points[0].X, (float)p.Points[0].Y);
        for (var i = 1; i < p.Points.Count; i++)
            path.LineTo((float)p.Points[i].X, (float)p.Points[i].Y);
        if (p.Closed)
            path.Close();

        var fill = FillPaint(p.Style);
        if (fill is not null)
            canvas.DrawPath(path, fill);
        canvas.DrawPath(path, StrokePaint(p.Style));
    }

    private static void DrawPolyline(SKCanvas canvas, ElementPolyline pl)
    {
        if (pl.Points.Count < 2)
            return;
        using var path = new SKPath();
        path.MoveTo((float)pl.Points[0].X, (float)pl.Points[0].Y);
        for (var i = 1; i < pl.Points.Count; i++)
            path.LineTo((float)pl.Points[i].X, (float)pl.Points[i].Y);
        canvas.DrawPath(path, StrokePaint(pl.Style));
    }

    private static void DrawText(SKCanvas canvas, ElementText t, string? label)
    {
        var text = t.Text;
        if (t is ElementDynamicText d && string.IsNullOrEmpty(text))
        {
            // Texto dinâmico sem conteúdo: usa a etiqueta do elemento quando
            // o texto vem das informações do elemento (ElementInfo).
            text = d.TextFrom == "ElementInfo"
                ? (string.IsNullOrEmpty(label) ? string.Empty : label)
                : string.Empty;
        }
        if (string.IsNullOrEmpty(text))
            return;

        var (family, sizePt, bold) = ParseFont(t.Font);
        var sizeMm = (float)(sizePt * PtToMm);
        // Clamp de tela: mantém o texto legível mesmo quando o símbolo encolhe muito.
        var minLocal = (float)(MinTextPixels / CurrentScreenScale);
        if (sizeMm < minLocal)
            sizeMm = minLocal;

        using var typeface = SKTypeface.FromFamilyName(family,
            bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var font = new SKFont(typeface, sizeMm) { Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint
        {
            Color = ParseColor(t.Color),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Typeface = typeface
        };

        var width = paint.MeasureText(text);
        var metrics = font.Metrics;
        var ascent = Math.Abs(metrics.Ascent);

        // O BuildMatrix aplica world→tela com Y invertido (mundo Y-up, Skia Y-down).
        // Sem Scale(1,-1) o glifo acompanha essa inversão e o texto fica de cabeça para baixo.
        canvas.Save();
        canvas.Translate((float)t.X, (float)t.Y);

        // Contro-rotação: TODOS os textos de elemento permanecem legíveis (na vertical)
        // mesmo quando o elemento está rotacionado. Números de terminal, etiquetas e
        // textos dinâmicos ficam sempre na horizontal na tela, independentemente da
        // rotação do elemento pai.
        if (CurrentElementRotation != 0)
            canvas.RotateDegrees((float)CurrentElementRotation);

        if (t.Rotation != 0)
            canvas.RotateDegrees((float)-t.Rotation);
        canvas.Scale(1, -1);

        float dx = t.Halign switch
        {
            ElementTextHAlign.Right => -width,
            ElementTextHAlign.Center => -width / 2,
            _ => 0
        };
        // Após Scale(1,-1) o espaço local do texto é Y-down (convenção do Skia DrawText).
        float dy = t.Valign switch
        {
            ElementTextVAlign.Bottom => 0,
            ElementTextVAlign.Center => ascent / 2,
            _ => ascent // Top: topo do glifo no ponto de ancoragem
        };

        canvas.DrawText(text, dx, dy, font, paint);
        canvas.Restore();
    }

    /// <summary>Interpreta a string de fonte padrão CAD: "FamilyName,sizePt,...".</summary>
    private static (string Family, double SizePt, bool Bold) ParseFont(string font)
    {
        var parts = font.Split(',');
        var family = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "Liberation Sans";
        var size = parts.Length > 1 && double.TryParse(parts[1], out var s) ? s : 5.0;
        var weight = parts.Length > 4 ? parts[4] : "50";
        var bold = weight == "75" || weight == "87" || weight == "63";
        return (family, size, bold);
    }

    private static SKPaint StrokePaint(ElementStyle style)
    {
        var width = LineWidthMm(style.LineWeight);
        // Clamp de tela: mantém um piso visível de espessura mesmo quando o símbolo encolhe.
        var minLocal = (float)(MinStrokePixels / CurrentScreenScale);
        if (width < minLocal)
            width = minLocal;
        var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ParseColor(style.Color),
            StrokeWidth = width,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
        ShapeRenderer.ApplyDashEffect(paint, style.DashStyle);
        return paint;
    }

    private static SKPaint? FillPaint(ElementStyle style)
    {
        if (style.Filling == "none" || style.Filling.Length == 0)
            return null;
        var color = ParseColor(style.Filling);
        if (color.Alpha == 0 && style.Filling != "transparent")
            color = SKColors.Black;
        return new SKPaint { Style = SKPaintStyle.Fill, Color = color, IsAntialias = true };
    }

    /// <summary>Converte nome/hex de cor padrão CAD em SKColor.</summary>
    private static SKColor ParseColor(string color)
    {
        var c = color.Trim();
        if (c.Length == 0)
            return SKColors.Black;
        if (c.StartsWith('#'))
            return SKColor.TryParse(c, out var parsed) ? parsed : SKColors.Black;
        return c.ToLowerInvariant() switch
        {
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            "blue" => SKColors.Blue,
            "yellow" => SKColors.Yellow,
            "gray" or "grey" => SKColors.Gray,
            "transparent" => SKColors.Transparent,
            _ => SKColors.Black
        };
    }
}
