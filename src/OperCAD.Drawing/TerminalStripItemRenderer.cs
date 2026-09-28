using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using OperCAD.Core.TerminalStrips;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>
/// Renderiza o item de borne na folha (tabela do terminal strip, como o
/// item de borne do padrão CAD): cabeçalho com o nome do borne, uma linha por nível
/// com número + terminais reais e marcador de ponte.
/// </summary>
public static class TerminalStripItemRenderer
{
    private static readonly SKColor HeaderFill = new(0xD6, 0xE4, 0xF5);
    private static readonly SKColor RowFillA = SKColors.White;
    private static readonly SKColor RowFillB = new(0xF0, 0xF0, 0xF0);
    private static readonly SKColor BorderColor = new(0x8A, 0x8A, 0x8A);

    public static void Render(SKCanvas canvas, TerminalStripItem item, TerminalStrip? strip, ViewTransform view)
    {
        var origin = view.WorldToScreen(new Point2D(item.X, item.Y));
        var widthPx = (float)(item.Width * view.Scale);
        var rowPx = (float)(item.RowHeight * view.Scale);
        var levels = strip?.PhysicalTerminals.Count ?? 0;
        var heightPx = rowPx * (levels + 1);
        var fontPx = Math.Max((float)(3.2 * view.Scale), 6f);

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), fontPx);
        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        textPaint.Typeface = font.Typeface;
        textPaint.TextSize = font.Size;

        using var border = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = BorderColor,
            StrokeWidth = Math.Max(1f, (float)(view.Scale * 0.25)),
            IsAntialias = true
        };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var bridgePaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xE6, 0x00, 0x00),
            IsAntialias = true
        };

        var rect = SKRect.Create((float)origin.X, (float)origin.Y, widthPx, heightPx);

        // Fundo da tabela
        fill.Color = RowFillA;
        canvas.DrawRect(rect, fill);

        // Cabeçalho
        fill.Color = HeaderFill;
        canvas.DrawRect(SKRect.Create(rect.Left, rect.Top, widthPx, rowPx), fill);
        if (strip is not null)
            DrawText(canvas, strip.Name, rect.Left + 3, rect.Top + rowPx * 0.72f, font, textPaint);

        // Linhas (níveis)
        var numColPx = (float)(8 * view.Scale);
        var n = 0;
        foreach (var level in strip?.PhysicalTerminals ?? (IReadOnlyList<PhysicalTerminal>)Array.Empty<PhysicalTerminal>())
        {
            n++;
            var rowTop = rect.Top + rowPx * n;
            fill.Color = n % 2 == 1 ? RowFillB : RowFillA;
            canvas.DrawRect(SKRect.Create(rect.Left, rowTop, widthPx, rowPx), fill);

            // Número do nível
            DrawText(canvas, n.ToString(), rect.Left + 3, rowTop + rowPx * 0.72f, font, textPaint);

            // Terminais reais (etiquetas dos elementos)
            var label = string.Join(" + ", level.ElementUuids.Select(id => id.ToString("N")[..6]));
            DrawText(canvas, label, rect.Left + numColPx + 3, rowTop + rowPx * 0.72f, font, textPaint);

            // Marcador de ponte
            var bridge = strip?.Bridges.FirstOrDefault(b => b.ElementUuids.Any(level.ElementUuids.Contains));
            if (bridge is not null)
            {
                bridgePaint.Color = SKColor.TryParse(bridge.Color, out var bc) ? bc : new SKColor(0xE6, 0x00, 0x00);
                var s = Math.Max(2f, (float)(2.2 * view.Scale));
                canvas.DrawRect(SKRect.Create(rect.Right - s - 3, rowTop + (rowPx - s) / 2, s, s), bridgePaint);
            }
        }

        canvas.DrawRect(rect, border);
        canvas.DrawLine(rect.Left, rect.Top + rowPx, rect.Right, rect.Top + rowPx, border);
        canvas.DrawLine(rect.Left + numColPx, rect.Top + rowPx, rect.Left + numColPx, rect.Bottom, border);
    }

    private static void DrawText(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint)
        => canvas.DrawText(text, x, y, font, paint);
}
