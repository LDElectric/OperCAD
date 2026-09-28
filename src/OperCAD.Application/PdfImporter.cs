using System.Buffers.Binary;
using System.Reflection;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using OperCAD.Core.Documents;
using OperCAD.Core.Geometry;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace OperCAD.Application;

/// <summary>Importa páginas PDF vetoriais como um projeto editável do OperCAD.</summary>
public sealed class PdfImporter
{
    private const double PointsToMillimeters = 25.4 / 72.0;

    private readonly PdfImportOptions _options;

    /// <summary>
    /// Fator de escala detectado automaticamente para linhas. PDFs industriais usam
    /// valores de largura de linha grandes (ex.: 16-44 pt) em um sistema de coordenadas
    /// onde 1 unidade ≠ 1 pt visual. Este fator é computado na primeira chamada de
    /// <see cref="PathWidthInMillimeters"/> e normaliza as espessuras para o alcance
    /// esperado do OperCAD (0,07-1,43 mm).
    /// </summary>
    private double _lineWidthScale = 1.0;
    private bool _lineWidthScaleComputed;

    public PdfImporter(PdfImportOptions? options = null)
    {
        _options = options ?? new PdfImportOptions();
    }

    /// <summary>
    /// Lê todas as páginas do PDF. Cada path vira uma forma livre e cada linha de texto
    /// vira um TextItem; nenhum conteúdo é convertido em PlacedElement semântico.
    /// </summary>
    public Project Import(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Arquivo PDF não encontrado.", filePath);

        var project = new Project { Name = Path.GetFileNameWithoutExtension(filePath) };

        // Diretório temporário para imagens extraídas
        var imageDir = Path.Combine(Path.GetTempPath(), "OperCAD", "pdf_images", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imageDir);

        using var document = PdfDocument.Open(filePath, new ParsingOptions { ClipPaths = false });
        for (var index = 1; index <= document.NumberOfPages; index++)
        {
            var pdfPage = document.GetPage(index);
            var transform = PdfPageTransform.Create(pdfPage.Width, pdfPage.Height, pdfPage.Rotation);
            var shapes = new List<ShapeItem>();
            var texts = new List<TextItem>();
            var images = new List<ImageItem>();
            ImportPaths(pdfPage, shapes, transform);
            ImportText(pdfPage, texts, transform);
            ImportImages(pdfPage, images, transform, imageDir, index);

            var page = project.AddPage($"Página {index:00}");
            if (_options.Target == PdfImportTarget.NativeSheet)
                ImportNativeSheet(page, shapes, texts, index, transform);
            else
                ImportFullPage(page, shapes, texts, transform.WidthMm, transform.HeightMm);

            // Adicionar imagens diretamente à página (após shapes/textos)
            foreach (var image in images)
                page.Add(image);

            // Calibra a espessura: texto vetorizado recebe caneta fina (não pode parecer
            // "negrito") e as linhas de desenho recebem a espessura padrão do OperCAD.
            ApplyCalibratedStrokeWidths(page);

            // Agrupa por SÍMBOLO (1 grupo = 1 símbolo): permite selecionar, mover, girar e
            // copiar o desenho importado como uma unidade, sem tratar a página toda como grupo.
            AssignSymbolGroups(page);
        }

        if (project.Pages.All(page => page.Objects.Count == 0))
            throw new InvalidDataException("O PDF não contém geometria vetorial ou texto editável.");

        project.ActivePage = project.Pages.FirstOrDefault();
        return project;
    }

    private static void ImportFullPage(
        Page page, List<ShapeItem> shapes, List<TextItem> texts, double widthMm, double heightMm)
    {
        page.Settings.Size = PaperSize.Custom;
        page.Settings.CustomWidth = widthMm;
        page.Settings.CustomHeight = heightMm;
        page.Settings.ElementScale = 1.0;
        page.Settings.ShowFrame = false;
        page.Settings.ShowGrid = false;
        page.Settings.ShowColumns = false;
        page.Settings.ShowRows = false;
        page.Settings.MarginLeft = 0;
        page.Settings.MarginRight = 0;
        page.Settings.MarginTop = 0;
        page.Settings.MarginBottom = 0;
        page.Settings.TitleBlockFile = null;
        page.Settings.FolhaTemplateFile = null;

        foreach (var shape in shapes) page.Add(shape);
        foreach (var text in texts) page.Add(text);
    }

    /// <summary>
    /// Agrupa o conteúdo importado por SÍMBOLO: cada componente compacta de primitivas (mais
    /// os textos que a ela pertencem) recebe um <see cref="ShapeItem.ImportGroupId"/> próprio.
    /// Assim o usuário seleciona, move, gira e copia um símbolo inteiro com um clique, e o
    /// agrupamento é preservado ao salvar/reabrir o projeto.
    /// </summary>
    /// <summary>
    /// Calibra a espessura das formas JÁ transformadas (mm finais):
    /// <list type="bullet">
    /// <item>forma PEQUENA (glifo de texto vetorizado) → caneta FINA: o texto lê como texto e
    /// não como traço grosso (nos PDFs analisados, 82-85% das formas são glifos de 0-3 mm);</item>
    /// <item>forma PREENCHIDA → caneta hairline: o preenchimento define a aparência; contornar
    /// a 0,29 mm "engrossa" os glifos;</item>
    /// <item>demais (linhas de desenho) → espessura PADRÃO do OperCAD (0,29 mm).</item>
    /// </list>
    /// </summary>
    private void ApplyCalibratedStrokeWidths(Page page)
    {
        if (!_options.CalibrateStrokeWidths)
            return;

        var shapes = page.Objects.OfType<ShapeItem>().ToList();

        // Texto vetorizado é decidido por CONTEXTO (densidade de traços curtos), nunca pelo
        // tamanho da forma isolada: depois de dividir o desenho em segmentos, um traço CURTO de
        // desenho também é "pequeno" — classificá-lo como glifo deixava a espessura do desenho
        // inconsistente (traços curtos finos, longos grossos).
        var textLike = ShapeClusterer.FindTextLikeShapes(
            shapes,
            _options.GlyphMaxSizeMm,
            _options.GlyphClusterRadiusMm,
            _options.GlyphMinNeighbours);

        foreach (var shape in shapes)
        {
            var isFilled = !string.IsNullOrEmpty(shape.Filling) &&
                           !string.Equals(shape.Filling, "none", StringComparison.OrdinalIgnoreCase);

            // PenWidth 0 = caminho só PREENCHIDO no PDF (sem traço): preservar — reativar a
            // caneta desenharia as linhas de ligação entre os contornos do mesmo path.
            if (isFilled && shape.PenWidth <= 0)
                continue;

            if (isFilled)
                shape.PenWidth = _options.HairlinePenMm;        // o preenchimento define a aparência
            else if (textLike.Contains(shape.Id))
                shape.PenWidth = _options.ThinPenMm;           // glifo de texto vetorizado
            else
                shape.PenWidth = _options.StandardPenMm;       // desenho: espessura padrão uniforme
        }
    }

    private void AssignSymbolGroups(Page page)
    {
        if (!_options.GroupSymbols)
            return;

        var shapes = page.Objects.OfType<ShapeItem>().ToList();
        if (shapes.Count == 0)
            return;

        var texts = page.Objects.OfType<TextItem>().ToList();
        var cluster = ShapeClusterer.SegmentSymbols(
            shapes, texts,
            gapThreshold: _options.SymbolGapMm,
            wireMinLengthMm: _options.WireMinLengthMm,
            wireMaxThicknessMm: _options.WireMaxThicknessMm,
            maxSymbolWidthMm: _options.MaxSymbolWidthMm,
            maxSymbolHeightMm: _options.MaxSymbolHeightMm,
            textLikeMaxSizeMm: _options.GlyphMaxSizeMm);

        foreach (var group in cluster)
        {
            if (group.Shapes.Count < 2)
                continue;

            var groupId = Guid.NewGuid();
            foreach (var shape in group.Shapes)
                shape.ImportGroupId = groupId;
            foreach (var text in group.Texts)
                text.ImportGroupId = groupId;
        }
    }

    /// <summary>
    /// Estratégia "folha nativa": recorta a área útil do desenho (descarta moldura/guias/
    /// carimbo do PDF), configura a página com a moldura ABNT padrão (template.json) e coloca o
    /// conteúdo recortado na área útil de desenho.
    /// <para>
    /// Por PADRÃO a colocação é em <b>escala REAL (1:1)</b> — as medidas do PDF não mudam, o que
    /// é indispensável para reutilizar a geometria importada — e a folha é escolhida
    /// automaticamente: a MENOR folha padrão (A4…A0) que comporte o recorte, ou uma folha sob
    /// medida quando nenhuma padrão serve. <see cref="PdfImportScaleMode.FitToSheet"/> (ou
    /// <see cref="PdfImportOptions.AutoPaperSize"/> desligado) mantém a folha fixa escolhida pelo
    /// usuário e reescala o conteúdo para preencher a área útil.
    /// </para>
    /// </summary>
    private void ImportNativeSheet(Page page, List<ShapeItem> shapes, List<TextItem> texts, int pageNumber,
        PdfPageTransform transform)
    {
        // 1) Resolve o retângulo de recorte (explícito por página ou auto-detectado).
        var crop = ResolveCrop(shapes, pageNumber, transform.WidthMm, transform.HeightMm);

        // 2) Filtra o conteúdo: mantém apenas o que pertence à área útil do recorte.
        //    Formas cujo centro está fora do recorte (bordas da moldura) ou que atravessam
        //    quase todo o recorte (o próprio quadro/frame) são descartadas.
        var keptShapes = new List<ShapeItem>();
        var keptTexts = new List<TextItem>();
        foreach (var shape in shapes)
            if (crop is not { } c || IsInsideUsefulArea(shape, c))
                keptShapes.Add(shape);
        foreach (var text in texts)
            if (crop is not { } c || c.Contains(new Point2D(text.X, text.Y)))
                keptTexts.Add(text);

        // 3) Região de origem: o retângulo que o usuário selecionou (recorte) — ou, se não
        //    houver recorte, os limites do próprio conteúdo mantido. É essa região que define o
        //    papel necessário (escala real) ou é mapeada sobre a área útil (modo "ajustar").
        Rect2D region;
        if (crop is { } selected)
        {
            region = selected;
        }
        else
        {
            if (ContentBounds(keptShapes, keptTexts) is not { } cb)
                return;
            region = cb;
        }

        // 4) Configura a moldura ABNT padrão (template.json) e decide o PAPEL: por padrão a menor
        //    folha que comporte o recorte em escala real — escolher A4 e "esticar" o desenho
        //    para preenchê-la mudaria as dimensões do desenho (perda de fidelidade).
        var folha = FolhaTemplateCatalog.Builtin();
        var folhaSettings = page.Settings;
        ApplyFolhaTemplate(folhaSettings, folha);
        var plan = PlanSheet(region, folha, _options);
        SetSheetSize(folhaSettings, plan);

        // 5) Coloca o conteúdo na área útil: 1:1 (só translação) na escala real, ou mapeando o
        //    recorte sobre TODA a área útil (reescala uniforme, sem distorcer) no modo "ajustar".
        var drawingArea = FolhaFrameGeometry.DrawingArea(folha, folhaSettings);
        if (plan.Scale == 1.0)
            PlaceRegionOneToOne(keptShapes, keptTexts, region, drawingArea, folhaSettings);
        else
            FillDrawingArea(keptShapes, keptTexts, region, drawingArea);

        // Limpa/encaixa a geometria: remove ruído de precisão e encaixa à malha do OperCAD
        // (2,5 mm) apenas quando o resíduo é pequeno — para virar um desenho nativo mais
        // fácil de editar e alinhar sem deformar o conteúdo.
        CleanSnapGeometry(keptShapes, keptTexts);

        foreach (var shape in keptShapes) page.Add(shape);
        foreach (var text in keptTexts) page.Add(text);
    }

    /// <summary>Configura a página com a moldura ABNT do catálogo (template, guias de colunas/linhas,
    /// carimbo e recuos). NÃO define o tamanho do papel — isso é decidido por <see cref="PlanSheet"/>.</summary>
    private void ApplyFolhaTemplate(PageSettings settings, FolhaTemplate folha)
    {
        settings.FolhaTemplate = folha;
        settings.FolhaTemplateFile = folha.FileName;
        settings.FolhaFrameInset = folha.FrameInset > 0 ? folha.FrameInset : settings.FolhaFrameInset;
        settings.ColumnsCount = folha.ColumnsCount;
        settings.RowsCount = folha.RowsCount;
        settings.TitleBlockFile = folha.FileName;
        settings.ElementScale = 1.0;
        settings.ShowFrame = _options.ShowFrame;
        settings.ShowGrid = _options.ShowGrid;
        settings.ShowColumns = _options.ShowFrame;
        settings.ShowRows = _options.ShowFrame;
        settings.MarginLeft = folha.FrameInset;
        settings.MarginRight = folha.FrameInset;
        settings.MarginTop = folha.FrameInset;
        settings.MarginBottom = folha.FrameInset;
    }

    private static void SetSheetSize(PageSettings settings, PdfSheetPlan plan)
    {
        settings.Size = plan.Size;
        if (plan.Size == PaperSize.Custom)
        {
            settings.CustomWidth = plan.WidthMm;
            settings.CustomHeight = plan.HeightMm;
        }
    }

    /// <summary>Menores folhas padrão consideradas na escolha automática, em ordem crescente.</summary>
    private static readonly PaperSize[] SheetCandidates =
        [PaperSize.A4, PaperSize.A3, PaperSize.A2, PaperSize.A1, PaperSize.A0];

    /// <summary>
    /// Planeja a folha nativa para uma região de recorte: escolhe o papel, a escala a aplicar e
    /// informa se o conteúdo cabe. É a MESMA decisão usada na importação e exibida no wizard —
    /// assim o que o usuário vê no diálogo é o que ele recebe na folha.
    /// <list type="bullet">
    /// <item><see cref="PdfImportScaleMode.RealScale"/> + <see cref="PdfImportOptions.AutoPaperSize"/>:
    /// MENOR folha padrão que CONTENHA o recorte (escala 1:1) — as folhas dos PDFs analisados são
    /// A4, então o resultado também é A4. Observação: o conteúdo pode ocupar a faixa da moldura/
    /// carimbo do OperCAD (<see cref="PdfSheetPlan.FitsDrawingArea"/> = false), mas não é cortado;</item>
    /// <item><see cref="PdfImportScaleMode.RealScale"/> com folha fixa: escala 1:1 naquela folha
    /// (<see cref="PdfSheetPlan.FitsSheet"/> = false ⇒ o excedente é cortado);</item>
    /// <item><see cref="PdfImportScaleMode.FitToSheet"/>: folha fixa, conteúdo reescalado para
    /// preencher a área útil de desenho.</item>
    /// </list>
    /// </summary>
    public static PdfSheetPlan PlanSheet(Rect2D region, FolhaTemplate folha, PdfImportOptions options)
    {
        var inset = folha.FrameInset > 0 ? folha.FrameInset : 5.0;
        var margin = Math.Max(0, options.RealScaleMarginMm);

        if (options.ScaleMode == PdfImportScaleMode.FitToSheet)
        {
            var (fitW, fitH) = options.Paper.Mm();
            var fitArea = DrawingAreaFor(folha, fitW, fitH, inset);
            // O recorte é MAPEADO sobre a área útil: por construção ele passa a caber nela e,
            // portanto, também na folha.
            return new PdfSheetPlan(options.Paper, fitW, fitH, ScaleToFit(region, fitArea), true, true);
        }

        if (options.AutoPaperSize)
        {
            foreach (var candidate in SheetCandidates)
            {
                var (w, h) = candidate.Mm();
                if (FitsSheet(w, h, region, margin))
                    return new PdfSheetPlan(candidate, w, h, 1.0,
                        FitsDrawingArea(folha, w, h, inset, region, margin), true);
                // Mesma folha girada (desenho "em pé"): dimensões exatas ⇒ folha sob medida.
                if (FitsSheet(h, w, region, margin))
                    return new PdfSheetPlan(PaperSize.Custom, h, w, 1.0,
                        FitsDrawingArea(folha, h, w, inset, region, margin), true);
            }

            var (cw, ch) = CustomSheetFor(region, folha, inset, margin);
            return new PdfSheetPlan(PaperSize.Custom, cw, ch, 1.0,
                FitsDrawingArea(folha, cw, ch, inset, region, margin), true);
        }

        var (paperW, paperH) = options.Paper.Mm();
        return new PdfSheetPlan(options.Paper, paperW, paperH, 1.0,
            FitsDrawingArea(folha, paperW, paperH, inset, region, margin),
            FitsSheet(paperW, paperH, region, margin));
    }

    /// <summary>Área útil de desenho de uma folha hipotética (usada para escolher o papel).</summary>
    private static Rect2D DrawingAreaFor(FolhaTemplate folha, double widthMm, double heightMm, double inset)
    {
        var probe = new PageSettings
        {
            Size = PaperSize.Custom,
            CustomWidth = widthMm,
            CustomHeight = heightMm,
            FolhaFrameInset = inset
        };
        return FolhaFrameGeometry.DrawingArea(folha, probe);
    }

    /// <summary>O conteúdo fica DENTRO da área útil de desenho (sem invadir moldura/carimbo)?</summary>
    private static bool FitsDrawingArea(FolhaTemplate folha, double widthMm, double heightMm, double inset,
        Rect2D region, double margin)
    {
        var area = DrawingAreaFor(folha, widthMm, heightMm, inset);
        return region.Width + margin * 2 <= area.Width + 1e-6 &&
               region.Height + margin * 2 <= area.Height + 1e-6;
    }

    /// <summary>O conteúdo cabe na FOLHA (nada é cortado)?</summary>
    private static bool FitsSheet(double widthMm, double heightMm, Rect2D region, double margin)
        => region.Width + margin * 2 <= widthMm + 1e-6 &&
           region.Height + margin * 2 <= heightMm + 1e-6;

    private static double ScaleToFit(Rect2D region, Rect2D area)
    {
        if (region.Width <= 0 || region.Height <= 0 || area.Width <= 0 || area.Height <= 0)
            return 1.0;
        return Math.Min(area.Width / region.Width, area.Height / region.Height);
    }

    /// <summary>
    /// Folha sob medida (recorte maior que A0, ou em pé): parte da orientação do recorte e cresce
    /// a folha com a mesma proporção até a área útil comportar o conteúdo em escala 1:1.
    /// </summary>
    private static (double WidthMm, double HeightMm) CustomSheetFor(
        Rect2D region, FolhaTemplate folha, double inset, double margin)
    {
        var (longSide, shortSide) = PaperSize.A0.Mm();
        var landscape = region.Width >= region.Height;
        var width = landscape ? longSide : shortSide;
        var height = landscape ? shortSide : longSide;

        for (var i = 0; i < 8; i++)
        {
            var area = DrawingAreaFor(folha, width, height, inset);
            if (area.Width <= 0 || area.Height <= 0)
                break;
            var factor = Math.Max(
                (region.Width + margin * 2) / area.Width,
                (region.Height + margin * 2) / area.Height);
            if (factor <= 1.0001)
                break;
            width *= factor;
            height *= factor;
        }

        // Folga fixa para absorver o arredondamento e o recuo da moldura.
        return (width + 0.5, height + 0.5);
    }

    /// <summary>
    /// Posiciona a região do PDF na folha SEM reescalar (1:1): apenas translação. O conteúdo é
    /// centralizado na ÁREA ÚTIL de desenho quando couber; quando não couber, continua 1:1 e a
    /// posição é corrigida para que a extensão real do conteúdo fique DENTRO da folha (o excedente
    /// apenas ocupa a faixa da moldura/carimbo — nunca é cortado pelo papel).
    /// </summary>
    private static void PlaceRegionOneToOne(List<ShapeItem> shapes, List<TextItem> texts,
        Rect2D region, Rect2D area, PageSettings settings)
    {
        var slackX = area.Width - region.Width;
        var slackY = area.Height - region.Height;
        var originX = area.MinX + (slackX > 0 ? slackX / 2 : 0) - region.MinX;
        var originY = area.MinY + (slackY > 0 ? slackY / 2 : 0) - region.MinY;

        ApplyAffine(shapes, texts, originX, originY, 1.0);

        // Segundo ajuste: alguma forma pode passar das bordas do RECORTE (o filtro mantém a que tem
        // o centro dentro dele). Corrige pela extensão real do CONTEÚDO, para que nada seja cortado
        // pelo papel da folha.
        if (ContentBounds(shapes, texts) is not { } bounds)
            return;

        var fixX = bounds.MinX < 0 ? -bounds.MinX
            : bounds.MaxX > settings.Width ? settings.Width - bounds.MaxX : 0;
        var fixY = bounds.MinY < 0 ? -bounds.MinY
            : bounds.MaxY > settings.Height ? settings.Height - bounds.MaxY : 0;

        if (fixX != 0 || fixY != 0)
            ApplyAffine(shapes, texts, fixX, fixY, 1.0);
    }

    /// <summary>
    /// "Preenche" a área útil: aplica uma transformação afim (escala uniforme + translação)
    /// que leva <paramref name="region"/> — o que o usuário selecionou no PDF — a ocupar o
    /// máximo possível de <paramref name="area"/> (a área útil da folha ABNT), preservando a
    /// proporção e centralizando o excedente. O tamanho de fonte dos textos acompanha a escala.
    /// </summary>
    private static void FillDrawingArea(List<ShapeItem> shapes, List<TextItem> texts,
        Rect2D region, Rect2D area)
    {
        if (region.Width <= 0 || region.Height <= 0 || area.Width <= 0 || area.Height <= 0)
            return;

        var scale = Math.Min(area.Width / region.Width, area.Height / region.Height);
        var targetW = region.Width * scale;
        var targetH = region.Height * scale;
        var originX = area.MinX + (area.Width - targetW) / 2 - region.MinX * scale;
        var originY = area.MinY + (area.Height - targetH) / 2 - region.MinY * scale;

        ApplyAffine(shapes, texts, originX, originY, scale);
    }

    /// <summary>
    /// Aplica X' = originX + X*scale e Y' = originY + Y*scale a formas e textos, ajustando o
    /// tamanho de fonte dos textos pela mesma escala.
    /// </summary>
    private static void ApplyAffine(List<ShapeItem> shapes, List<TextItem> texts,
        double originX, double originY, double scale)
    {
        foreach (var shape in shapes)
        {
            for (var i = 0; i < shape.Points.Count; i++)
            {
                var p = shape.Points[i];
                shape.Points[i] = new Point2D(originX + p.X * scale, originY + p.Y * scale);
            }
            if (shape.ControlPoints is { } cp)
            {
                for (var i = 0; i < cp.Count; i++)
                {
                    var (c1, c2) = cp[i];
                    cp[i] = (
                        new Point2D(originX + c1.X * scale, originY + c1.Y * scale),
                        new Point2D(originX + c2.X * scale, originY + c2.Y * scale));
                }
            }
        }

        foreach (var text in texts)
        {
            text.X = originX + text.X * scale;
            text.Y = originY + text.Y * scale;
            text.FontSizePt = Math.Max(1.0, text.FontSizePt * scale);
        }
    }

    private Rect2D? ResolveCrop(List<ShapeItem> shapes, int pageNumber,
        double paperWidthMm, double paperHeightMm)
    {
        if (_options.PageCrops is { } pageCrops)
        {
            var match = pageCrops.FirstOrDefault(c => c.PageNumber == pageNumber);
            if (match is not null)
                return match.RectMm;
        }
        if (_options.Crop is { } explicitCrop && explicitCrop.PageNumber == pageNumber)
            return explicitCrop.RectMm;
        return PdfAreaDetector.Detect(shapes, paperWidthMm, paperHeightMm);
    }

    private static Rect2D? ContentBounds(List<ShapeItem> shapes, List<TextItem> texts)
    {
        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        var any = false;
        foreach (var shape in shapes)
        {
            var b = shape.Bounds;
            if (b.Width <= 0 && b.Height <= 0) continue;
            minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY);
            maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY);
            any = true;
        }
        foreach (var text in texts)
        {
            minX = Math.Min(minX, text.X); minY = Math.Min(minY, text.Y);
            maxX = Math.Max(maxX, text.X); maxY = Math.Max(maxY, text.Y);
            any = true;
        }
        return any ? new Rect2D(minX, minY, maxX - minX, maxY - minY) : null;
    }

    private static bool RectangleIntersects(Rect2D a, Rect2D b)
        => a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;

    private static bool IsInsideUsefulArea(ShapeItem shape, Rect2D crop)
    {
        var b = shape.Bounds;
        if (b.Width <= 0 && b.Height <= 0)
            return false;
        var centerX = (b.MinX + b.MaxX) / 2.0;
        var centerY = (b.MinY + b.MaxY) / 2.0;
        var centerInside = crop.MinX <= centerX && centerX <= crop.MaxX &&
                           crop.MinY <= centerY && centerY <= crop.MaxY;
        if (!centerInside)
            return false;
        // Forma que atravessa quase todo o recorte (quadro/frame) é descartada.
        var frameLike = b.Width >= crop.Width * 0.95 || b.Height >= crop.Height * 0.95;
        return !frameLike;
    }

    /// <summary>
    /// Limpeza de geometria pós-importação: arredonda o ruído de precisão e, quando
    /// <see cref="PdfImportOptions.SnapToGrid"/> está ligado, encaixa à malha do OperCAD (2,5 mm)
    /// apenas se o resíduo for pequeno (≤0,25 mm). Assim o desenho importado fica "on-grid" e
    /// fácil de editar/alinhar sem ser deformado. Desligue para fidelidade EXATA ao PDF
    /// (no modo escala real, o encaixe desloca o conteúdo em até 0,25 mm).
    /// </summary>
    private void CleanSnapGeometry(List<ShapeItem> shapes, List<TextItem> texts)
    {
        const double grid = 2.5;
        const double tol = 0.25;
        var snapToGrid = _options.SnapToGrid;

        double Snap(double v)
        {
            var snapped = Math.Round(v / grid) * grid;
            return snapToGrid && Math.Abs(v - snapped) <= tol ? snapped : Math.Round(v, 3);
        }

        foreach (var shape in shapes)
        {
            for (var i = 0; i < shape.Points.Count; i++)
            {
                var p = shape.Points[i];
                shape.Points[i] = new Point2D(Snap(p.X), Snap(p.Y));
            }
            if (shape.ControlPoints is { } cp)
            {
                for (var i = 0; i < cp.Count; i++)
                {
                    var (c1, c2) = cp[i];
                    cp[i] = (new Point2D(Snap(c1.X), Snap(c1.Y)), new Point2D(Snap(c2.X), Snap(c2.Y)));
                }
            }
        }

        foreach (var text in texts)
        {
            text.X = Snap(text.X);
            text.Y = Snap(text.Y);
        }
    }

    private void ImportPaths(UglyToad.PdfPig.Content.Page pdfPage, List<ShapeItem> shapes, PdfPageTransform transform)
    {
        ComputeLineWidthScale(pdfPage);

        var pathIndex = 0;
        foreach (var path in pdfPage.ExperimentalAccess.Paths)
        {
            var currentObjectIndex = pathIndex++;
            if (path.IsClipping || (!path.IsStroked && !path.IsFilled))
                continue;

            // Aplica cor/espessura/tracejado/preenchimento do path às formas criadas.
            void StyleShape(ShapeItem shape)
            {
                shape.Color = PdfColorToHex(path.IsStroked ? path.StrokeColor : path.FillColor);
                shape.PenWidth = PenWidthFor(path.LineWidth);
                shape.DashStyle = ParseDashStyle(path.LineDashPattern);
                shape.DashPattern = ParseDashPattern(path.LineDashPattern);
                shape.DashPhase = ParseDashPhase(path.LineDashPattern);
                shape.LineCap = ParseLineCap(path.LineCapStyle);
                shape.LineJoin = ParseLineJoin(path.LineJoinStyle);
                shape.Filling = path.IsFilled ? PdfColorToHex(path.FillColor) : "none";
            }

            // Caminho PREENCHIDO: agrega TODOS os subpaths do path num ÚNICO objeto, com os
            // contornos independentes.
            //
            // Isto é ESSENCIAL: um texto vetorizado é UM path com dezenas de subpaths (cada letra
            // E os VAZIOS internos dela). Emitindo cada contorno separado, os vazios eram
            // preenchidos e as letras viravam borrões sólidos; e, ligados por segmentos, o
            // preenchimento "vazava" entre letras vizinhas. Com contornos independentes o
            // preenchimento (par-ímpar) preserva os furos e mantém as letras separadas.
            if (path.IsFilled)
            {
                var filledPoints = new List<Point2D>();
                var contourStarts = new List<int>();
                foreach (var subpath in path)
                {
                    foreach (var figure in BuildFigures(subpath, transform))
                    {
                        var contour = figure.Flatten();
                        if (contour.Count < 3)
                            continue;
                        contourStarts.Add(filledPoints.Count);   // cada contorno começa aqui
                        filledPoints.AddRange(contour);
                        // O preenchimento do PDF fecha o contorno implicitamente.
                        if (!Same(contour[0], contour[^1]))
                            filledPoints.Add(contour[0]);
                    }
                }

                if (filledPoints.Count >= 3)
                {
                    var filledShape = new ShapeItem
                    {
                        Kind = ShapeKind.Polygon,
                        Points = filledPoints,
                        Closed = true,
                        ContourStarts = contourStarts.Count > 1 ? contourStarts : null
                    };
                    StyleShape(filledShape);
                    // Caminho só PREENCHIDO (LineWidth = 0 no PDF) não é traçado.
                    if (!path.IsStroked)
                        filledShape.PenWidth = 0;
                    filledShape.PdfObjectIndex = currentObjectIndex;

                    // SEPARA os contornos por proximidade: um path do PDF costuma trazer TODOS os
                    // rótulos de uma linha (medido: um objeto de 109 mm com as etiquetas de vários
                    // contatos). Sem separar, selecionar o rótulo de um contato traz o de outro a
                    // 98 mm de distância e não há como editar/excluir um sem o outro. Contornos
                    // aninhados (furo de letra) permanecem juntos, preservando o preenchimento.
                    foreach (var part in ShapeContourSplitter.SplitByProximity(
                                 filledShape, _options.LabelClusterGapMm))
                        shapes.Add(part);
                }
                continue;
            }

            foreach (var subpath in path)
            {
                // Tentar retângulo nativo primeiro
                if (subpath.IsDrawnAsRectangle && subpath.GetDrawnRectangle() is PdfRectangle rectangle)
                {
                    shapes.Add(new ShapeItem
                    {
                        Kind = ShapeKind.Rectangle,
                        Points = RectanglePoints(rectangle, transform),
                        Closed = true,
                        Color = PdfColorToHex(path.IsStroked ? path.StrokeColor : path.FillColor),
                        PenWidth = PenWidthFor(path.LineWidth),
                        DashStyle = ParseDashStyle(path.LineDashPattern),
                        DashPattern = ParseDashPattern(path.LineDashPattern),
                        DashPhase = ParseDashPhase(path.LineDashPattern),
                        LineCap = ParseLineCap(path.LineCapStyle),
                        LineJoin = ParseLineJoin(path.LineJoinStyle),
                        Filling = path.IsFilled ? PdfColorToHex(path.FillColor) : "none",
                        PdfObjectIndex = currentObjectIndex
                    });
                    continue;
                }

                // Reconstrói as FIGURAS do PDF. Um "Move" é "levantar a caneta": começa uma
                // figura nova e NUNCA deve ser ligado ao comando anterior — era isso que criava
                // quinas/segmentos fantasmas e juntava traços que no arquivo são separados.
                var figures = BuildFigures(subpath, transform);
                if (figures.Count == 0)
                    continue;

                // Elipse nativa (4 béziers fechados): um único objeto, preservando a área.
                var allPoints = figures.SelectMany(f => f.Vertices).ToList();
                var ellipseBounds = TryGetEllipseBounds(subpath, allPoints);

                if (ellipseBounds is { } ellipse)
                {
                    var ellipseShape = new ShapeItem
                    {
                        Kind = ShapeKind.Ellipse,
                        Points = new List<Point2D>
                        {
                            new(ellipse.MinX, ellipse.MinY),
                            new(ellipse.MaxX, ellipse.MaxY)
                        },
                        Closed = true
                    };
                    StyleShape(ellipseShape);
                    ellipseShape.PdfObjectIndex = currentObjectIndex;
                    shapes.Add(ellipseShape);
                    continue;
                }

                // Caminho apenas TRAÇADO: emite UM OBJETO POR SEGMENTO. É a granularidade do
                // editor de PDF: dá para selecionar, copiar, colorir e reutilizar um traço de
                // cada vez (ex.: as duas pernas do "L" viram dois objetos independentes).
                if (_options.SplitStrokedPathsIntoSegments)
                {
                    foreach (var figure in figures)
                    foreach (var segment in figure.Segments)
                    {
                        ShapeItem segmentShape;
                        if (segment.C1 is { } c1 && segment.C2 is { } c2)
                        {
                            segmentShape = new ShapeItem
                            {
                                Kind = ShapeKind.Curve,
                                Points = new List<Point2D> { segment.From, segment.To },
                                ControlPoints = new List<(Point2D C1, Point2D C2)> { (c1, c2) }
                            };
                        }
                        else
                        {
                            segmentShape = new ShapeItem
                            {
                                Kind = ShapeKind.Line,
                                Points = new List<Point2D> { segment.From, segment.To }
                            };
                        }
                        StyleShape(segmentShape);
                        segmentShape.PdfObjectIndex = currentObjectIndex;
                        shapes.Add(segmentShape);
                    }
                    continue;
                }

                // Alternativa (desligando a divisão): mantém a figura inteira como um objeto.
                foreach (var figure in figures)
                {
                    if (figure.Vertices.Count < 2)
                        continue;
                    var whole = new ShapeItem
                    {
                        Kind = figure.HasBezier
                            ? ShapeKind.Curve
                            : figure.Vertices.Count == 2 ? ShapeKind.Line : ShapeKind.Polygon,
                        Points = figure.Vertices.ToList(),
                        ControlPoints = figure.HasBezier ? figure.BuildControlPairs() : null,
                        Closed = figure.Closed
                    };
                    StyleShape(whole);
                    whole.PdfObjectIndex = currentObjectIndex;
                    shapes.Add(whole);
                }
            }
        }
    }

    /// <summary>Um segmento do desenho: reto (C1/C2 nulos) ou curva Bézier.</summary>
    private readonly record struct PdfSegment(Point2D From, Point2D To, Point2D? C1, Point2D? C2);

    /// <summary>
    /// Uma FIGURA do PDF: a sequência de segmentos entre dois "Move" (caneta levantada).
    /// </summary>
    private sealed class PdfFigure
    {
        public List<Point2D> Vertices { get; } = new();
        public List<PdfSegment> Segments { get; } = new();
        public bool Closed { get; set; }
        public bool HasBezier => Segments.Any(s => s.C1 is not null);

        /// <summary>
        /// Pontos de controle alinhados por segmento (para figuras mistas). Segmentos retos
        /// recebem controles degenerados, preservando o formato geral da figura preenchida.
        /// </summary>
        public List<(Point2D C1, Point2D C2)> BuildControlPairs()
        {
            var pairs = new List<(Point2D C1, Point2D C2)>(Segments.Count);
            foreach (var s in Segments)
            {
                if (s.C1 is { } c1 && s.C2 is { } c2)
                    pairs.Add((c1, c2));
                else
                    pairs.Add((s.From, s.To));
            }
            return pairs;
        }

        /// <summary>
        /// Achata o contorno em polilinha (Bézier subdividido). Usado para preenchimento
        /// multi-contorno: um caminho preenchido vira UM objeto com todas as suas figuras.
        /// </summary>
        public List<Point2D> Flatten(int subdivisions = 10)
        {
            var pts = new List<Point2D>();
            foreach (var s in Segments)
            {
                if (pts.Count == 0)
                    pts.Add(s.From);

                if (s.C1 is { } c1 && s.C2 is { } c2)
                {
                    for (var i = 1; i <= subdivisions; i++)
                    {
                        var t = (double)i / subdivisions;
                        var mt = 1 - t;
                        var x = mt * mt * mt * s.From.X + 3 * mt * mt * t * c1.X +
                                3 * mt * t * t * c2.X + t * t * t * s.To.X;
                        var y = mt * mt * mt * s.From.Y + 3 * mt * mt * t * c1.Y +
                                3 * mt * t * t * c2.Y + t * t * t * s.To.Y;
                        pts.Add(new Point2D(x, y));
                    }
                }
                else
                {
                    pts.Add(s.To);
                }
            }
            return pts;
        }
    }

    /// <summary>
    /// Reconstrói as figuras respeitando "Move" (caneta levantada). Sem isso, comandos
    /// separados no arquivo eram ligados por um segmento que não existe no PDF.
    /// </summary>
    private static List<PdfFigure> BuildFigures(PdfSubpath subpath, PdfPageTransform transform)
    {
        var figures = new List<PdfFigure>();
        PdfFigure? current = null;
        Point2D? pen = null;

        foreach (var command in subpath.Commands)
        {
            switch (command.GetType().Name)
            {
                case "Move":
                    // Caneta levantada: começa figura nova (a anterior fica como está).
                    current = new PdfFigure();
                    figures.Add(current);
                    pen = transform.Convert(GetPoint(command, "Location"));
                    current.Vertices.Add(pen.Value);
                    break;

                case "Line":
                {
                    var to = transform.Convert(GetPoint(command, "To"));
                    current ??= NewFigureAt(figures, ref pen, to);
                    var from = pen ?? to;
                    if (!Same(from, to))
                    {
                        current.Vertices.Add(to);
                        current.Segments.Add(new PdfSegment(from, to, null, null));
                    }
                    pen = to;
                    break;
                }

                case "BezierCurve":
                {
                    var c1 = transform.Convert(GetPoint(command, "FirstControlPoint"));
                    var c2 = transform.Convert(GetPoint(command, "SecondControlPoint"));
                    var end = transform.Convert(GetPoint(command, "EndPoint"));
                    current ??= NewFigureAt(figures, ref pen, end);
                    var start = pen ?? end;
                    current.Vertices.Add(end);
                    current.Segments.Add(new PdfSegment(start, end, c1, c2));
                    pen = end;
                    break;
                }

                case "Close":
                    if (current is not null)
                    {
                        current.Closed = true;
                        if (current.Vertices.Count > 1 && !Same(current.Vertices[0], current.Vertices[^1]))
                        {
                            var from = current.Vertices[^1];
                            var to = current.Vertices[0];
                            current.Segments.Add(new PdfSegment(from, to, null, null));
                            current.Vertices.Add(to);
                            pen = to;
                        }
                    }
                    break;
            }
        }

        return figures;

        static PdfFigure NewFigureAt(List<PdfFigure> figures, ref Point2D? pen, Point2D first)
        {
            var figure = new PdfFigure();
            figure.Vertices.Add(first);
            figures.Add(figure);
            pen = first;
            return figure;
        }
    }

    private static bool Same(Point2D a, Point2D b) => a.DistanceTo(b) <= 1e-4;

    private void ImportText(UglyToad.PdfPig.Content.Page pdfPage, List<TextItem> texts, PdfPageTransform transform)
    {
        var glyphs = DeduplicateGlyphs(pdfPage.Letters);
        foreach (var run in BuildTextRuns(glyphs))
        {
            var first = run[0];
            var last = run[^1];
            var (bold, italic) = DetectFontStyle(first.FontName);
            texts.Add(new TextItem
            {
                Text = string.Concat(run.Select(letter => letter.Value)),
                X = transform.Convert(first.StartBaseLine).X,
                Y = transform.Convert(first.StartBaseLine).Y,
                Rotation = Math.Atan2(
                    last.EndBaseLine.Y - first.StartBaseLine.Y,
                    last.EndBaseLine.X - first.StartBaseLine.X) * 180.0 / Math.PI + transform.RotationDegrees,
                FontFamily = NormalizeFontFamily(first.FontName, _options.FallbackFontFamily),
                FontSizePt = Math.Max(1, first.PointSize),
                Bold = bold,
                Italic = italic,
                Color = PdfColorToHex(first.Color)
            });
        }
    }

    /// <summary>
    /// Detecta bold/italic a partir do nome da fonte do PDF.
    /// Fontes PDF frequentemente embutem o estilo no nome (ex.: "Arial-BoldMT", "TimesNewRoman-Italic").
    /// </summary>
    private static (bool Bold, bool Italic) DetectFontStyle(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return (false, false);

        var name = fontName.ToLowerInvariant();
        var bold = name.Contains("bold") || name.Contains("black") || name.Contains("heavy") ||
                   name.Contains("semibold") || name.Contains("demi");
        var italic = name.Contains("italic") || name.Contains("oblique") || name.Contains("slant");
        return (bold, italic);
    }

    /// <summary>
    /// Extrai imagens rasterizadas do PDF e salva como arquivos temporários.
    /// Cada imagem vira um ImageItem posicionado usando o retângulo de bounds do PDF.
    /// </summary>
    private static void ImportImages(UglyToad.PdfPig.Content.Page pdfPage, List<ImageItem> images,
        PdfPageTransform transform, string imageDir, int pageNumber)
    {
        IEnumerable<UglyToad.PdfPig.Content.IPdfImage> pdfImages;
        try { pdfImages = pdfPage.GetImages(); }
        catch { return; }

        var imageList = pdfImages.ToList();
        if (imageList.Count == 0)
            return;

        var counter = 0;
        foreach (var img in imageList)
        {
            try
            {
                byte[]? imageBytes = null;
                var ext = "png";

                // Tentar PNG primeiro (funciona para a maioria das imagens não-JPEG)
                if (img.TryGetPng(out var pngBytes))
                {
                    imageBytes = pngBytes;
                    ext = "png";
                }
                // Para imagens JPEG, os RawBytes já são JPEG válido
                else if (img.RawBytes is { Count: > 0 } raw && IsJpegHeader(raw))
                {
                    imageBytes = raw.ToArray();
                    ext = "jpg";
                }
                // Último recurso: tentar bytes decodificados
                else if (img.TryGetBytes(out var decoded))
                {
                    var pixelData = UglyToad.PdfPig.Images.ColorSpaceDetailsByteConverter.Convert(
                        img.ColorSpaceDetails, decoded,
                        img.WidthInSamples, img.HeightInSamples, img.BitsPerComponent);
                    if (pixelData is { Length: > 0 })
                    {
                        imageBytes = BuildPngFromRgb(pixelData, img.WidthInSamples, img.HeightInSamples);
                        ext = "png";
                    }
                }

                if (imageBytes is null || imageBytes.Length == 0)
                    continue;

                var fileName = $"page{pageNumber:00}_img{++counter}.{ext}";
                var filePath = Path.Combine(imageDir, fileName);
                File.WriteAllBytes(filePath, imageBytes);

                // Converter bounds do PDF (points) para mm
                var bounds = img.Bounds;
                var p1 = transform.Convert(new UglyToad.PdfPig.Core.PdfPoint(bounds.Left, bounds.Bottom));
                var p2 = transform.Convert(new UglyToad.PdfPig.Core.PdfPoint(bounds.Right, bounds.Top));

                var x = Math.Min(p1.X, p2.X);
                var y = Math.Min(p1.Y, p2.Y);
                var w = Math.Abs(p2.X - p1.X);
                var h = Math.Abs(p2.Y - p1.Y);

                if (w > 0.1 && h > 0.1)
                {
                    images.Add(new ImageItem
                    {
                        FilePath = filePath,
                        X = x,
                        Y = y,
                        Width = w,
                        Height = h
                    });
                }
            }
            catch
            {
                // Imagem não extraível — pular silenciosamente
            }
        }
    }

    private static bool IsJpegHeader(IReadOnlyList<byte> data)
    {
        if (data.Count < 4) return false;
        return data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
    }

    /// <summary>
    /// Constrói um PNG simples a partir de dados RGB brutos (3 bytes por pixel).
    /// Usa deflate via System.IO.Compression.
    /// </summary>
    private static byte[] BuildPngFromRgb(byte[] rgbData, int width, int height)
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, true))
        {
            // PNG signature
            writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // IHDR chunk
            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 2; // color type: RGB
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace
            WriteChunk(writer, "IHDR", ihdr);

            // IDAT chunk: raw data with filter byte 0 per row
            using var rawStream = new MemoryStream();
            var rowBytes = width * 3;
            for (var y = 0; y < height; y++)
            {
                rawStream.WriteByte(0); // filter: none
                var offset = y * rowBytes;
                var count = Math.Min(rowBytes, rgbData.Length - offset);
                if (count > 0)
                    rawStream.Write(rgbData, offset, count);
            }

            using var deflate = new System.IO.Compression.DeflateStream(rawStream, System.IO.Compression.CompressionLevel.Optimal, true);
            using var compressed = new MemoryStream();
            deflate.CopyTo(compressed);
            deflate.Flush();

            WriteChunk(writer, "IDAT", compressed.ToArray());

            // IEND chunk
            WriteChunk(writer, "IEND", Array.Empty<byte>());
        }

        return output.ToArray();

        static void WriteChunk(BinaryWriter writer, string type, byte[] data)
        {
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            writer.Write(BinaryPrimitives.ReverseEndianness(data.Length));
            writer.Write(typeBytes);
            writer.Write(data);
            var crc = Crc32(typeBytes, data);
            writer.Write(BinaryPrimitives.ReverseEndianness(crc));
        }

        static uint Crc32(byte[] type, byte[] data)
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }

            uint crc = 0xFFFFFFFF;
            foreach (var b in type)
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in data)
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }

    private static List<Point2D> RectanglePoints(PdfRectangle rectangle, PdfPageTransform transform)
        => new()
        {
            transform.Convert(new PdfPoint(rectangle.Left, rectangle.Bottom)),
            transform.Convert(new PdfPoint(rectangle.Right, rectangle.Top))
        };

    private static Rect2D? TryGetEllipseBounds(PdfSubpath subpath, IReadOnlyList<Point2D> points)
    {
        var bezierCount = subpath.Commands.Count(command => command.GetType().Name == "BezierCurve");
        var hasOtherGeometry = subpath.Commands.Any(command =>
            command.GetType().Name is not "Move" and not "BezierCurve" and not "Close");
        if (!subpath.IsClosed() || bezierCount != 4 || hasOtherGeometry || points.Count < 4)
            return null;

        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    private static PdfPoint GetPoint(object command, string propertyName)
    {
        var property = command.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidDataException($"Comando PDF sem a propriedade {propertyName}.");
        return (PdfPoint)(property.GetValue(command) ?? throw new InvalidDataException("Ponto PDF vazio."));
    }

    /// <summary>
    /// Pré-análise: decide o fator de escala das larguras de linha do documento.
    /// <para>
    /// Em PDFs "normais" (medidos: 1.pdf/2.pdf com 0,8-1,92 pt = 0,28-0,68 mm) o fator é 1 e a
    /// largura REAL do PDF é usada — FIDELIDADE. Mas vários PDFs industriais são exportados com
    /// uma matriz de transformação reduzida, o que deixa a largura em valores absurdos (medido:
    /// 5304 com TODAS as 3048 linhas em 23 pt = 8,1 mm; CFE071 até 44 pt = 15,5 mm). Nesses casos
    /// aplica-se UM único fator para o documento, que leva a espessura MEDIANA à espessura padrão
    /// do OperCAD — assim a hierarquia relativa (fina/média/grossa) do desenho é preservada e as
    /// medidas continuam coerentes.
    /// </para>
    /// </summary>
    private void ComputeLineWidthScale(UglyToad.PdfPig.Content.Page pdfPage)
    {
        if (_lineWidthScaleComputed || _options.LineWidthMode != PdfLineWidthMode.PdfNative)
            return;

        var widthsMm = new List<double>();
        foreach (var path in pdfPage.ExperimentalAccess.Paths)
        {
            if (path.IsClipping || !path.IsStroked)
                continue;
            var mm = (double)path.LineWidth * PointsToMillimeters;
            if (mm > 0)
                widthsMm.Add(mm);
        }

        _lineWidthScaleComputed = true;

        if (widthsMm.Count == 0)
            return;

        // Documento em escala utilizável: nada a ajustar, as larguras do PDF valem como estão.
        if (widthsMm.Max() <= _options.MaxPenMm)
            return;

        var median = Median(widthsMm);
        if (median <= 0)
            return;

        _lineWidthScale = _options.StandardPenMm / median;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return 0;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>
    /// Largura da linha em MILÍMETROS: conversão direta pt → mm do valor do PDF (fidelidade).
    /// "0 w" no PDF significa "a linha mais fina possível" → espessura hairline do OperCAD.
    /// </summary>
    private double PathWidthInMillimeters(decimal widthPt)
    {
        if (widthPt <= 0)
            return _options.HairlineWidthMm;

        return (double)widthPt * PointsToMillimeters * _lineWidthScale;
    }

    private static IEnumerable<List<UglyToad.PdfPig.Content.Letter>> BuildTextRuns(
        IReadOnlyList<UglyToad.PdfPig.Content.Letter> glyphs)
    {
        var lineTolerance = 0.8;
        var runs = new List<List<UglyToad.PdfPig.Content.Letter>>();

        foreach (var line in glyphs
            .GroupBy(letter => BuildTextLineKey(letter, lineTolerance))
            .OrderByDescending(group => group.Key.LinePosition))
        {
            var ordered = line.OrderBy(TextAdvancePosition).ToList();
            var current = new List<UglyToad.PdfPig.Content.Letter>();
            for (var index = 0; index < ordered.Count; index++)
            {
                var letter = ordered[index];
                if (current.Count > 0)
                {
                    var previous = current[^1];
                    var gap = TextGap(previous, letter, line.Key.Vertical);
                    var maxGap = Math.Max(2.0, letter.PointSize * 0.75);
                    if (gap > maxGap)
                    {
                        runs.Add(current);
                        current = new List<UglyToad.PdfPig.Content.Letter>();
                    }
                }
                current.Add(letter);
            }
            if (current.Count > 0)
                runs.Add(current);
        }

        return runs;
    }

    private static IReadOnlyList<UglyToad.PdfPig.Content.Letter> DeduplicateGlyphs(
        IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UglyToad.PdfPig.Content.Letter>();
        foreach (var letter in letters)
        {
            var key = string.Join("|",
                letter.Value,
                Math.Round(letter.GlyphRectangle.Left, 2).ToString("F2", CultureInfo.InvariantCulture),
                Math.Round(letter.GlyphRectangle.Bottom, 2).ToString("F2", CultureInfo.InvariantCulture),
                Math.Round(letter.GlyphRectangle.Width, 2).ToString("F2", CultureInfo.InvariantCulture),
                Math.Round(letter.GlyphRectangle.Height, 2).ToString("F2", CultureInfo.InvariantCulture),
                NormalizeFontFamily(letter.FontName),
                Math.Round(letter.PointSize, 2).ToString("F2", CultureInfo.InvariantCulture));
            if (seen.Add(key))
                result.Add(letter);
        }
        return result;
    }

    private static TextLineKey BuildTextLineKey(UglyToad.PdfPig.Content.Letter letter, double tolerance)
    {
        var dx = letter.EndBaseLine.X - letter.StartBaseLine.X;
        var dy = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
        var vertical = Math.Abs(dy) > Math.Abs(dx);
        var linePosition = vertical ? letter.StartBaseLine.X : letter.StartBaseLine.Y;
        var angle = Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI / 5) * 5;
        return new TextLineKey(
            NormalizeFontFamily(letter.FontName),
            Math.Round(letter.PointSize, 1),
            angle,
            vertical,
            Math.Round(linePosition / tolerance) * tolerance);
    }

    private static double TextAdvancePosition(UglyToad.PdfPig.Content.Letter letter)
        => Math.Abs(letter.EndBaseLine.X - letter.StartBaseLine.X) >=
           Math.Abs(letter.EndBaseLine.Y - letter.StartBaseLine.Y)
            ? letter.StartBaseLine.X
            : letter.StartBaseLine.Y;

    private static double TextGap(
        UglyToad.PdfPig.Content.Letter previous,
        UglyToad.PdfPig.Content.Letter current,
        bool vertical)
    {
        if (vertical)
            return current.GlyphRectangle.Bottom - previous.GlyphRectangle.Top;
        return current.GlyphRectangle.Left - previous.GlyphRectangle.Right;
    }

    /// <summary>Fonte de fallback quando o nome da fonte do PDF não é utilizável.</summary>
    public const string DefaultFallbackFont = "Arial";

    /// <summary>
    /// Normaliza o nome da fonte do PDF para uma família utilizável. Muitos PDFs trazem nomes
    /// "de máquina" (ex.: <c>FEFD43854F7-Roman</c>) que não existem no sistema — o Skia cairia
    /// no tipo padrão e o texto perderia fidelidade silenciosamente. Nesses casos mapeamos para
    /// a fonte de fallback, preservando tamanho, estilo, rotação e cor originais.
    /// </summary>
    private static string NormalizeFontFamily(string? fontName, string fallback = DefaultFallbackFont)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return fallback;

        // Remove prefixo de subset (ex.: "ABCDEF+Arial").
        var name = Regex.Replace(fontName, "^[A-Z]{6}\\+", string.Empty);
        name = Regex.Replace(name, "(?i)(MT|PSMT|Identity-H)$", string.Empty).Trim();
        if (name.Length == 0)
            return fallback;

        if (name.Contains("Arial", StringComparison.OrdinalIgnoreCase))
            return "Arial";

        // Remove sufixos de estilo para julgar a FAMÍLIA.
        var family = Regex.Replace(name,
            @"[-_](Roman|Regular|Normal|Book|Medium|Bold|Italic|Oblique|Black|Light|Condensed|BoldMT|BoldItalic)$",
            string.Empty, RegexOptions.IgnoreCase).Trim();

        return IsPlausibleFontFamily(family) ? family : fallback;
    }

    /// <summary>
    /// Heurística: o nome parece uma FAMÍLIA de fonte real (e não um identificador de subset
    /// gerado pelo produtor do PDF, tipo <c>FEFD43854F7</c>).
    /// </summary>
    private static bool IsPlausibleFontFamily(string name)
    {
        if (name.Length is < 3 or > 32)
            return false;
        if (Regex.IsMatch(name, @"^[0-9A-F]{6,}$"))
            return false;                                     // hex puro (subset)
        if (Regex.IsMatch(name, @"^[A-Z]{5,}[0-9]"))
            return false;                                     // FEFD43854F7-like
        if (!Regex.IsMatch(name, "[A-Za-z]{3}"))
            return false;
        if (!Regex.IsMatch(name, "[aeiouAEIOU]"))
            return false;                                     // sem vogal: não é palavra
        return true;
    }

    /// <summary>
    /// Espessura do traço importado. Em <see cref="PdfLineWidthMode.PdfNative"/> (padrão) usa a
    /// LARGURA REAL do PDF convertida para mm — fidelidade ao original; em
    /// <see cref="PdfLineWidthMode.Uniform"/> força a espessura padrão do OperCAD (0,29 mm) em
    /// todas as linhas.
    /// </summary>
    private double PenWidthFor(decimal widthPt)
        => _options.LineWidthMode == PdfLineWidthMode.Uniform
            ? _options.NormalLineWidthMm
            : PathWidthInMillimeters(widthPt);

    private static LineDashStyle ParseDashStyle(object? pattern)
    {
        var value = pattern?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Contains("[]", StringComparison.Ordinal))
            return LineDashStyle.Solid;
        if (value.Contains(".", StringComparison.Ordinal) && value.Contains(",", StringComparison.Ordinal))
            return LineDashStyle.DashDot;
        return LineDashStyle.Dashed;
    }

    private static List<double> ParseDashPattern(object? pattern)
    {
        var value = pattern?.ToString() ?? string.Empty;
        var match = Regex.Match(value, @"\[(.*?)\]");
        if (!match.Success)
            return new List<double>();
        return Regex.Matches(match.Groups[1].Value, @"[-+]?\d+(?:[.,]\d+)?")
            .Select(token => double.TryParse(token.Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) ? number : double.NaN)
            .Where(number => !double.IsNaN(number) && number > 0)
            .Select(number => number * PointsToMillimeters)
            .ToList();
    }

    private static double ParseDashPhase(object? pattern)
    {
        var value = pattern?.ToString() ?? string.Empty;
        var close = value.IndexOf(']');
        if (close < 0)
            return 0;
        var match = Regex.Match(value[(close + 1)..], @"[-+]?\d+(?:[.,]\d+)?");
        return match.Success && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var phase) ? phase * PointsToMillimeters : 0;
    }

    private static ShapeLineCap ParseLineCap(object? value)
        => value?.ToString() switch
        {
            "Butt" => ShapeLineCap.Butt,
            "Square" => ShapeLineCap.Square,
            _ => ShapeLineCap.Round
        };

    private static ShapeLineJoin ParseLineJoin(object? value)
        => value?.ToString() switch
        {
            "Miter" => ShapeLineJoin.Miter,
            "Bevel" => ShapeLineJoin.Bevel,
            _ => ShapeLineJoin.Round
        };

    private static string PdfColorToHex(object? color)
    {
        if (color is null)
            return "#000000";

        var type = color.GetType();

        // Tentar RGB primeiro (R, G, B)
        var red = type.GetProperty("R")?.GetValue(color);
        var green = type.GetProperty("G")?.GetValue(color);
        var blue = type.GetProperty("B")?.GetValue(color);
        if (red is not null && green is not null && blue is not null &&
            decimal.TryParse(red.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) &&
            decimal.TryParse(green.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var g) &&
            decimal.TryParse(blue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            static int Channel(decimal value) => (int)Math.Clamp(Math.Round(value <= 1 ? value * 255 : value), 0, 255);
            return $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
        }

        // Tentar CMYK (C, M, Y, K)
        var cyan = type.GetProperty("C")?.GetValue(color);
        var magenta = type.GetProperty("M")?.GetValue(color);
        var yellow = type.GetProperty("Y")?.GetValue(color);
        var key = type.GetProperty("K")?.GetValue(color);
        if (cyan is not null && magenta is not null && yellow is not null && key is not null &&
            decimal.TryParse(cyan.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) &&
            decimal.TryParse(magenta.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
            decimal.TryParse(yellow.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            decimal.TryParse(key.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var k))
        {
            // CMYK → RGB (valores 0..1)
            static int CmykChannel(decimal c, decimal m, decimal y, decimal k)
            {
                var v = (1 - c) * (1 - k);
                return (int)Math.Clamp(Math.Round(v <= 1 ? v * 255 : v), 0, 255);
            }
            return $"#{CmykChannel(c, m, y, k):X2}{CmykChannel(m, c, y, k):X2}{CmykChannel(y, c, m, k):X2}";
        }

        // Tentar grayscale (Gray property ou um único valor)
        var gray = type.GetProperty("Gray")?.GetValue(color) ?? type.GetProperty("Grey")?.GetValue(color);
        if (gray is not null &&
            decimal.TryParse(gray.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gs))
        {
            static int GsChannel(decimal value) => (int)Math.Clamp(Math.Round(value <= 1 ? value * 255 : value), 0, 255);
            var hex = GsChannel(gs);
            return $"#{hex:X2}{hex:X2}{hex:X2}";
        }

        // Fallback: tentar extrair de string "(R, G, B)" ou "rgb(r, g, b)"
        var str = color.ToString() ?? string.Empty;
        var rgbMatch = Regex.Match(str, @"rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");
        if (!rgbMatch.Success)
            rgbMatch = Regex.Match(str, @"\((\d+)\s*,\s*(\d+)\s*,\s*(\d+)\)");
        if (rgbMatch.Success)
            return $"#{int.Parse(rgbMatch.Groups[1].Value):X2}{int.Parse(rgbMatch.Groups[2].Value):X2}{int.Parse(rgbMatch.Groups[3].Value):X2}";

        // Fallback: string de cor CSS
        var cssMatch = Regex.Match(str, @"#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})");
        if (cssMatch.Success)
            return cssMatch.Value;

        return "#000000";
    }

    private readonly record struct TextLineKey(
        string Font, double Size, double Angle, bool Vertical, double LinePosition);

    private readonly record struct PdfPageTransform(
        double WidthMm, double HeightMm, int RotationDegrees, double WidthPt, double HeightPt)
    {
        public static PdfPageTransform Create(double widthPt, double heightPt, object rotation)
        {
            var degrees = rotation.ToString() switch
            {
                "90" or "Ninety" => 90,
                "180" or "OneHundredEighty" => 180,
                "270" or "TwoHundredSeventy" => 270,
                _ => 0
            };
            var rotated = degrees is 90 or 270;
            return new PdfPageTransform(
                (rotated ? heightPt : widthPt) * PointsToMillimeters,
                (rotated ? widthPt : heightPt) * PointsToMillimeters,
                degrees, widthPt, heightPt);
        }

        public Point2D Convert(PdfPoint point)
        {
            var converted = RotationDegrees switch
            {
                90 => new Point2D(HeightPt - point.Y, point.X),
                180 => new Point2D(WidthPt - point.X, HeightPt - point.Y),
                270 => new Point2D(point.Y, WidthPt - point.X),
                _ => new Point2D(point.X, point.Y)
            };
            return new Point2D(converted.X * PointsToMillimeters, converted.Y * PointsToMillimeters);
        }
    }
}

public sealed class PdfImportOptions
{
    /// <summary>
    /// Como as espessuras de linha do PDF são tratadas. Padrão
    /// <see cref="PdfLineWidthMode.PdfNative"/>: FIDELIDADE — a largura REAL de cada traço do PDF é
    /// convertida para mm (pt × 0,3528). Só há um ajuste global quando o documento está fora de
    /// escala (todas as linhas com valores absurdos, ex.: 23 pt = 8,1 mm).
    /// </summary>
    public PdfLineWidthMode LineWidthMode { get; init; } = PdfLineWidthMode.PdfNative;

    /// <summary>
    /// Maior espessura (mm) aceita como "largura real do PDF". Acima disto considera-se que o PDF
    /// está em outra escala e um único fator (mediana → <see cref="StandardPenMm"/>) é aplicado ao
    /// documento, preservando a hierarquia relativa das linhas.
    /// </summary>
    public double MaxPenMm { get; init; } = 1.43;

    public double HairlineWidthMm { get; init; } = 0.07;
    public double NormalLineWidthMm { get; init; } = 0.29;

    /// <summary>Modo de destino: FullPage (estratégia A) ou NativeSheet (estratégia B).</summary>
    public PdfImportTarget Target { get; init; } = PdfImportTarget.FullPage;

    /// <summary>
    /// Como o conteúdo recortado é colocado na folha nativa. Padrão
    /// <see cref="PdfImportScaleMode.RealScale"/>: as dimensões do PDF são PRESERVADAS (1:1) —
    /// indispensável para reutilizar a geometria importada.
    /// </summary>
    public PdfImportScaleMode ScaleMode { get; init; } = PdfImportScaleMode.RealScale;

    /// <summary>
    /// Escolhe automaticamente a MENOR folha padrão (A4…A0) que comporte o recorte em escala
    /// real; se nenhuma servir, usa uma folha sob medida. Usado apenas no modo
    /// <see cref="PdfImportScaleMode.RealScale"/>; desligue para impor <see cref="Paper"/> 1:1
    /// (o conteúdo que não couber é cortado).
    /// </summary>
    public bool AutoPaperSize { get; init; } = true;

    /// <summary>Folha usada quando a escolha automática está desligada (ou no modo "ajustar").</summary>
    public PaperSize Paper { get; init; } = PaperSize.A4;

    /// <summary>Folga (mm) entre o recorte e a área útil da folha no modo escala real.</summary>
    public double RealScaleMarginMm { get; init; } = 2.0;

    /// <summary>
    /// Encaixa os pontos à malha de 2,5 mm do OperCAD quando o resíduo é pequeno (≤0,25 mm).
    /// <para>
    /// DESLIGADO por padrão — e deve continuar assim para fidelidade: o encaixe move cada ponto em
    /// até 0,25 mm, o que DEFORMA detalhes finos. Comprovado: os glifos de texto vetorizado (letras
    /// de ~2 mm) tinham os contornos deslocados, criando auto-interseções e, com preenchimento
    /// par-ímpar, FUROS nas letras ("texto vazado").
    /// </para>
    /// </summary>
    public bool SnapToGrid { get; init; }

    /// <summary>
    /// Distância (mm) entre contornos de um mesmo path PREENCHIDO para que continuem no MESMO
    /// objeto. Contornos mais afastados que isso viram objetos separados.
    /// <para>
    /// Motivo: no PDF um único path costuma conter TODOS os rótulos de uma linha (medido no arquivo
    /// real: 109 mm de largura com as etiquetas de vários contatos). Sem separar, selecionar o
    /// rótulo de um contato trazia o rótulo de outro a 98 mm de distância, sem como editar ou
    /// excluir um sem o outro. Contornos ANINHADOS (o furo de uma letra) ficam sempre juntos.
    /// </para>
    /// </summary>
    public double LabelClusterGapMm { get; init; } = 1.5;

    /// <summary>Recorte explícito da área útil (por página), em mm do mundo da página PDF.</summary>
    public PdfPageCrop? Crop { get; init; }

    /// <summary>Recortes por página (suporta PDF multipágina com áreas úteis distintas).</summary>
    public List<PdfPageCrop>? PageCrops { get; init; }

    /// <summary>Margens da folha nativa gerada (mm).</summary>
    public double MarginLeftMm { get; init; } = 12;
    public double MarginRightMm { get; init; } = 8;
    public double MarginTopMm { get; init; } = 8;
    public double MarginBottomMm { get; init; } = 12;

    /// <summary>Exibir moldura nativa do OperCAD na folha refluída.</summary>
    public bool ShowFrame { get; init; } = true;
    public bool ShowGrid { get; init; } = true;

    /// <summary>Clusters reconhecidos pelo wizard (fase 2) para conversão em PlacedElement.</summary>
    public List<ShapeCluster>? RecognizedClusters { get; init; }

    /// <summary>Fonte usada quando o nome da fonte do PDF não é utilizável (subset/inexistente).</summary>
    public string FallbackFontFamily { get; init; } = PdfImporter.DefaultFallbackFont;

    /// <summary>
    /// Agrupa o conteúdo importado por símbolo (1 grupo = 1 símbolo editável).
    /// DESLIGADO por padrão: em diagramas ligados por fios a heurística pode juntar traços que
    /// não fazem parte do mesmo conjunto. O usuário seleciona o conjunto e usa Ctrl+G (a seleção
    /// dele é a autoridade). Ligue para tentar o agrupamento automático.
    /// </summary>
    public bool GroupSymbols { get; init; }

    /// <summary>Distância máxima (mm) entre bounding boxes para unir primitivas no mesmo símbolo.</summary>
    public double SymbolGapMm { get; init; } = 1.5;

    /// <summary>Comprimento (mm) a partir do qual um traço fino é tratado como FIO (cortado do símbolo).</summary>
    public double WireMinLengthMm { get; init; } = 15.0;

    /// <summary>Espessura máxima (mm) do bbox de um traço para que ele possa ser tratado como fio.</summary>
    public double WireMaxThicknessMm { get; init; } = 0.8;

    /// <summary>Componentes maiores que esta largura (mm) são descartados como símbolo.</summary>
    public double MaxSymbolWidthMm { get; init; } = 210.0;

    /// <summary>Componentes mais altos que isto (mm) são descartados como símbolo.</summary>
    public double MaxSymbolHeightMm { get; init; } = 150.0;

    /// <summary>
    /// Emite UM OBJETO POR SEGMENTO nos caminhos apenas traçados — granularidade do editor de
    /// PDF (selecionar/copiar/colorir/reutilizar um traço por vez). Desligue para manter cada
    /// figura como um único objeto.
    /// </summary>
    public bool SplitStrokedPathsIntoSegments { get; init; } = true;

    /// <summary>
    /// Calibra a espessura das formas já transformadas: formas pequenas (glifos de texto
    /// vetorizado) e formas preenchidas recebem caneta fina, para o texto NÃO ficar grosso;
    /// as linhas de desenho recebem a espessura padrão do OperCAD.
    /// <para>
    /// DESLIGADO por padrão: a fidelidade ao PDF (<see cref="LineWidthMode"/> =
    /// <see cref="PdfLineWidthMode.PdfNative"/>) é o comportamento desejado. Ligue para o
    /// tratamento antigo, que uniformiza as espessuras.
    /// </para>
    /// </summary>
    public bool CalibrateStrokeWidths { get; init; }

    /// <summary>Espessura padrão (mm) das LINHAS de desenho importadas. Padrão do OperCAD.</summary>
    public double StandardPenMm { get; init; } = 0.29;

    /// <summary>Espessura (mm) para glifos de texto vetorizado (formas pequenas).</summary>
    public double ThinPenMm { get; init; } = 0.15;

    /// <summary>Raio (mm) usado para reconhecer um aglomerado de traços curtos como TEXTO.</summary>
    public double GlyphClusterRadiusMm { get; init; } = 4.0;

    /// <summary>Mínimo de traços curtos vizinhos para uma região ser tratada como texto.</summary>
    public int GlyphMinNeighbours { get; init; } = 6;

    /// <summary>Espessura (mm) para formas PREENCHIDAS (o preenchimento define a aparência).</summary>
    public double HairlinePenMm { get; init; } = 0.07;

    /// <summary>
    /// Tamanho máximo (mm) do maior lado para uma forma ser tratada como GLIFO de texto
    /// vetorizado. Medido nos PDFs reais: os glifos ficam em 0-3 mm e o desenho em 4+ mm.
    /// </summary>
    public double GlyphMaxSizeMm { get; init; } = 3.5;
}

/// <summary>Modo de destino da importação de um PDF.</summary>
public enum PdfImportTarget { FullPage, NativeSheet }

/// <summary>Como as espessuras de linha do PDF são tratadas na importação.</summary>
public enum PdfLineWidthMode
{
    /// <summary>Largura REAL do PDF (pt → mm) — fidelidade ao original.</summary>
    PdfNative,

    /// <summary>Todas as linhas na espessura padrão do OperCAD (0,29 mm).</summary>
    Uniform
}

/// <summary>Como o conteúdo recortado é dimensionado ao entrar na folha nativa.</summary>
public enum PdfImportScaleMode
{
    /// <summary>Escala REAL: o desenho mantém as dimensões do PDF (1:1) — só é transladado.</summary>
    RealScale,

    /// <summary>Ajusta o recorte à área útil da folha escolhida (reescala uniforme, sem distorcer).</summary>
    FitToSheet
}

/// <summary>
/// Plano de folha calculado para um recorte: papel escolhido, escala a aplicar e se o conteúdo
/// cabe na área útil de desenho (<see cref="FitsDrawingArea"/>) e na folha (<see cref="FitsSheet"/>).
/// Ver <see cref="PdfImporter.PlanSheet"/>.
/// </summary>
public sealed record PdfSheetPlan(PaperSize Size, double WidthMm, double HeightMm, double Scale,
    bool FitsDrawingArea, bool FitsSheet)
{
    /// <summary>Nome curto do papel ("A4", "A3", "sob medida").</summary>
    public string PaperName => Size == PaperSize.Custom ? "sob medida" : Size.ToString();

    /// <summary>Folha em texto legível ("A3 420×297 mm"), para exibição no wizard.</summary>
    public string SheetDescription => $"{PaperName} {WidthMm:0.#}×{HeightMm:0.#} mm";

    /// <summary>Descrição do resultado da importação para o recorte informado (usada no wizard).</summary>
    public string Describe(Rect2D region)
    {
        var crop = $"recorte {region.Width:0.#}×{region.Height:0.#} mm";
        var scale = Scale == 1.0
            ? "escala 1:1"
            : $"conteúdo em {Scale * 100:0.#}% da escala original";
        var nota = !FitsSheet
            ? " — ATENÇÃO: não cabe na folha, o excedente será cortado"
            : !FitsDrawingArea
                ? " — ocupa também a faixa da moldura/carimbo (não é cortado)"
                : string.Empty;

        return $"Folha {SheetDescription} — {scale} ({crop}){nota}.";
    }
}

/// <summary>Retângulo da área útil selecionada de uma página, em mm (mundo da página PDF).</summary>
public sealed record PdfPageCrop(int PageNumber, Rect2D RectMm);