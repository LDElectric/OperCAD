using System.Text.RegularExpressions;
using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>
/// Renderiza um template de legenda (.titleblock) do padrão CAD dentro da área de
/// desenho, na posição inferior (Bottom) ou à direita (Right) — como
/// desenho do cartouche do padrão CAD. As alturas de linha do template são em
/// unidades padrão CAD (= mm no mundo do OperCAD).
/// </summary>
public static class TitleBlockTemplateRenderer
{
    private const string FontFamily = "Segoe UI";

    /// <summary>Altura total do template (soma das alturas das linhas), em mm.</summary>
    public static double HeightMm(TitleBlockTemplate tpl) => tpl.RowHeights.Sum();

    /// <summary>
    /// Interpreta variáveis %key e %{key} usando o resolvedor de contexto
    /// (como interpretação de variáveis do template do padrão CAD).
    /// </summary>
    public static string Interpret(string text, Func<string, string> resolver)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var result = Regex.Replace(text, @"%\{([^}]+)\}", m => resolver(m.Groups[1].Value));
        result = Regex.Replace(result, @"%([A-Za-z_][A-Za-z0-9_]*)", m => resolver(m.Groups[1].Value));
        return result;
    }

    public static void Render(SKCanvas canvas, TitleBlockTemplate tpl, ViewTransform view,
        Rect2D area, TitleBlockPosition position, Func<string, string> resolver,
        Dictionary<string, string>? customLabels = null)
    {
        if (tpl is null || tpl.RowHeights.Count == 0 || tpl.ColumnWidths.Count == 0)
            return;

        var h = HeightMm(tpl);
        var w = position == TitleBlockPosition.Bottom ? area.Width : area.Height;
        var widths = ComputeColumnWidths(tpl, w);
        if (widths.Count == 0)
            return;

        using var stroke = StrokePaint("#000000", 0.35f);

        // moldura externa do legenda
        var outer = position == TitleBlockPosition.Bottom
            ? new Rect2D(area.MinX, area.MinY, w, h)
            : new Rect2D(area.MaxX - h, area.MaxY - w, h, w);
        DrawWorldRect(canvas, outer, view, stroke);

        // células não-vazias
        foreach (var cell in tpl.Cells)
        {
            var cellRect = CellWorldRect(tpl, widths, cell, area, position);
            DrawWorldRect(canvas, cellRect, view, stroke);

            var text = BuildText(cell, resolver, customLabels);
            if (!string.IsNullOrEmpty(text))
                DrawCellText(canvas, cell, text, view, cellRect, position);
        }
    }

    /// <summary>
    /// Calcula as larguras finais das colunas em mm, reproduzindo o algoritmo
    /// larguras das colunas do template do padrão CAD (absolutas, % do total, % do restante).
    /// </summary>
    public static List<double> ComputeColumnWidths(TitleBlockTemplate tpl, double totalWidth)
    {
        if (totalWidth < 0)
            return new List<double>();

        var widths = new double[tpl.ColumnWidths.Count];
        var absoluteSum = 0.0;
        var relativeColumns = new List<int>();

        // 1ª passada: absolutas e % do total
        for (var i = 0; i < tpl.ColumnWidths.Count; i++)
        {
            var (type, value) = tpl.ColumnWidths[i];
            if (type == TitleBlockColumnLength.Absolute)
            {
                absoluteSum += value;
                widths[i] = value;
            }
            else if (type == TitleBlockColumnLength.RelativeToTotal)
            {
                var v = Math.Round(totalWidth * value / 100.0);
                absoluteSum += v;
                widths[i] = v;
                relativeColumns.Add(i);
            }
        }

        var remaining = totalWidth - absoluteSum;
        var relativeSum = 0.0;

        // 2ª passada: % do restante
        for (var i = 0; i < tpl.ColumnWidths.Count; i++)
        {
            if (tpl.ColumnWidths[i].Type == TitleBlockColumnLength.RelativeToRemaining)
            {
                var v = Math.Round(remaining * tpl.ColumnWidths[i].Value / 100.0);
                widths[i] = v;
                relativeColumns.Add(i);
                relativeSum += v;
            }
        }

        // compensa diferença de arredondamento
        if (relativeColumns.Count > 0)
        {
            var difference = (int)(totalWidth - absoluteSum - relativeSum);
            if (Math.Abs(difference) <= relativeColumns.Count * 0.5)
            {
                var share = difference > 0 ? 1 : -1;
                while (difference != 0)
                {
                    foreach (var idx in relativeColumns)
                    {
                        widths[idx] += share;
                        difference -= share;
                        if (difference == 0)
                            break;
                    }
                }
            }
        }

        return widths.ToList();
    }

    /// <summary>Texto final da célula: "rótulo: valor" quando displaylabel, senão só o valor.</summary>
    private static string BuildText(TitleBlockCell cell, Func<string, string> resolver,
        Dictionary<string, string>? customLabels = null)
    {
        var value = Interpret(cell.Value, resolver);
        var label = Interpret(cell.Label, resolver);

        // Usar rótulo customizado se disponível
        if (customLabels != null && !string.IsNullOrWhiteSpace(cell.Name))
        {
            var normalizedField = cell.Name.ToLowerInvariant().Replace(" ", "");
            if (customLabels.TryGetValue(normalizedField, out var customLabel) && !string.IsNullOrWhiteSpace(customLabel))
                label = customLabel;
        }

        if (cell.DisplayLabel && !string.IsNullOrWhiteSpace(label))
            return $"{label}: {value}";
        return value;
    }

    private static Rect2D CellWorldRect(TitleBlockTemplate tpl, IReadOnlyList<double> widths,
        TitleBlockCell cell, Rect2D area, TitleBlockPosition position)
    {
        var h = HeightMm(tpl);
        var w = position == TitleBlockPosition.Bottom ? area.Width : area.Height;

        var lx0 = Slice(widths, 0, cell.Col);
        var lx1 = Slice(widths, 0, Math.Min(cell.Col + cell.ColSpan + 1, widths.Count));
        var ly0 = Slice(tpl.RowHeights, 0, cell.Row);
        var ly1 = Slice(tpl.RowHeights, 0, Math.Min(cell.Row + cell.RowSpan + 1, tpl.RowHeights.Count));

        var a = WorldOf(position, area, lx0, ly0, w, h);
        var b = WorldOf(position, area, lx1, ly1, w, h);
        return new Rect2D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    /// <summary>Mapeia coordenadas locais do template (x→, y↓) para o mundo.</summary>
    private static Point2D WorldOf(TitleBlockPosition position, Rect2D area, double lx, double ly, double w, double h)
        => position == TitleBlockPosition.Bottom
            ? new Point2D(area.MinX + lx, area.MinY + h - ly)
            : new Point2D(area.MaxX - ly, area.MaxY - lx);

    private static double Slice(IReadOnlyList<int> values, int from, int to)
    {
        var sum = 0.0;
        for (var i = from; i < to && i < values.Count; i++)
            sum += values[i];
        return sum;
    }

    private static double Slice(IReadOnlyList<double> values, int from, int to)
    {
        var sum = 0.0;
        for (var i = from; i < to && i < values.Count; i++)
            sum += values[i];
        return sum;
    }

    private static void DrawCellText(SKCanvas canvas, TitleBlockCell cell, string text,
        ViewTransform view, Rect2D cellRect, TitleBlockPosition position)
    {
        var sizeMm = cell.FontSize > 0
            ? cell.FontSize * 0.3528
            : Math.Clamp(cellRect.Height * 0.4, 1.2, 4.5);

        using var typeface = SKTypeface.FromFamilyName(FontFamily, SKFontStyle.Normal);
        var fontSize = (float)(sizeMm * view.Scale);
        using var font = new SKFont(typeface, fontSize) { Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColor.Parse("#000000"),
            Typeface = typeface,
            TextSize = fontSize
        };

        // direção do texto na tela (a largura útil depende da rotação no caso Right)
        var fitLenMm = position == TitleBlockPosition.Bottom ? cellRect.Width : cellRect.Height;
        var maxW = fitLenMm * view.Scale;

        var measured = paint.MeasureText(text);
        if (cell.Hadjust && measured > maxW && maxW > 0)
        {
            var factor = Math.Clamp((float)(maxW / measured), 0.4f, 1f);
            font.Size *= factor;
            paint.TextSize = font.Size;
            measured = paint.MeasureText(text);
        }

        var center = view.WorldToScreen(new Point2D((cellRect.MinX + cellRect.MaxX) / 2.0, (cellRect.MinY + cellRect.MaxY) / 2.0));
        canvas.Save();
        canvas.Translate((float)center.X, (float)center.Y);
        if (position == TitleBlockPosition.Right)
            canvas.RotateDegrees(-90);

        var alignOffset = cell.Align switch
        {
            "left" => -maxW / 2f,
            "right" => maxW / 2f - measured,
            _ => -measured / 2f
        };
        var halfH = (float)(cellRect.Height * view.Scale / 2.0);
        var halfV = (float)(cellRect.Width * view.Scale / 2.0);
        float baseline = cell.Valign switch
        {
            "top" => position == TitleBlockPosition.Bottom
                ? -halfH + (float)(sizeMm * view.Scale)
                : -halfV + (float)(sizeMm * view.Scale),
            "bottom" => position == TitleBlockPosition.Bottom
                ? halfH - (float)(sizeMm * view.Scale * 0.25)
                : halfV - (float)(sizeMm * view.Scale * 0.25),
            _ => (float)(sizeMm * view.Scale * 0.35)
        };

        canvas.DrawText(text, (float)alignOffset, baseline, font, paint);
        canvas.Restore();
    }

    private static void DrawWorldRect(SKCanvas canvas, Rect2D world, ViewTransform view, SKPaint paint)
    {
        var tl = view.WorldToScreen(new Point2D(world.MinX, world.MaxY));
        var br = view.WorldToScreen(new Point2D(world.MaxX, world.MinY));
        var r = SKRect.Create((float)tl.X, (float)tl.Y, (float)(br.X - tl.X), (float)(br.Y - tl.Y));
        canvas.DrawRect(r, paint);
    }

    private static SKPaint StrokePaint(string hex, float width) => new()
    {
        Color = SKColor.Parse(hex),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width
    };
}
