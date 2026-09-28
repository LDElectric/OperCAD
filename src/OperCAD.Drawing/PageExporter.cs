using OperCAD.Core.Documents;
using OperCAD.Core.TerminalStrips;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>Exporta uma folha para imagem (PNG) ou PDF.</summary>
public static class PageExporter
{
    /// <summary>Renderiza a folha em um bitmap BGRA na resolução dada (px por mm).</summary>
    public static SKBitmap RenderBitmap(Page page, double pxPerMm,
        SceneRenderer.RenderOptions? options = null,
        IReadOnlyList<TerminalStrip>? terminalStrips = null)
    {
        var w = (int)(page.Settings.Width * pxPerMm);
        var h = (int)(page.Settings.Height * pxPerMm);
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
        {
            var view = new ViewTransform(pxPerMm, 0, h);
            // Exportação: sem caixas de seleção e sem grid
            var exportOpts = new SceneRenderer.RenderOptions(
                DrawFrame: options?.DrawFrame ?? true,
                DrawTitleBlock: options?.DrawTitleBlock ?? true,
                ColoredConductors: options?.ColoredConductors ?? true,
                DrawTerminals: options?.DrawTerminals ?? true,
                DrawSelectionFrames: false);
            SceneRenderer.Render(canvas, page, view, new GridSettings { IsVisible = false }, w, h,
                options: exportOpts, terminalStrips: terminalStrips);
        }
        return bmp;
    }

    /// <summary>
    /// Renderiza a folha em um bitmap com dimensões exatas (px), preservando ou não
    /// a proporção — como as opções de exportação de imagem do padrão CAD. O bitmap sempre
    /// tem as dimensões pedidas; keepRatio controla se a folha é esticada (false)
    /// ou encaixada com proporção uniforme (true, com fundo ao redor).
    /// </summary>
    public static SKBitmap RenderBitmapSized(Page page, int widthPx, int heightPx, bool keepRatio,
        SceneRenderer.RenderOptions? options = null,
        IReadOnlyList<TerminalStrip>? terminalStrips = null)
    {
        if (widthPx <= 0) widthPx = 1;
        if (heightPx <= 0) heightPx = 1;

        var pageW = page.Settings.Width;
        var pageH = page.Settings.Height;

        var bmp = new SKBitmap(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
        {
            ViewTransform view;
            if (keepRatio)
            {
                // escala uniforme: a folha inteira cabe na área, centralizada
                var pxPerMm = Math.Min(widthPx / pageW, heightPx / pageH);
                var scaledW = pageW * pxPerMm;
                var scaledH = pageH * pxPerMm;
                var offsetX = (widthPx - scaledW) / 2.0;
                var offsetY = heightPx - (heightPx - scaledH) / 2.0;
                view = new ViewTransform(pxPerMm, offsetX, offsetY);
            }
            else
            {
                // sem proporção: estica a folha para a área exata
                var pxPerMmX = widthPx / pageW;
                var pxPerMmY = heightPx / pageH;
                view = new ViewTransform(pxPerMmX, 0, heightPx);
            }
            var exportOpts = new SceneRenderer.RenderOptions(
                DrawFrame: options?.DrawFrame ?? true,
                DrawTitleBlock: options?.DrawTitleBlock ?? true,
                ColoredConductors: options?.ColoredConductors ?? true,
                DrawTerminals: options?.DrawTerminals ?? true,
                DrawSelectionFrames: false);
            SceneRenderer.Render(canvas, page, view, new GridSettings { IsVisible = false },
                widthPx, heightPx, options: exportOpts, terminalStrips: terminalStrips);
        }
        return bmp;
    }

    /// <summary>Exporta a folha como PNG (sem grid), na resolução dada (px por mm).</summary>
    public static void ExportPng(Page page, string path, double pxPerMm = 4.0)
    {
        using var bmp = RenderBitmap(page, pxPerMm);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>
    /// Exporta uma folha como PDF VETORIAL (sem rasterizar) em tamanho real — conveniência
    /// para chamadas de página única.
    /// </summary>
    public static void ExportPdf(Page page, string path, SceneRenderer.RenderOptions? options = null,
        IReadOnlyList<TerminalStrip>? terminalStrips = null)
        => ExportPdf(new[] { page }, path, options, terminalStrips);

    /// <summary>
    /// Exporta uma ou mais folhas para um único PDF, na ordem dada — cada folha vira uma
    /// página do PDF. Os elementos, molduras e condutores são desenhados como caminhos/texto
    /// vetoriais diretamente no canvas do PDF, mantendo a mesma qualidade em qualquer escala.
    /// </summary>
    public static void ExportPdf(IReadOnlyList<Page> pages, string path,
        SceneRenderer.RenderOptions? options = null,
        IReadOnlyList<TerminalStrip>? terminalStrips = null)
    {
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream);

        foreach (var page in pages)
        {
            var pageSize = new SKSize(
                (float)(page.Settings.Width * 72.0 / 25.4),
                (float)(page.Settings.Height * 72.0 / 25.4));

            using var pdfCanvas = doc.BeginPage(pageSize.Width, pageSize.Height);

            // 1 unidade de mundo (mm) = 72/25,4 pt do PDF → coordenadas em pontos físicos.
            var view = new ViewTransform(72.0 / 25.4, 0, pageSize.Height);
            var exportOpts = new SceneRenderer.RenderOptions(
                DrawFrame: options?.DrawFrame ?? true,
                DrawTitleBlock: options?.DrawTitleBlock ?? true,
                ColoredConductors: options?.ColoredConductors ?? true,
                DrawTerminals: options?.DrawTerminals ?? true,
                DrawSelectionFrames: false,
                // Exportação VETORIAL: espessura exata (sem o mínimo de 0,3 px da tela).
                MinStrokePx: 0);

            SceneRenderer.Render(pdfCanvas, page, view, new GridSettings { IsVisible = false },
                (int)pageSize.Width, (int)pageSize.Height,
                options: exportOpts, terminalStrips: terminalStrips);

            doc.EndPage();
        }

        doc.Close();
    }
}