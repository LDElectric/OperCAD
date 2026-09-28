using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>
/// Renderiza uma moldura de folha dirigida por dados (<see cref="FolhaTemplate"/>):
/// moldura externa, margem de encadernação, faixa superior de colunas (números),
/// faixas laterais de linhas (letras A–F) e o bloco de legenda com células.
/// As coordenadas do template são frações (0..1) da área da moldura.
/// </summary>
public static class FolhaTemplateRenderer
{
    private const string FontFamily = "Segoe UI";

    /// <summary>Retângulo da moldura (área interna = página menos o recuo).</summary>
    public static Rect2D FrameRect(PageSettings settings)
        => FolhaFrameGeometry.FrameRect(settings);

    /// <summary>
    /// Área de desenho efetiva (onde o grid deve ser desenhado): desconta as faixas de
    /// colunas (topo) e linhas (laterais) da moldura, e a área do bloco de legenda.
    /// O grid fica contido entre as duas colunas de letras (esquerda e direita).
    /// </summary>
    public static Rect2D DrawingArea(FolhaTemplate tpl, PageSettings settings)
        => FolhaFrameGeometry.DrawingArea(tpl, settings);

    public static void Render(SKCanvas canvas, FolhaTemplate tpl, Page page, ViewTransform view,
        IReadOnlyList<Page>? projectPages = null, string? folhaAutoNumFormula = null)
    {
        if (tpl is null || page is null)
            return;

        var s = page.Settings;
        var frame = FrameRect(s);
        var fw = frame.Width;
        var fh = frame.Height;

        using var thin = StrokePaint("#000000", 0.35f);
        using var thick = StrokePaint("#000000", 0.6f);

        // Moldura externa
        DrawWorldRect(canvas, frame, view, thick);

        // Margem de encadernação (linhas verticais à esquerda)
        foreach (var b in tpl.BindingLines)
        {
            var x = frame.MinX + b * fw;
            canvas.DrawLine(ToX(x, view), ToY(frame.MinY, view), ToX(x, view), ToY(frame.MaxY, view), thin);
        }

        // Faixa superior (colunas numeradas) — controlada pelas configurações da folha.
        if (s.ShowColumns)
            DrawColumnStrip(canvas, tpl, s, frame, view, thin);

        // Faixas laterais (linhas com letras A–F) — controladas pelas configurações da folha.
        if (s.ShowRows)
        {
            DrawRowStrip(canvas, tpl, s, frame, view, thin, left: true);
            if (tpl.ShowRightStrip)
                DrawRowStrip(canvas, tpl, s, frame, view, thin, left: false);
        }

        // Bloco de legenda
        DrawTitleBlock(canvas, tpl, page, frame, view, thin, projectPages, folhaAutoNumFormula);
    }

    private static void DrawColumnStrip(SKCanvas canvas, FolhaTemplate tpl, PageSettings s, Rect2D frame, ViewTransform view, SKPaint thin)
    {
        var fw = frame.Width;
        var fh = frame.Height;
        var stripH = Math.Max(0.004, tpl.ColumnStripHeight) * fh;
        var topY = frame.MaxY;
        var bottomY = topY - stripH;

        // Faixa (linha horizontal do fundo da faixa)
        canvas.DrawLine(ToX(frame.MinX, view), ToY(bottomY, view), ToX(frame.MaxX, view), ToY(bottomY, view), thin);

        // Colunas sobre a área de desenho (entre as duas colunas de letras)
        var leftX = frame.MinX + FolhaFrameGeometry.LeftStripRight(tpl, fw);
        var rightX = frame.MaxX - (tpl.ShowRightStrip ? Math.Max(0.004, tpl.RowStripWidth) * fw : 0);
        var count = Math.Max(1, s.ColumnsCount);
        var cellW = (rightX - leftX) / count;

        for (var i = 1; i < count; i++)
        {
            var x = leftX + i * cellW;
            canvas.DrawLine(ToX(x, view), ToY(bottomY, view), ToX(x, view), ToY(topY, view), thin);
        }

        for (var i = 0; i < count; i++)
        {
            var cx = leftX + (i + 0.5) * cellW;
            DrawHeaderText(canvas, view, cx, topY - stripH * 0.72, stripH, (i + 1).ToString(), thin);
        }
    }

    private static void DrawRowStrip(SKCanvas canvas, FolhaTemplate tpl, PageSettings s, Rect2D frame, ViewTransform view, SKPaint thin, bool left)
    {
        var fw = frame.Width;
        var fh = frame.Height;
        var stripW = Math.Max(0.004, tpl.RowStripWidth) * fw;

        // Faixa horizontal das letras:
        // - esquerda: a última coluna da margem de encadernação (imediatamente antes
        //   da área de desenho), quando houver margem de encadernação;
        // - direita: a faixa externa, simétrica à esquerda.
        double x0, x1;
        if (left && tpl.BindingLines.Count >= 2)
        {
            var a = tpl.BindingLines[tpl.BindingLines.Count - 2] * fw;
            var b = tpl.BindingLines[tpl.BindingLines.Count - 1] * fw;
            x0 = frame.MinX + a;
            x1 = frame.MinX + b;
        }
        else
        {
            x0 = left ? frame.MinX : frame.MaxX - stripW;
            x1 = left ? frame.MinX + stripW : frame.MaxX;
        }
        var cx = (x0 + x1) / 2;

        var topY = frame.MaxY - Math.Max(0.004, tpl.ColumnStripHeight) * fh;
        var bottomY = frame.MaxY - tpl.RowsStripEnd * fh;
        var count = Math.Max(1, s.RowsCount);
        var cellH = (topY - bottomY) / count;

        // Fecha a faixa nas duas bordas verticais.
        canvas.DrawLine(ToX(x0, view), ToY(bottomY, view), ToX(x0, view), ToY(topY, view), thin);
        canvas.DrawLine(ToX(x1, view), ToY(bottomY, view), ToX(x1, view), ToY(topY, view), thin);

        for (var i = 1; i < count; i++)
        {
            var y = topY - i * cellH;
            canvas.DrawLine(ToX(x0, view), ToY(y, view), ToX(x1, view), ToY(y, view), thin);
        }

        // Letra de cada fileira (A, B, C...), centralizada na faixa, em ambos os lados.
        for (var i = 0; i < count; i++)
        {
            var rowCenterY = topY - (i + 0.5) * cellH;
            DrawHeaderText(canvas, view, cx, rowCenterY, cellH, ColumnLetter(i), thin);
        }
    }

    private static string ColumnLetter(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            index--;
            s = (char)('A' + index % 26) + s;
            index /= 26;
        }
        return s;
    }

    private static void DrawHeaderText(SKCanvas canvas, ViewTransform view, double cx, double cy, double h, string text, SKPaint thin)
    {
        var size = Math.Clamp(h * 0.55, 1.2, 2.4) * view.Scale;
        if (size < 0.6)
            return;
        using var paint = new SKPaint
        {
            Color = SKColor.Parse("#000000"),
            IsAntialias = true,
            TextSize = (float)size,
            Typeface = SKTypeface.FromFamilyName(FontFamily, SKFontStyle.Normal)
        };
        var screen = view.WorldToScreen(new Point2D(cx, cy));
        var measured = paint.MeasureText(text);
        canvas.DrawText(text, (float)(screen.X - measured / 2), (float)screen.Y, paint);
    }

    private static void DrawTitleBlock(SKCanvas canvas, FolhaTemplate tpl, Page page, Rect2D frame, ViewTransform view,
        SKPaint thin, IReadOnlyList<Page>? projectPages, string? folhaAutoNumFormula)
    {
        var fw = frame.Width;
        var fh = frame.Height;
        var left = frame.MinX + tpl.TitleBlockLeft * fw;
        var right = frame.MaxX;
        var top = frame.MaxY - tpl.TitleBlockTop * fh;
        var bottom = frame.MaxY - tpl.TitleBlockBottom * fh;

        // Borda do bloco de legenda
        var outer = new Rect2D(left, top, right - left, bottom - top);
        DrawWorldRect(canvas, outer, view, thin);

        foreach (var cell in tpl.TitleBlockCells)
        {
            var rect = new Rect2D(
                frame.MinX + cell.X * fw,
                frame.MaxY - (cell.Y + cell.H) * fh,
                cell.W * fw,
                cell.H * fh);
            DrawWorldRect(canvas, rect, view, thin);

            if (cell.IsNorth)
            {
                DrawNorthSymbol(canvas, view, rect, thin);
                continue;
            }

            var value = string.IsNullOrWhiteSpace(cell.Field)
                ? string.Empty
                : ResolveField(page, cell.Field, projectPages, folhaAutoNumFormula);
            var hasLabel = !string.IsNullOrWhiteSpace(cell.Label);
            var hasValue = !string.IsNullOrWhiteSpace(value);

            // Estilo personalizado do campo (FontSize, Bold, Italic, FontFamily)
            var normalizedField = string.IsNullOrWhiteSpace(cell.Field)
                ? string.Empty
                : DrawingFrameRenderer.NormalizeField(cell.Field);
            page.TitleBlock.FieldStyles.TryGetValue(normalizedField, out var fieldStyle);

            // Usar rótulo customizado se disponível
            var label = cell.Label;
            if (!string.IsNullOrWhiteSpace(normalizedField) && page.TitleBlock.FieldLabels.TryGetValue(normalizedField, out var customLabel) && !string.IsNullOrWhiteSpace(customLabel))
                label = customLabel;

            // Célula com rótulo E valor: rótulo no topo e valor abaixo (ex.: EMPREENDIMENTO).
            if (hasLabel && hasValue)
            {
                DrawCellLabelValue(canvas, view, rect, label, value, cell.Align, cell.Valign, fieldStyle);
                continue;
            }

            var text = hasValue ? value : cell.Label;
            if (string.IsNullOrWhiteSpace(text))
                continue;
            DrawCellText(canvas, view, rect, text, cell.Align, cell.Valign, fieldStyle);
        }
    }

    private static void DrawNorthSymbol(SKCanvas canvas, ViewTransform view, Rect2D rect, SKPaint thin)
    {
        var cx = rect.MinX + rect.Width / 2;
        var cy = rect.MinY + rect.Height / 2;
        var r = Math.Min(rect.Width, rect.Height) * 0.32;
        using var paint = StrokePaint("#000000", (float)Math.Max(0.3, r * 0.16));
        canvas.DrawCircle(ToX(cx, view), ToY(cy, view), (float)(r * view.Scale), paint);

        // seta do norte
        var north = new Point2D(cx, cy + r * 0.85);
        var arrow = new[]
        {
            new Point2D(cx - r * 0.35, cy + r * 0.55),
            new Point2D(cx + r * 0.35, cy + r * 0.55),
            new Point2D(cx, north.Y)
        };
        using var arrowPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black };
        using var path = new SKPath();
        var a0 = view.WorldToScreen(arrow[0]);
        var a1 = view.WorldToScreen(arrow[1]);
        var a2 = view.WorldToScreen(arrow[2]);
        path.MoveTo((float)a0.X, (float)a0.Y);
        path.LineTo((float)a1.X, (float)a1.Y);
        path.LineTo((float)a2.X, (float)a2.Y);
        path.Close();
        canvas.DrawPath(path, arrowPaint);
    }

    private static void DrawCellText(SKCanvas canvas, ViewTransform view, Rect2D rect, string text, string align, string valign,
        OperCAD.Core.Documents.TitleBlockFieldStyle? style = null)
    {
        var cellH = rect.Height;
        var padY = Math.Max(cellH * 0.08, 0.3);
        // Se houver estilo de fonte personalizado, usa o FontSizePt dele; caso contrário, calcula pelo tamanho da célula
        float size;
        if (style?.FontSizePt is { } customPt)
            size = (float)(customPt * 0.3528 * view.Scale); // pt -> mm -> screen
        else
            size = (float)(Math.Clamp(cellH * 0.42, 1.0, 2.2) * view.Scale);
        if (size < 0.6f)
            return;

        var fontFamily = style?.FontFamily ?? FontFamily;
        var bold = style?.Bold == true;
        var italic = style?.Italic == true;
        var skStyle = (bold, italic) switch
        {
            (true, true)  => SKFontStyle.BoldItalic,
            (true, false) => SKFontStyle.Bold,
            (false, true) => SKFontStyle.Italic,
            _             => SKFontStyle.Normal
        };

        using var paint = new SKPaint
        {
            Color = SKColor.Parse("#000000"),
            IsAntialias = true,
            TextSize = size,
            Typeface = SKTypeface.FromFamilyName(fontFamily, skStyle)
        };

        if (string.IsNullOrWhiteSpace(text))
            return;

        var cellW = rect.Width * view.Scale;
        var lines = WrapText(paint, text, cellW);

        if (lines.Count <= 1)
        {
            var measured = paint.MeasureText(text);
            if (measured > cellW && cellW > 0)
                paint.TextSize *= Math.Clamp((float)(cellW / measured), 0.45f, 1f);
            lines = WrapText(paint, text, cellW);
        }

        var center = view.WorldToScreen(new Point2D(rect.MinX + rect.Width / 2, rect.MinY + rect.Height / 2));
        var halfW = (float)(rect.Width * view.Scale / 2);
        var halfH = (float)(rect.Height * view.Scale / 2);
        var padPx = (float)(padY * view.Scale);
        var lineH = (float)(paint.TextSize * 1.3);
        var blockH = lineH * lines.Count;
        var top = valign switch
        {
            "top" => (float)center.Y - halfH + padPx + paint.TextSize,
            "bottom" => (float)center.Y + halfH - padPx - blockH + paint.TextSize,
            _ => (float)center.Y - blockH / 2 + paint.TextSize
        };

        // Se texto em múltiplas linhas e sem alinhamento explícito -> justificar à esquerda
        var effectiveAlign = (lines.Count > 1 && align == "center") ? "left" : align;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var m = paint.MeasureText(line);
            float x = effectiveAlign switch
            {
                "left" => (float)center.X - halfW + padPx,
                "right" => (float)center.X + halfW - m - padPx,
                _ => (float)center.X - m / 2
            };
            canvas.DrawText(line, x, top + lineH * i, paint);
        }
    }

    /// <summary>
    /// Desenha uma célula com rótulo e valor em DUAS linhas: o rótulo (negrito, maior) no
    /// topo e o valor (regular) abaixo — como em EMPREENDIMENTO / DESCRIÇÃO.
    /// Valores curtos são centralizados; valores longos são justificados à esquerda com
    /// quebra de linha automática.
    /// </summary>
    private static void DrawCellLabelValue(SKCanvas canvas, ViewTransform view, Rect2D rect, string label, string value, string align, string valign,
        OperCAD.Core.Documents.TitleBlockFieldStyle? style = null)
    {
        var cellH = rect.Height;
        var padY = Math.Max(cellH * 0.08, 0.3);
        var labelMm = Math.Clamp(cellH * 0.22, 1.2, 2.4);
        var valueMm = Math.Clamp(cellH * 0.26, 1.2, 3.0);
        if (valueMm * view.Scale < 0.6)
            return;

        using var labelPaint = new SKPaint
        {
            Color = SKColor.Parse("#606060"),
            IsAntialias = true,
            TextSize = (float)(labelMm * view.Scale),
            Typeface = SKTypeface.FromFamilyName(FontFamily, SKFontStyle.Bold)
        };

        // Estilo customizado só se aplica ao VALOR; o rótulo sempre usa a fonte padrão.
        var fontFamily = style?.FontFamily ?? FontFamily;
        var bold = style?.Bold == true;
        var italic = style?.Italic == true;
        var skStyle = (bold, italic) switch
        {
            (true, true)  => SKFontStyle.BoldItalic,
            (true, false) => SKFontStyle.Bold,
            (false, true) => SKFontStyle.Italic,
            _             => SKFontStyle.Normal
        };
        float valueSizePx;
        if (style?.FontSizePt is { } customPt)
            valueSizePx = (float)(customPt * 0.3528 * view.Scale);
        else
            valueSizePx = (float)(valueMm * view.Scale);
        using var valuePaint = new SKPaint
        {
            Color = SKColor.Parse("#000000"),
            IsAntialias = true,
            TextSize = valueSizePx,
            Typeface = SKTypeface.FromFamilyName(fontFamily, skStyle)
        };

        var center = view.WorldToScreen(new Point2D(rect.MinX + rect.Width / 2, rect.MinY + rect.Height / 2));
        var halfW = (float)(rect.Width * view.Scale / 2);
        var halfH = (float)(rect.Height * view.Scale / 2);
        var padPx = (float)(padY * view.Scale);
        var gap = (float)(Math.Max(labelMm, valueMm) * 0.55 * view.Scale);
        var cx = (float)center.X;
        var cy = (float)center.Y;

        // Valor: quebra em linhas que cabem na largura da célula. Uma única linha curta é
        // centralizada; múltiplas linhas (ou uma linha longa) são justificadas à esquerda.
        var cellW = rect.Width * view.Scale;
        var lines = WrapText(valuePaint, value, cellW);
        var oneLine = lines.Count <= 1 && valuePaint.MeasureText(value) <= cellW;
        var valueLineH = (float)(valuePaint.TextSize * 1.3);
        var valueBlockH = valueLineH * lines.Count;
        var labelM = labelPaint.MeasureText(label);

        // Posição vertical: título com padding do topo, valor abaixo com gap confortável.
        float labelY;
        if (valign == "top")
        {
            labelY = cy - halfH + padPx + labelPaint.TextSize;
        }
        else if (valign == "bottom")
        {
            var bottom = cy + halfH - padPx;
            var valueBottom = bottom;
            var labelBottom = valueBottom - valueBlockH - gap;
            labelY = labelBottom;
        }
        else
        {
            var blockH = labelPaint.TextSize + gap + valueBlockH;
            var top = cy - halfH + padPx;
            var topAvail = (halfH * 2) - (padPx * 2);
            labelY = top + Math.Max(0, (topAvail - blockH) / 2) + labelPaint.TextSize;
        }

        var valueTop = labelY + labelPaint.TextSize + gap;

        if (valign == "top")
        {
            var availH = (cy + halfH - padPx) - (labelY + gap);
            if (oneLine && availH > valueBlockH)
                valueTop = labelY + gap + (availH - valueBlockH) / 2 + valuePaint.TextSize;
        }

        // Título centralizado.
        var labelX = align switch
        {
            "left" => cx - halfW + padPx,
            "right" => cx + halfW - labelM - padPx,
            _ => cx - labelM / 2
        };

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var m = valuePaint.MeasureText(line);
            float vx = oneLine
                ? cx - m / 2
                : cx - halfW + padPx;
            canvas.DrawText(line, vx, valueTop + valueLineH * i, valuePaint);
        }

        canvas.DrawText(label, labelX, labelY, labelPaint);
    }

    /// <summary>
    /// Quebra <paramref name="text"/> em linhas que cabem em <paramref name="maxWidthPx"/>
    /// (em pixels de tela), sempre quebrando em espaços. Uma única palavra mais larga que a
    /// célula é truncada na largura disponível.
    /// </summary>
    private static IReadOnlyList<string> WrapText(SKPaint paint, string text, double maxWidthPx)
    {
        var result = new List<string>();
        if (maxWidthPx <= 0 || string.IsNullOrEmpty(text))
        {
            result.Add(text ?? string.Empty);
            return result;
        }

        foreach (var paragraph in text.Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (paint.MeasureText(candidate) <= maxWidthPx)
                {
                    line = candidate;
                }
                else
                {
                    if (line.Length > 0)
                    {
                        result.Add(line);
                        line = word;
                    }
                    else
                    {
                        // Palavra isolada maior que a célula: trunca com reticências.
                        result.Add(TruncateToWidth(paint, word, maxWidthPx));
                        line = string.Empty;
                    }
                }
            }
            if (line.Length > 0)
                result.Add(line);
        }

        if (result.Count == 0)
            result.Add(string.Empty);
        return result;
    }

    private static string TruncateToWidth(SKPaint paint, string word, double maxWidthPx)
    {
        if (string.IsNullOrEmpty(word) || paint.MeasureText(word) <= maxWidthPx)
            return word;
        var ellipsis = "\u2026";
        var max = Math.Max(1, maxWidthPx - paint.MeasureText(ellipsis));
        var chars = word.Length;
        while (chars > 0 && paint.MeasureText(word.Substring(0, chars)) > max)
            chars--;
        return word.Substring(0, Math.Max(1, chars)) + ellipsis;
    }

    /// <summary>Resolve o valor de um campo da legenda (author, title, scale...).</summary>
    private static string ResolveField(Page page, string field, IReadOnlyList<Page>? projectPages, string? folhaAutoNumFormula)
    {
        var t = page.TitleBlock;
        return field.ToLowerInvariant() switch
        {
            "author" or "autor" => t.Author ?? string.Empty,
            "revpor" => t.ReviewedBy ?? string.Empty,
            "aprovpor" or "approvedby" => t.ApprovedBy ?? string.Empty,
            "reviewedby" or "revisadopor" or "revisadopordrawn" => t.ReviewedBy ?? string.Empty,
            "title" or "descricao" => t.Title ?? string.Empty,
            "company" or "empreendimento" or "project" => t.Company ?? string.Empty,
            "obra" or "plant" => t.Obra ?? string.Empty,
            "drawingnumber" or "registro" => t.DrawingNumber ?? string.Empty,
            "revision" or "rev" => t.Revision ?? string.Empty,
            "scale" or "escala" => t.Scale ?? string.Empty,
            "sheet" or "pagina" => t.Sheet ?? string.Empty,
            "totalsheets" or "totalpaginas" => t.TotalSheets ?? string.Empty,
            "date" or "data" => t.Date.ToString("dd/MM/yyyy"),
            "id" => AutoNum(page, projectPages, folhaAutoNumFormula),
            _ => string.Empty
        };
    }

    private static string AutoNum(Page page, IReadOnlyList<Page>? projectPages, string? folhaAutoNumFormula)
    {
        if (string.IsNullOrEmpty(folhaAutoNumFormula))
            return page.TitleBlock.Sheet ?? string.Empty;
        var total = projectPages?.Count ?? 1;
        var id = string.IsNullOrWhiteSpace(page.TitleBlock.Sheet) ? "1" : page.TitleBlock.Sheet;
        return folhaAutoNumFormula
            .Replace("%id", id)
            .Replace("%total", total.ToString());
    }

    private static void DrawWorldRect(SKCanvas canvas, Rect2D world, ViewTransform view, SKPaint paint)
    {
        var tl = view.WorldToScreen(new Point2D(world.MinX, world.MaxY));
        var br = view.WorldToScreen(new Point2D(world.MaxX, world.MinY));
        var r = SKRect.Create((float)tl.X, (float)tl.Y, (float)(br.X - tl.X), (float)(br.Y - tl.Y));
        canvas.DrawRect(r, paint);
    }

    private static float ToX(double x, ViewTransform view) => (float)view.WorldToScreen(new Point2D(x, 0)).X;
    private static float ToY(double y, ViewTransform view) => (float)view.WorldToScreen(new Point2D(0, y)).Y;

    private static SKPaint StrokePaint(string hex, float width) => new()
    {
        Color = SKColor.Parse(hex),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width
    };
}
