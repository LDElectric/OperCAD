using System.Collections.Concurrent;
using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>Converte nomes de cor do estilo padrão CAD (black, red, green...) ou hex em SKColor.</summary>
public static class ColorPalette
{
    public static SKColor Parse(string? color)
    {
        var c = (color ?? "black").Trim();
        return c.ToLowerInvariant() switch
        {
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            "blue" => SKColors.Blue,
            "yellow" => SKColors.Yellow,
            "gray" or "grey" => SKColors.Gray,
            "orange" => new SKColor(255, 165, 0),
            _ => SKColor.TryParse(c, out var parsed) ? parsed : SKColors.Black
        };
    }
}

/// <summary>Renderiza formas livres da folha (linha, retângulo, elipse, polilinha).</summary>
public static class ShapeRenderer
{
    /// <summary>Espessura mínima padrão (px de tela) para traços finos continuarem visíveis.</summary>
    public const float DefaultMinStrokePx = 0.3f;

    /// <param name="minStrokePx">
    /// Espessura mínima em px de tela. Na EXPORTAÇÃO vetorial deve ser 0, para o traço sair com a
    /// espessura exata do objeto (sem inflar linhas finas).
    /// </param>
    public static void Render(SKCanvas canvas, ShapeItem shape, ViewTransform view,
        float minStrokePx = DefaultMinStrokePx)
    {
        if (shape.Points.Count == 0)
            return;

        var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            // PenWidth = 0 significa "este objeto NÃO é traçado" (caminho só PREENCHIDO no PDF,
            // como os glifos de texto vetorizado, cujo LineWidth é 0). O traço é simplesmente
            // NÃO desenhado (abaixo) — usar cor transparente não bastava: no PDF exportado o
            // Skia emitia o traço mesmo assim (0,3 pt) e cada glifo saía DUPLICADO, com contorno
            // fino extra (borrava o texto) e ~104 mil segmentos de linha a mais no arquivo.
            Color = ColorPalette.Parse(shape.Color),
            StrokeWidth = (float)Math.Max(shape.PenWidth * view.Scale, minStrokePx),
            IsAntialias = true,
            StrokeCap = shape.LineCap switch
            {
                ShapeLineCap.Butt => SKStrokeCap.Butt,
                ShapeLineCap.Square => SKStrokeCap.Square,
                _ => SKStrokeCap.Round
            },
            StrokeJoin = shape.LineJoin switch
            {
                ShapeLineJoin.Miter => SKStrokeJoin.Miter,
                ShapeLineJoin.Bevel => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Round
            }
        };
        ApplyDashEffect(stroke, shape, view.Scale);
        using var _s = stroke;

        // Só traça quem TEM caneta: forma preenchida sem traço no PDF não pode ganhar contorno.
        var strokePaint = shape.PenWidth > 0 ? stroke : null;

        switch (shape.Kind)
        {
            case ShapeKind.Line:
                if (strokePaint is not null)
                    canvas.DrawLine(ToScreen(shape.Points[0], view), ToScreen(shape.Points[1], view), strokePaint);
                break;

            case ShapeKind.Rectangle:
            {
                var r = WorldRect(shape.Points[0], shape.Points[1], view);
                if (FillColor(shape) is { } fill)
                    using (fill)
                        canvas.DrawRect(r, fill);
                if (strokePaint is not null)
                    canvas.DrawRect(r, strokePaint);
                break;
            }

            case ShapeKind.Ellipse:
            {
                var r = WorldRect(shape.Points[0], shape.Points[1], view);
                if (FillColor(shape) is { } fill)
                    using (fill)
                        canvas.DrawOval(r, fill);
                if (strokePaint is not null)
                    canvas.DrawOval(r, strokePaint);
                break;
            }

            case ShapeKind.Polygon:
            {
                using var path = new SKPath();
                // Cada CONTORNO começa num MoveTo: sem isso os contornos ficariam ligados por
                // segmentos, criando área preenchida ENTRE as letras (texto virava borrão).
                foreach (var (start, end) in shape.Contours())
                {
                    path.MoveTo(ToScreen(shape.Points[start], view));
                    for (var i = start + 1; i < end; i++)
                        path.LineTo(ToScreen(shape.Points[i], view));
                    if (shape.Closed)
                        path.Close();
                }
                if (shape.Closed && FillColor(shape) is { } fill)
                {
                    using (fill)
                    {
                        // EvenOdd preserva os FUROS: um texto vetorizado é um path com vários
                        // contornos (letra + vazio interno); sem even-odd o vazio era preenchido.
                        path.FillType = SKPathFillType.EvenOdd;
                        canvas.DrawPath(path, fill);
                    }
                }
                if (strokePaint is not null)
                    canvas.DrawPath(path, strokePaint);
                break;
            }

            case ShapeKind.Triangle:
            {
                if (shape.Points.Count == 3)
                {
                    var sp0 = ToScreen(shape.Points[0], view);
                    var sp1 = ToScreen(shape.Points[1], view);
                    var sp2 = ToScreen(shape.Points[2], view);
                    using var path = new SKPath();
                    path.MoveTo(sp0);
                    path.LineTo(sp1);
                    path.LineTo(sp2);
                    path.Close();
                    if (FillColor(shape) is { } fill)
                        using (fill) canvas.DrawPath(path, fill);
                    if (strokePaint is not null)
                        canvas.DrawPath(path, strokePaint);
                }
                else if (shape.Points.Count >= 2)
                {
                    // Fallback: 2 pontos → triângulo isósceles (compatibilidade)
                    var p0 = shape.Points[0];
                    var p1 = shape.Points[1];
                    var midX = (p0.X + p1.X) / 2.0;
                    var topY = Math.Max(p0.Y, p1.Y);
                    var botY = Math.Min(p0.Y, p1.Y);
                    var sp0 = ToScreen(new Point2D(p0.X, botY), view);
                    var sp1 = ToScreen(new Point2D(p1.X, botY), view);
                    var sp2 = ToScreen(new Point2D(midX, topY), view);
                    using var path = new SKPath();
                    path.MoveTo(sp0);
                    path.LineTo(sp1);
                    path.LineTo(sp2);
                    path.Close();
                    if (FillColor(shape) is { } fill)
                        using (fill) canvas.DrawPath(path, fill);
                    if (strokePaint is not null)
                        canvas.DrawPath(path, strokePaint);
                }
                break;
            }

            case ShapeKind.Arc:
            {
                if (shape.Points.Count >= 1)
                {
                    var center = shape.Points[0];
                    var radius = shape.Points.Count >= 2 ? center.DistanceTo(shape.Points[1]) : 5.0;
                    var sc = view.WorldToScreen(center);
                    var screenRadius = radius * view.Scale;
                    using var arcPath = new SKPath();
                    // SKPath.AddArc uses angles in degrees, clockwise from 3 o'clock
                    // Our convention: 0 = right, positive = counter-clockwise
                    // SkiaSharp: 0 = right, positive = clockwise → negate and adjust
                    arcPath.AddArc(
                        SKRect.Create((float)(sc.X - screenRadius), (float)(sc.Y - screenRadius),
                                      (float)(screenRadius * 2), (float)(screenRadius * 2)),
                        (float)(-shape.StartAngle),
                        (float)(-shape.SweepAngle));
                    if (strokePaint is not null)
                        canvas.DrawPath(arcPath, strokePaint);
                }
                break;
            }

            case ShapeKind.EllipseArc:
            {
                if (shape.Points.Count >= 2)
                {
                    var c = shape.Points[0];
                    var q = shape.Points[1];
                    var rx = Math.Abs(q.X - c.X);
                    var ry = Math.Abs(q.Y - c.Y);
                    if (rx < 0.01) rx = 0.01;
                    if (ry < 0.01) ry = 0.01;
                    var sc = view.WorldToScreen(c);
                    var srX = rx * view.Scale;
                    var srY = ry * view.Scale;
                    using var arcPath = new SKPath();
                    arcPath.AddArc(
                        SKRect.Create((float)(sc.X - srX), (float)(sc.Y - srY),
                                      (float)(srX * 2), (float)(srY * 2)),
                        (float)(-shape.StartAngle),
                        (float)(-shape.SweepAngle));
                    if (strokePaint is not null)
                        canvas.DrawPath(arcPath, strokePaint);
                }
                break;
            }

            case ShapeKind.RoundedRectangle:
            {
                var r = WorldRect(shape.Points[0], shape.Points[1], view);
                var rx = (float)(shape.Rx * view.Scale);
                var ry = (float)(shape.Ry * view.Scale);
                if (rx < 0.5f) rx = 0f;
                if (ry < 0.5f) ry = 0f;
                using var rrPath = new SKPath();
                rrPath.AddRoundRect(r, rx, ry);
                if (FillColor(shape) is { } fill)
                    using (fill) canvas.DrawPath(rrPath, fill);
                if (strokePaint is not null)
                    canvas.DrawPath(rrPath, strokePaint);
                break;
            }

            case ShapeKind.Curve:
            {
                if (shape.Points.Count >= 2)
                {
                    using var path = new SKPath();
                    var first = ToScreen(shape.Points[0], view);
                    path.MoveTo(first);

                    if (shape.ControlPoints is { } cp && cp.Count > 0)
                    {
                        // Curva Bézier nativa: usar CubicTo para renderização fiel
                        var segCount = Math.Min(shape.Points.Count - 1, cp.Count);
                        for (var i = 0; i < segCount; i++)
                        {
                            var (c1, c2) = cp[i];
                            var end = shape.Points[i + 1];
                            path.CubicTo(ToScreen(c1, view), ToScreen(c2, view), ToScreen(end, view));
                        }
                    }
                    else
                    {
                        // Fallback: Catmull-Rom (curvas desenhadas pelo usuário)
                        var flat = ShapeGeometry.FlattenCurve(shape.Points, shape.Closed);
                        for (var i = 1; i < flat.Count; i++)
                            path.LineTo(ToScreen(flat[i], view));
                    }

                    if (shape.Closed)
                        path.Close();
                    // Preenchimento também em CURVAS: glifos de texto vetorizado preenchidos vêm
                    // com Bézier — sem isto eles eram desenhados ocós ("vazados").
                    if (shape.Closed && FillColor(shape) is { } curveFill)
                    {
                        using (curveFill)
                        {
                            path.FillType = SKPathFillType.EvenOdd;
                            canvas.DrawPath(path, curveFill);
                        }
                    }
                    if (strokePaint is not null)
                        canvas.DrawPath(path, strokePaint);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Caminho do CONTORNO da forma em coordenadas de tela, derivado do mesmo contorno usado
    /// no hit-test (<see cref="ShapeGeometry.OutlineSegments"/>). Cada segmento é desenhado
    /// separadamente — nada é ligado que o arquivo não ligue.
    /// </summary>
    private static SKPath? BuildHighlightPath(ShapeItem shape, ViewTransform view)
    {
        var segments = ShapeGeometry.OutlineSegments(shape);
        if (segments.Count == 0)
            return null;

        var path = new SKPath();
        foreach (var (a, b) in segments)
        {
            path.MoveTo(ToScreen(a, view));
            path.LineTo(ToScreen(b, view));
        }
        return path;
    }

    /// <summary>Retângulo envolvente em mundo (para seleção).</summary>
    public static Rect2D ComputeBounds(ShapeItem shape)
    {
        // Para arco, calcula bounding box só da curva (não inclui o centro vazio)
        if (shape.Kind == ShapeKind.Arc && shape.Points.Count >= 2)
        {
            var center = shape.Points[0];
            var radius = center.DistanceTo(shape.Points[1]);
            if (radius <= 0) return shape.Bounds;
            var startRad = shape.StartAngle * Math.PI / 180.0;
            var sweepRad = shape.SweepAngle * Math.PI / 180.0;
            const int n = 36;
            var minX = double.MaxValue; var maxX = double.MinValue;
            var minY = double.MaxValue; var maxY = double.MinValue;
            for (var i = 0; i <= n; i++)
            {
                var t = startRad + sweepRad * i / n;
                var x = center.X + radius * Math.Cos(t);
                var y = center.Y + radius * Math.Sin(t);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            var padArc = Math.Max(shape.PenWidth, 1) / 2.0 + 0.5;
            return new Rect2D(minX - padArc, minY - padArc, maxX - minX + padArc * 2, maxY - minY + padArc * 2);
        }

        // Arco elíptico: amostra a curva da elipse para obter o bounding box do trecho
        if (shape.Kind == ShapeKind.EllipseArc && shape.Points.Count >= 2)
        {
            var c = shape.Points[0]; var q = shape.Points[1];
            var rx = Math.Abs(q.X - c.X);
            var ry = Math.Abs(q.Y - c.Y);
            if (rx <= 0 || ry <= 0) return shape.Bounds;
            var startRad = shape.StartAngle * Math.PI / 180.0;
            var sweepRad = shape.SweepAngle * Math.PI / 180.0;
            const int n = 36;
            var minX = double.MaxValue; var maxX = double.MinValue;
            var minY = double.MaxValue; var maxY = double.MinValue;
            for (var i = 0; i <= n; i++)
            {
                var t = startRad + sweepRad * i / n;
                var x = c.X + rx * Math.Cos(t);
                var y = c.Y + ry * Math.Sin(t);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            var padEa = Math.Max(shape.PenWidth, 1) / 2.0 + 0.5;
            return new Rect2D(minX - padEa, minY - padEa, maxX - minX + padEa * 2, maxY - minY + padEa * 2);
        }

        // Curva suave: amostra a spline para obter o bounding box do trecho
        if (shape.Kind == ShapeKind.Curve && shape.Points.Count >= 2)
        {
            var flatCurve = ShapeGeometry.FlattenCurve(shape.Points, shape.Closed);
            var minX = double.MaxValue; var maxX = double.MinValue;
            var minY = double.MaxValue; var maxY = double.MinValue;
            foreach (var pt in flatCurve)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }
            var padC = Math.Max(shape.PenWidth, 1) / 2.0 + 0.5;
            return new Rect2D(minX - padC, minY - padC, maxX - minX + padC * 2, maxY - minY + padC * 2);
        }

        var b = shape.Bounds;
        var pad = Math.Max(shape.PenWidth, 1) / 2.0 + 0.5;
        return new Rect2D(b.MinX - pad, b.MinY - pad, b.Width + pad * 2, b.Height + pad * 2);
    }

    private static SKPaint? FillColor(ShapeItem shape)
    {
        if (shape.Filling == "none")
            return null;
        var c = ColorPalette.Parse(shape.Filling);
        return new SKPaint { Style = SKPaintStyle.Fill, Color = c, IsAntialias = true };
    }

    private static SKPoint ToScreen(Point2D p, ViewTransform view)
    {
        var s = view.WorldToScreen(p);
        return new SKPoint((float)s.X, (float)s.Y);
    }

    private static SKRect WorldRect(Point2D a, Point2D b, ViewTransform view)
    {
        var p1 = view.WorldToScreen(a);
        var p2 = view.WorldToScreen(b);
        return SKRect.Create(
            (float)Math.Min(p1.X, p2.X), (float)Math.Min(p1.Y, p2.Y),
            (float)Math.Abs(p2.X - p1.X), (float)Math.Abs(p2.Y - p1.Y));
    }

    /// <summary>Aplica efeito de tracejado/pontilhado no paint baseado no estilo de dash.</summary>
    public static void ApplyDashEffect(SKPaint paint, LineDashStyle dashStyle)
    {
        switch (dashStyle)
        {
            case LineDashStyle.Dashed:
                paint.PathEffect = SKPathEffect.CreateDash(new[] { 8f, 4f }, 0);
                break;
            case LineDashStyle.Dotted:
                paint.PathEffect = SKPathEffect.CreateDash(new[] { 2f, 4f }, 0);
                break;
            case LineDashStyle.DashDot:
                paint.PathEffect = SKPathEffect.CreateDash(new[] { 8f, 4f, 2f, 4f }, 0);
                break;
        }
    }

    private static void ApplyDashEffect(SKPaint paint, ShapeItem shape, double viewScale)
    {
        if (shape.DashPattern.Count >= 2)
        {
            var intervals = shape.DashPattern
                .Select(value => (float)Math.Max(0.01, value * viewScale))
                .ToArray();
            paint.PathEffect = SKPathEffect.CreateDash(intervals, (float)(shape.DashPhase * viewScale));
            return;
        }
        ApplyDashEffect(paint, shape.DashStyle);
    }

    public static void RenderSelectionFrame(SKCanvas canvas, ShapeItem shape, ViewTransform view,
        bool showHandles = true)
    {
        if (shape.Points.Count == 0) return;

        // Destaque da PRÓPRIA GEOMETRIA (a linha/caminho), não de uma caixa envolvente: com
        // muitas linhas selecionadas as caixas se aglomeravam e atrapalhavam a leitura do
        // desenho. O contorno vem da mesma geometria do hit-test, então o destaque coincide
        // exatamente com a área clicável.
        if (BuildHighlightPath(shape, view) is { } highlight)
        {
            using (highlight)
            {
                // Espessura do destaque = a espessura da PRÓPRIA LINHA (como no editor de PDF),
                // com um mínimo para continuar visível em zoom baixo.
                var penScreen = (float)Math.Max(1.2, shape.PenWidth * view.Scale);

                // Linha fina demais para a cor ser percebida: um halo leve garante visibilidade.
                if (penScreen < 2.5f)
                {
                    using var halo = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = new SKColor(0, 120, 215, 96),
                        StrokeWidth = penScreen + 4f,
                        IsAntialias = true,
                        StrokeCap = SKStrokeCap.Round,
                        StrokeJoin = SKStrokeJoin.Round
                    };
                    canvas.DrawPath(highlight, halo);
                }

                using var crisp = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0, 120, 215),
                    StrokeWidth = penScreen,
                    IsAntialias = true,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round
                };
                canvas.DrawPath(highlight, crisp);
            }
        }

        // Caixa envolvente + alças apenas quando há UMA forma selecionada (aí elas servem para
        // redimensionar/girar/editar vértice). Em seleções múltiplas, só o destaque da linha.
        if (!showHandles)
            return;

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0)
        };
        var b = ComputeBounds(shape);
        var tl = view.WorldToScreen(new Point2D(b.MinX, b.MinY));
        var br = view.WorldToScreen(new Point2D(b.MaxX, b.MaxY));

        // Caixa envolvente apenas quando as alças NÃO são as extremidades: numa linha, o editor
        // de PDF marca só o traço e as extremidades (sem caixa).
        var usesVertexHandles = ShapeGeometry.UsesVertexHandles(shape);
        if (!usesVertexHandles)
            canvas.DrawRect((float)tl.X, (float)tl.Y, (float)(br.X - tl.X), (float)(br.Y - tl.Y), paint);

        using var handleFill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true
        };
        using var handleStroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.2f,
            IsAntialias = true
        };
        using var linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.2f,
            IsAntialias = true
        };
        var hs = TextItemRenderer.HandleHalf;
        var tlX = (float)tl.X; var tlY = (float)tl.Y;
        var brX = (float)br.X; var brY = (float)br.Y;
        var midX = (tlX + brX) / 2; var midY = (tlY + brY) / 2;

        // Alça de rotação (círculo acima do centro)
        var rotCenter = new SKPoint(midX, tlY - 20);
        canvas.DrawLine(midX, tlY, rotCenter.X, rotCenter.Y, linePaint);
        using var rotFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        using var rotStroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0, 120, 215), StrokeWidth = 1.4f, IsAntialias = true };
        canvas.DrawCircle(rotCenter, hs + 2, rotFill);
        canvas.DrawCircle(rotCenter, hs + 2, rotStroke);

        if (shape.Kind is ShapeKind.Arc or ShapeKind.EllipseArc && shape.Points.Count >= 2)
        {
            // Handles de ângulo para arcos: início e fim do arco
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
            var sStart = view.WorldToScreen(startPos);
            var sEnd = view.WorldToScreen(endPos);
            canvas.DrawCircle((float)sStart.X, (float)sStart.Y, hs, handleFill);
            canvas.DrawCircle((float)sStart.X, (float)sStart.Y, hs, handleStroke);
            canvas.DrawCircle((float)sEnd.X, (float)sEnd.Y, hs, handleFill);
            canvas.DrawCircle((float)sEnd.X, (float)sEnd.Y, hs, handleStroke);
        }
        else if (usesVertexHandles)
        {
            // Alças nas EXTREMIDADES/vértices do próprio traço (como no editor de PDF):
            // arrastar a alça move AQUELE ponto, esticando a linha — não escala a caixa.
            foreach (var pt in shape.Points)
            {
                var sp = view.WorldToScreen(pt);
                canvas.DrawRect((float)sp.X - hs, (float)sp.Y - hs, hs * 2, hs * 2, handleFill);
                canvas.DrawRect((float)sp.X - hs, (float)sp.Y - hs, hs * 2, hs * 2, handleStroke);
            }
        }
        else
        {
            // Handles de canto e borda (8 handles)
            var corners = new SKPoint[]
            {
                new(tlX, tlY), new(midX, tlY),
                new(brX, tlY), new(brX, midY),
                new(brX, brY), new(midX, brY),
                new(tlX, brY), new(tlX, midY)
            };
            foreach (var corner in corners)
            {
                canvas.DrawRect(corner.X - hs, corner.Y - hs, hs * 2, hs * 2, handleFill);
                canvas.DrawRect(corner.X - hs, corner.Y - hs, hs * 2, hs * 2, handleStroke);
            }
        }
    }
}

/// <summary>Renderiza textos independentes da folha (como texto independente do padrão CAD).</summary>
public static class TextItemRenderer
{
    public const double PtToMm = 0.3528;

    public static void Render(SKCanvas canvas, TextItem text, ViewTransform view)
    {
        if (string.IsNullOrEmpty(text.Text))
            return;

        var sizeMm = text.FontSizePt * PtToMm;
        var screenSize = (float)(sizeMm * view.Scale);
        if (screenSize < 0.5)
            return;

        using var typeface = SKTypeface.FromFamilyName(text.FontFamily,
            text.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        // Itálico sintético: com o overload DrawText(..., SKFont, ...) o skew tem de estar no
        // SKFont — SKPaint.TextSkewX é ignorado nesse caminho (SkiaSharp 2.88).
        using var font = new SKFont(typeface, screenSize)
        {
            Edging = SKFontEdging.Antialias,
            SkewX = text.Italic ? -0.3f : 0f
        };
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = ColorPalette.Parse(text.Color),
            Typeface = typeface,
            TextSize = screenSize
        };

        var pos = view.WorldToScreen(new Point2D(text.X, text.Y));
        canvas.Save();
        canvas.Translate((float)pos.X, (float)pos.Y);
        if (Math.Abs(text.Rotation) > 1e-6)
            canvas.RotateDegrees((float)-text.Rotation);

        var lines = text.Text.Split('\n');
        var lineHeight = font.Metrics.Descent - font.Metrics.Ascent;
        var y = 0f;

        // Pré-calcula a largura máxima para alinhamento center/right
        var maxWidth = 0f;
        if (text.TextAlign is "center" or "right")
        {
            foreach (var line in lines)
            {
                var bounds = new SKRect();
                paint.MeasureText(line, ref bounds);
                if (bounds.Width > maxWidth) maxWidth = bounds.Width;
            }
        }

        foreach (var line in lines)
        {
            var xOffset = 0f;
            if (text.TextAlign is "center" or "right")
            {
                var lineBounds = new SKRect();
                paint.MeasureText(line, ref lineBounds);
                xOffset = text.TextAlign == "center" ? -lineBounds.Width * 0.5f : -lineBounds.Width;
            }

            canvas.DrawText(line, xOffset, y, font, paint);

            if (text.Underline || text.Strikethrough)
            {
                var metrics = font.Metrics;
                var defaultThickness = Math.Max(1f, screenSize * 0.06f);
                var bounds = new SKRect();
                paint.MeasureText(line, ref bounds);
                var width = bounds.Width;

                if (text.Underline)
                {
                    var thickness = metrics.UnderlineThickness ?? 0f;
                    if (float.IsNaN(thickness) || thickness <= 0)
                        thickness = defaultThickness;

                    // UnderlinePosition positivo = abaixo da baseline. Usamos Abs para não depender
                    // da convenção de sinal e garantir a linha SEMPRE abaixo das letras.
                    var upPos = metrics.UnderlinePosition ?? 0f;
                    var belowBaseline = (float.IsNaN(upPos) || upPos == 0)
                        ? metrics.Descent * 0.5f
                        : Math.Abs(upPos);

                    using var underlinePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = paint.Color,
                        StrokeWidth = thickness,
                        IsAntialias = true
                    };
                    var underlineY = y + belowBaseline + thickness * 0.5f;
                    canvas.DrawLine(0, underlineY, width, underlineY, underlinePaint);
                }

                if (text.Strikethrough)
                {
                    var thickness = metrics.StrikeoutThickness ?? 0f;
                    if (float.IsNaN(thickness) || thickness <= 0)
                        thickness = defaultThickness;

                    // StrikeoutPosition é tipicamente negativo (acima da baseline).
                    var strikePos = metrics.StrikeoutPosition ?? 0f;
                    var strikeY = (float.IsNaN(strikePos) || strikePos == 0)
                        ? y + (metrics.Ascent + metrics.Descent) * 0.5f
                        : y + strikePos;

                    using var strikePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = paint.Color,
                        StrokeWidth = thickness,
                        IsAntialias = true
                    };
                    canvas.DrawLine(0, strikeY, width, strikeY, strikePaint);
                }
            }

            y += lineHeight;
        }
        canvas.Restore();
    }

    /// <summary>Retângulo envolvente em mundo (para hit-test e seleção).</summary>
    public static Rect2D ComputeBounds(TextItem text)
    {
        if (string.IsNullOrEmpty(text.Text))
            return new Rect2D(text.X, text.Y, 1, 1);

        var sizeMm = text.FontSizePt * PtToMm;
        var lines = text.Text.Split('\n');

        using var typeface = SKTypeface.FromFamilyName(text.FontFamily,
            text.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Typeface = typeface ?? SKTypeface.Default,
            TextSize = (float)sizeMm
        };
        using var font = new SKFont(paint.Typeface, (float)sizeMm)
        {
            Edging = SKFontEdging.Antialias
        };

        var metrics = font.Metrics;
        var lineHeight = metrics.Descent - metrics.Ascent;
        var width = 0f;
        foreach (var line in lines)
        {
            var bounds = new SKRect();
            paint.MeasureText(line, ref bounds);
            width = Math.Max(width, bounds.Width);
        }

        var height = lines.Length * lineHeight;

        var minX = text.TextAlign switch
        {
            "center" => text.X - width * 0.5,
            "right" => text.X - width,
            _ => text.X
        };
        // A posição (X, Y) é a baseline (canto inferior esquerdo); o texto estende-se
        // para cima (ascent) e um pouco abaixo da baseline (descent).
        var minY = text.Y - metrics.Descent;
        return new Rect2D(minX, minY, width, height);
    }

    public const float HandleHalf = 5f;

    /// <summary>Retângulo de seleção (moldura) do texto em coordenadas de tela.</summary>
    public static SKRect SelectionRect(TextItem text, ViewTransform view)
    {
        var b = ComputeBounds(text);
        var pad = Math.Max(3.0 / view.Scale, 1.0);
        var tl = view.WorldToScreen(new Point2D(b.MinX - pad, b.MaxY + pad));
        var br = view.WorldToScreen(new Point2D(b.MaxX + pad, b.MinY - pad));
        return SKRect.Create((float)tl.X, (float)tl.Y, (float)(br.X - tl.X), (float)(br.Y - tl.Y));
    }

    /// <summary>Apenas a moldura tracejada (usada no hover de texto).</summary>
    public static void RenderSelectionOutline(SKCanvas canvas, TextItem text, ViewTransform view)
    {
        var rect = SelectionRect(text, view);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0)
        };
        canvas.DrawRect(rect, paint);
    }

    /// <summary>Moldura tracejada de seleção com alças de redimensionar e rotacionar.</summary>
    public static void RenderSelectionFrame(SKCanvas canvas, TextItem text, ViewTransform view)
    {
        var rect = SelectionRect(text, view);
        RenderSelectionOutline(canvas, text, view);

        // Alça de rotação (círculo acima do centro)
        var rotCenter = new SKPoint(rect.MidX, rect.Top - 20);
        using var line = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.2f,
            IsAntialias = true
        };
        canvas.DrawLine(rect.MidX, rect.Top, rotCenter.X, rotCenter.Y, line);
        using var rotFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        using var rotStroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0, 120, 215), StrokeWidth = 1.4f, IsAntialias = true };
        canvas.DrawCircle(rotCenter, HandleHalf + 2, rotFill);
        canvas.DrawCircle(rotCenter, HandleHalf + 2, rotStroke);

        // Alças de canto (redimensionar)
        using var handle = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true
        };
        using var handleStroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.2f,
            IsAntialias = true
        };
        foreach (var corner in new[]
        {
            new SKPoint(rect.Left, rect.Top),
            new SKPoint(rect.Right, rect.Top),
            new SKPoint(rect.Left, rect.Bottom),
            new SKPoint(rect.Right, rect.Bottom)
        })
        {
            canvas.DrawRect(corner.X - HandleHalf, corner.Y - HandleHalf, HandleHalf * 2, HandleHalf * 2, handle);
            canvas.DrawRect(corner.X - HandleHalf, corner.Y - HandleHalf, HandleHalf * 2, HandleHalf * 2, handleStroke);
        }
    }
}

/// <summary>Renderiza imagens da folha (com cache por caminho de arquivo).</summary>
public static class ImageItemRenderer
{
    private static readonly ConcurrentDictionary<string, SKBitmap?> Cache = new();

    public static void Render(SKCanvas canvas, ImageItem image, ViewTransform view)
    {
        if (string.IsNullOrEmpty(image.FilePath) || !System.IO.File.Exists(image.FilePath))
            return;

        var bmp = Cache.GetOrAdd(image.FilePath, path =>
        {
            try
            {
                using var data = SKData.Create(path);
                return SKBitmap.Decode(data);
            }
            catch
            {
                return null;
            }
        });
        if (bmp is null)
            return;

        var p1 = view.WorldToScreen(new Point2D(image.X, image.Y + image.Height));
        var p2 = view.WorldToScreen(new Point2D(image.X + image.Width, image.Y));
        var dest = SKRect.Create(
            (float)Math.Min(p1.X, p2.X), (float)Math.Min(p1.Y, p2.Y),
            (float)Math.Abs(p2.X - p1.X), (float)Math.Abs(p2.Y - p1.Y));
        canvas.DrawBitmap(bmp, dest);
    }

    public static Rect2D ComputeBounds(ImageItem image)
        => new(image.X, image.Y, Math.Max(image.Width, 1), Math.Max(image.Height, 1));

    /// <summary>Limpa o cache (ex.: após testes).</summary>
    public static void ClearCache() => Cache.Clear();
}
