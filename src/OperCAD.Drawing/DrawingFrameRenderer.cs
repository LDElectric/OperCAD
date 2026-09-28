using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>
/// Desenha a moldura técnica da folha (estilo padrão CAD/desenho técnico):
/// borda dupla (externa espessa + interna fina), marcas de centragem e a legenda
/// (quadro de identificação) no canto inferior direito. Tudo em milímetros no mundo.
/// </summary>
public static class DrawingFrameRenderer
{
    private const string FontFamily = "Segoe UI";

    /// <summary>Campo editável da legenda encontrado sob o cursor.</summary>
    public sealed record TitleBlockFieldHit(string Field, string Label, Rect2D Bounds);

    private static readonly HashSet<string> EditableTitleBlockFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "author", "revpor", "reviewedby", "revisadopor", "aprovpor", "approvedby",
        "company", "empreendimento", "title", "descricao", "drawingnumber", "registro",
        "revision", "rev", "sheet", "pagina", "totalsheets", "totalpaginas"
    };

    public static void DrawFrame(SKCanvas canvas, Page page, ViewTransform view,
        IReadOnlyList<Page>? projectPages = null, string? folhaAutoNumFormula = null)
    {
        if (page is null || !page.Settings.ShowFrame)
            return;

        // Moldura dirigida por dados (template.json) — substitui a moldura padrão.
        if (page.Settings.FolhaTemplate is { } folhaTpl)
        {
            FolhaTemplateRenderer.Render(canvas, folhaTpl, page, view, projectPages, folhaAutoNumFormula);
            return;
        }

        var w = page.Settings.Width;
        var h = page.Settings.Height;
        var inner = page.Settings.DrawingBounds;

        using (var outer = StrokePaint("#000000", 0.8f))
        {
            var r = ToScreen(new Rect2D(0.5, 0.5, w - 1, h - 1), view);
            canvas.DrawRect(r, outer);
        }

        using (var thin = StrokePaint("#000000", 0.35f))
        {
            var r = ToScreen(inner, view);
            canvas.DrawRect(r, thin);
        }

        if (page.Settings.ShowColumns)
            DrawColumnHeaders(canvas, page, view);
        if (page.Settings.ShowRows)
            DrawRowHeaders(canvas, page, view);

        DrawInteriorGrid(canvas, page, view);

        using (var mark = StrokePaint("#000000", 0.35f))
            DrawCenteringMarks(canvas, view, w, h, mark);
    }

    /// <summary>Linhas internas que dividem a área de desenho em colunas e linhas (como no padrão CAD).</summary>
    private static void DrawInteriorGrid(SKCanvas canvas, Page page, ViewTransform view)
    {
        var inner = page.Settings.DrawingBounds;
        var count = Math.Max(page.Settings.ColumnsCount, 1);
        var rows = Math.Max(page.Settings.RowsCount, 1);
        using var stroke = StrokePaint("#000000", 0.18f);

        // Divisores verticais (colunas)
        if (count > 1)
        {
            var cellW = inner.Width / count;
            for (var i = 1; i < count; i++)
            {
                var x = inner.MinX + i * cellW;
                canvas.DrawLine(ToX(x, view), ToY(inner.MinY, view), ToX(x, view), ToY(inner.MaxY, view), stroke);
            }
        }

        // Divisores horizontais (linhas)
        if (rows > 1)
        {
            var cellH = inner.Height / rows;
            for (var i = 1; i < rows; i++)
            {
                var y = inner.MinY + i * cellH;
                canvas.DrawLine(ToX(inner.MinX, view), ToY(y, view), ToX(inner.MaxX, view), ToY(y, view), stroke);
            }
        }
    }

    /// <summary>Cabeçalhos das colunas (letras A, B, C...) na faixa superior do quadro.</summary>
    private static void DrawColumnHeaders(SKCanvas canvas, Page page, ViewTransform view)
    {
        var inner = page.Settings.DrawingBounds;
        var hh = Math.Clamp(page.Settings.ColumnsHeaderHeight, 4, 20);
        var top = inner.MaxY;
        var bandBottom = top - hh;
        var count = Math.Max(page.Settings.ColumnsCount, 1);
        var cellW = inner.Width / count;

        using var stroke = StrokePaint("#000000", 0.35f);
        canvas.DrawLine(ToX(inner.MinX, view), ToY(bandBottom, view), ToX(inner.MaxX, view), ToY(bandBottom, view), stroke);

        for (var i = 0; i < count; i++)
        {
            var x0 = inner.MinX + i * cellW;
            var x1 = x0 + cellW;
            canvas.DrawLine(ToX(x1, view), ToY(bandBottom, view), ToX(x1, view), ToY(top, view), stroke);
            DrawHeaderLabel(canvas, view, x0, bandBottom, cellW, hh, ColumnLetter(i));
        }
    }

    /// <summary>Cabeçalhos das linhas (números 1, 2, 3...) na faixa esquerda do quadro.</summary>
    private static void DrawRowHeaders(SKCanvas canvas, Page page, ViewTransform view)
    {
        var inner = page.Settings.DrawingBounds;
        var rw = Math.Clamp(page.Settings.RowsHeaderWidth, 4, 20);
        var left = inner.MinX;
        var bandRight = left + rw;
        var top = inner.MaxY;
        if (page.Settings.ShowColumns)
            top -= Math.Clamp(page.Settings.ColumnsHeaderHeight, 4, 20);
        var count = Math.Max(page.Settings.RowsCount, 1);
        var cellH = (top - inner.MinY) / count;

        using var stroke = StrokePaint("#000000", 0.35f);
        canvas.DrawLine(ToX(bandRight, view), ToY(inner.MinY, view), ToX(bandRight, view), ToY(top, view), stroke);

        for (var i = 0; i < count; i++)
        {
            var y1 = top - i * cellH;
            var y0 = y1 - cellH;
            canvas.DrawLine(ToX(left, view), ToY(y0, view), ToX(bandRight, view), ToY(y0, view), stroke);
            DrawHeaderLabel(canvas, view, left, y0, rw, cellH, (i + 1).ToString());
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

    private static void DrawHeaderLabel(SKCanvas canvas, ViewTransform view, double x, double y, double w, double h, string text)
    {
        var fontSize = Math.Clamp(h * 0.5, 1.6, 3.2);
        var size = fontSize * view.Scale;
        if (size < 0.6)
            return;
        using var paint = new SKPaint
        {
            Color = SKColor.Parse("#000000"),
            IsAntialias = true,
            TextSize = (float)size,
            Typeface = SKTypeface.FromFamilyName(FontFamily, SKFontStyle.Normal)
        };
        var measured = paint.MeasureText(text);
        var centerX = x + (w - measured / view.Scale) / 2;
        var baselineY = y + h * 0.66;
        var screen = view.WorldToScreen(new Point2D(centerX, baselineY));
        canvas.DrawText(text, (float)screen.X, (float)screen.Y, paint);
    }

    public static void DrawTitleBlock(SKCanvas canvas, Page page, ViewTransform view,
        IReadOnlyList<Page>? projectPages = null, string? folhaAutoNumFormula = null)
    {
        if (page is null || !page.Settings.ShowFrame)
            return;

        // Moldura dirigida por dados já desenha o bloco de legenda (template.json).
        if (page.Settings.FolhaTemplate is not null)
            return;

        var inner = page.Settings.DrawingBounds;
        var tb = TitleBlockWorldBounds(page);
        if (tb is null)
            return;

        // Template de legenda .titleblock selecionado (fiel ao padrão CAD)
        if (page.Settings.TitleBlock is { } tpl)
        {
            TitleBlockTemplateRenderer.Render(canvas, tpl, view, inner, page.Settings.TitleBlockPosition,
                key => ResolveVariable(page, key, projectPages, folhaAutoNumFormula),
                page.TitleBlock.FieldLabels);
            return;
        }

        // Fallback: legenda fixa estilo ISO7200 ("Padrão OperCAD")
        var left = tb.Value.MinX;
        var bottom = tb.Value.MinY;
        var tbW = tb.Value.Width;
        var tbH = tb.Value.Height;

        using var fill = FillPaint("#FFFFFF");
        using var stroke = StrokePaint("#000000", 0.35f);
        using var thick = StrokePaint("#000000", 0.6f);

        var r = ToScreen(tb.Value, view);
        canvas.DrawRect(r, fill);
        canvas.DrawRect(r, thick);

        var rightCols = new[] { 0.44, 0.18, 0.38 };
        var rightW = tbW * 0.52;
        var rowH = tbH * 0.5;
        var colX = new double[] { left, left + rightW * rightCols[0], left + rightW * (rightCols[0] + rightCols[1]) };
        var row2Bottom = bottom + rowH;

        // grade
        canvas.DrawLine(ToX(colX[1], view), ToY(bottom, view), ToX(colX[1], view), ToY(bottom + tbH, view), stroke);
        canvas.DrawLine(ToX(colX[2], view), ToY(bottom, view), ToX(colX[2], view), ToY(bottom + tbH, view), stroke);
        canvas.DrawLine(ToX(left + rightW, view), ToY(row2Bottom, view), ToX(left + rightW, view), ToY(bottom + tbH, view), stroke);
        canvas.DrawLine(ToX(left + rightW, view), ToY(bottom, view), ToX(left + rightW, view), ToY(bottom + tbH, view), stroke);

        var tbData = page.TitleBlock;
        var date = tbData.Date.ToString("dd/MM/yyyy");

        DrawCell(canvas, view, colX[0], bottom, colX[1] - colX[0], rowH, "DESENHO N.º", tbData.DrawingNumber);
        DrawCell(canvas, view, colX[1], bottom, colX[2] - colX[1], rowH, "REV", tbData.Revision);
        DrawCell(canvas, view, colX[2], bottom, left + rightW - colX[2], rowH, "FOLHA", $"{tbData.Sheet}/{tbData.TotalSheets}");
        DrawCell(canvas, view, colX[0], row2Bottom, colX[1] - colX[0], rowH, "ESCALA", tbData.Scale);
        DrawCell(canvas, view, colX[1], row2Bottom, colX[2] - colX[1], rowH, "DATA", date);
        DrawCell(canvas, view, colX[2], row2Bottom, left + rightW - colX[2], rowH, "AUTOR", tbData.Author);

        // área principal: título + empresa
        var mainLeft = left + rightW + tbW * 0.02;
        var mainW = tbW - rightW - tbW * 0.04;
        canvas.DrawLine(ToX(left + rightW, view), ToY(bottom, view), ToX(left + rightW, view), ToY(bottom + tbH, view), stroke);

        var title = string.IsNullOrWhiteSpace(tbData.Title) ? "PROJETO SEM TÍTULO" : tbData.Title;
        DrawText(canvas, view, mainLeft, bottom + tbH * 0.32, mainW, title, Clamp(tbH * 0.28, 2.6, 5), true);

        var company = string.IsNullOrWhiteSpace(tbData.Company) ? tbData.Author : tbData.Company;
        if (!string.IsNullOrWhiteSpace(company))
            DrawText(canvas, view, mainLeft, bottom + tbH * 0.08, mainW, company, Clamp(tbH * 0.13, 1.4, 2.2), false, "#404040");
    }

    /// <summary>
    /// Encontra o campo editável da legenda sob um ponto do mundo. O hit-test usa os mesmos
    /// retângulos que o renderer, por isso o duplo clique não depende da largura do texto atual.
    /// </summary>
    public static TitleBlockFieldHit? HitTestTitleBlockField(Page page, Point2D world)
    {
        if (page is null || !page.Settings.ShowFrame)
            return null;

        var inner = page.Settings.DrawingBounds;
        if (page.Settings.FolhaTemplate is { } folhaTemplate)
        {
            var frame = FolhaTemplateRenderer.FrameRect(page.Settings);
            var fw = frame.Width;
            var fh = frame.Height;
            foreach (var cell in folhaTemplate.TitleBlockCells)
            {
                if (string.IsNullOrWhiteSpace(cell.Field) || !EditableTitleBlockFields.Contains(cell.Field))
                    continue;
                var rect = new Rect2D(
                    frame.MinX + cell.X * fw,
                    frame.MaxY - (cell.Y + cell.H) * fh,
                    cell.W * fw,
                    cell.H * fh);
                if (rect.Contains(world))
                    return new TitleBlockFieldHit(NormalizeField(cell.Field), cell.Label, rect);
            }
            return null;
        }

        // Layout fallback ISO7200, mantido para projetos antigos sem template.json.
        var tb = TitleBlockWorldBounds(page);
        if (tb is null || !tb.Value.Contains(world))
            return null;
        var left = tb.Value.MinX;
        var bottom = tb.Value.MinY;
        var tbW = tb.Value.Width;
        var tbH = tb.Value.Height;
        var rightW = tbW * 0.52;
        var rowH = tbH * 0.5;
        var rightCols = new[] { 0.44, 0.18, 0.38 };
        var colX = new[] { left, left + rightW * rightCols[0], left + rightW * (rightCols[0] + rightCols[1]) };
        var row2Bottom = bottom + rowH;
        var fields = new[]
        {
            ("drawingnumber", "DESENHO N.º", new Rect2D(colX[0], bottom, colX[1] - colX[0], rowH)),
            ("revision", "REV", new Rect2D(colX[1], bottom, colX[2] - colX[1], rowH)),
            ("sheet", "FOLHA", new Rect2D(colX[2], bottom, left + rightW - colX[2], rowH)),
            ("author", "AUTOR", new Rect2D(colX[2], row2Bottom, left + rightW - colX[2], rowH)),
            ("title", "DESCRIÇÃO DO DESENHO", new Rect2D(left + rightW, bottom + tbH * 0.32, tbW - rightW, tbH * 0.32)),
            ("company", "EMPREENDIMENTO", new Rect2D(left + rightW, bottom, tbW - rightW, tbH * 0.32))
        };
        foreach (var field in fields)
        {
            if (field.Item3.Contains(world))
                return new TitleBlockFieldHit(field.Item1, field.Item2, field.Item3);
        }
        return null;
    }

    internal static string NormalizeField(string field) => field.ToLowerInvariant() switch
    {
        "reviewedby" or "revisadopor" => "revpor",
        "approvedby" => "aprovpor",
        "empreendimento" => "company",
        "descricao" => "title",
        "registro" => "drawingnumber",
        "rev" => "revision",
        "pagina" => "sheet",
        "totalpaginas" => "totalsheets",
        _ => field.ToLowerInvariant()
    };

    private static void DrawCell(SKCanvas canvas, ViewTransform view, double x, double y, double w, double h, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            value = "—";
        DrawText(canvas, view, x + w * 0.06, y + h * 0.68, w * 0.88, label, Clamp(h * 0.16, 1.1, 1.7), false, "#606060");
        DrawText(canvas, view, x + w * 0.06, y + h * 0.22, w * 0.88, value, Clamp(h * 0.22, 1.8, 2.8), true);
    }

    private static void DrawText(SKCanvas canvas, ViewTransform view, double x, double y, double maxWidth, string text, double fontSize, bool bold, string color = "#000000")
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var size = fontSize * view.Scale;
        if (size < 0.6)
            return;

        using var paint = new SKPaint
        {
            Color = SKColor.Parse(color),
            IsAntialias = true,
            TextSize = (float)size,
            Typeface = SKTypeface.FromFamilyName(FontFamily, bold ? SKFontStyle.Bold : SKFontStyle.Normal)
        };

        var screen = view.WorldToScreen(new Point2D(x, y));
        var maxW = maxWidth * view.Scale;
        var measured = paint.MeasureText(text);
        if (measured > maxW && maxW > 0)
            paint.TextSize *= Math.Clamp((float)(maxW / measured), 0.5f, 1f);

        canvas.DrawText(text, (float)screen.X, (float)screen.Y, paint);
    }

    private static void DrawCenteringMarks(SKCanvas canvas, ViewTransform view, double w, double h, SKPaint paint)
    {
        const double len = 3.0, gap = 1.2;
        void Mark(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            var a = view.WorldToScreen(new Point2D(x1, y1));
            var b = view.WorldToScreen(new Point2D(x2, y2));
            var c = view.WorldToScreen(new Point2D(x3, y3));
            using var path = new SKPath();
            path.MoveTo((float)a.X, (float)a.Y);
            path.LineTo((float)b.X, (float)b.Y);
            path.LineTo((float)c.X, (float)c.Y);
            canvas.DrawPath(path, paint);
        }

        Mark(w / 2 - len, gap, w / 2, gap + len, w / 2 + len, gap);
        Mark(w / 2 - len, h - gap, w / 2, h - gap - len, w / 2 + len, h - gap);
        Mark(gap, h / 2 - len, gap + len, h / 2, gap, h / 2 + len);
        Mark(w - gap, h / 2 - len, w - gap - len, h / 2, w - gap, h / 2 + len);
    }

    private static float ToX(double x, ViewTransform view) => (float)view.WorldToScreen(new Point2D(x, 0)).X;
    private static float ToY(double y, ViewTransform view) => (float)view.WorldToScreen(new Point2D(0, y)).Y;

    private static SKRect ToScreen(Rect2D world, ViewTransform view)
    {
        var topLeft = view.WorldToScreen(new Point2D(world.MinX, world.MaxY));
        var bottomRight = view.WorldToScreen(new Point2D(world.MaxX, world.MinY));
        return SKRect.Create((float)topLeft.X, (float)topLeft.Y, (float)(bottomRight.X - topLeft.X), (float)(bottomRight.Y - topLeft.Y));
    }

    /// <summary>
    /// Retângulo (mundo) ocupado pelo legenda na folha — usado pelo render e pelo
    /// hit-test do duplo-clique (abrir "Propriedades da folha", como o padrão CAD).
    /// </summary>
    public static Rect2D? TitleBlockWorldBounds(Page page)
    {
        if (page is null || !page.Settings.ShowFrame)
            return null;

        var inner = page.Settings.DrawingBounds;

        if (page.Settings.FolhaTemplate is { } folhaTpl)
        {
            var frame = FolhaTemplateRenderer.FrameRect(page.Settings);
            var fw = frame.Width;
            var fh = frame.Height;
            var left = frame.MinX + folhaTpl.TitleBlockLeft * fw;
            var right = frame.MaxX;
            var top = frame.MaxY - folhaTpl.TitleBlockTop * fh;
            var bottom = frame.MaxY - folhaTpl.TitleBlockBottom * fh;
            return new Rect2D(left, bottom, right - left, top - bottom);
        }

        // Template .titleblock (posição embaixo ou à direita)
        if (page.Settings.TitleBlock is { } tpl)
        {
            var h = TitleBlockTemplateRenderer.HeightMm(tpl);
            return page.Settings.TitleBlockPosition == TitleBlockPosition.Bottom
                ? new Rect2D(inner.MinX, inner.MinY, inner.Width, h)
                : new Rect2D(inner.MaxX - h, inner.MaxY - inner.Width, h, inner.Width);
        }

        // Fallback ISO7200 ("Padrão OperCAD")
        var tbH = Clamp(inner.Height * 0.17, 26, 40);
        var tbW = Clamp(inner.Width * 0.48, 140, 240);
        return new Rect2D(inner.MaxX - tbW, inner.MinY, tbW, tbH);
    }

    private static double Clamp(double v, double min, double max) => Math.Clamp(v, min, max);

    /// <summary>
    /// Resolve uma variável de contexto do legenda (como o contexto do esquema do padrão CAD):
    /// %author, %title, %date, %filename, %folio, %scale, %indexrev, %company...
    /// </summary>
    private static string ResolveVariable(Page page, string key, IReadOnlyList<Page>? projectPages, string? folhaAutoNumFormula)
    {
        var t = page.TitleBlock;
        return key.ToLowerInvariant() switch
        {
            "author" => t.Author ?? string.Empty,
            "title" => t.Title ?? string.Empty,
            "date" => t.Date.ToString("dd/MM/yyyy"),
            "filename" => page.Name ?? string.Empty,
            "folio" or "sheet" => t.Sheet ?? string.Empty,
            "totalsheets" or "total" => t.TotalSheets ?? string.Empty,
            "scale" => t.Scale ?? string.Empty,
            "indexrev" or "revision" or "rev" => t.Revision ?? string.Empty,
            "company" or "plant" => t.Company ?? string.Empty,
            "drawingnumber" or "drawing" => t.DrawingNumber ?? string.Empty,
            "id" => AutoNum(page, projectPages, folhaAutoNumFormula),
            _ => string.Empty // campos personalizados (%{campo}) ainda não suportados
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

    private static SKPaint StrokePaint(string hex, float width) => new()
    {
        Color = SKColor.Parse(hex),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width
    };

    private static SKPaint FillPaint(string hex) => new()
    {
        Color = SKColor.Parse(hex),
        IsAntialias = true,
        Style = SKPaintStyle.Fill
    };
}
