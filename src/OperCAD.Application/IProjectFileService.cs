using System.Globalization;
using System.Xml.Linq;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;
using OperCAD.Core.TerminalStrips;

namespace OperCAD.Application;

/// <summary>
/// Serviço de persistência de projetos. Salva/abre o projeto em XML (formato
/// nativo do OperCAD). Persiste configurações de autonumeração, folhas (moldura,
/// legenda, legenda), elementos, condutores (com número), textos independentes,
/// formas, imagens e bornes (terminal strips).
/// </summary>
public interface IProjectFileService
{
    Task SaveAsync(Project project, string filePath);
    Task<Project> LoadAsync(string filePath);
}

public sealed class ProjectFileService : IProjectFileService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public Task SaveAsync(Project project, string filePath)
    {
        var doc = new XDocument(new XElement("operCAD",
            new XAttribute("project_version", "0.2"),
            new XElement("name", project.Name),
            ProjectConfigXml(project),
            TerminalStripXml.ToXml(project.TerminalStrips),
            new XElement("pages",
                project.Pages.Select(p => PageToXml(p, project.ActivePage == p)))));

        doc.Save(filePath);
        return Task.CompletedTask;
    }

    public Task<Project> LoadAsync(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "operCAD")
            throw new InvalidDataException("Arquivo de projeto inválido.");

        var project = new Project
        {
            Name = root.Element("name")?.Value ?? "Projeto"
        };

        var config = root.Element("config");
        if (config is not null)
        {
            project.ConductorAutoNumPrefix = config.Attribute("conductorPrefix")?.Value ?? string.Empty;
            project.ConductorAutoNumStep = Int(config.Attribute("conductorStep"), 1);
            project.ConductorAutoNumEnabled = Bool(config.Attribute("conductorAutoNum"), true);
            project.ElementAutoNumEnabled = Bool(config.Attribute("elementAutoNum"), true);
            project.ElementAutoNumFormula = config.Attribute("elementFormula")?.Value ?? "%prefix%%autonum%";
            project.FolhaAutoNumFormula = config.Attribute("folioFormula")?.Value ?? "%id/%total";
        }

        var stripsEl = root.Element(TerminalStripXml.ItemsTag);
        if (stripsEl is not null)
        {
            foreach (var strip in TerminalStripXml.FromXml(stripsEl))
                project.TerminalStrips.Add(strip.Strip);
        }

        var pagesEl = root.Element("pages");
        if (pagesEl is not null)
        {
            foreach (var pageEl in pagesEl.Elements("page"))
            {
                var page = XmlToPage(pageEl);
                project.Pages.Add(page);
                if (pageEl.Attribute("active")?.Value == "true")
                    project.ActivePage = page;
            }
        }

        if (project.ActivePage is null && project.Pages.Count > 0)
            project.ActivePage = project.Pages[0];

        return Task.FromResult(project);
    }

    private static XElement ProjectConfigXml(Project project)
        => new("config",
            new XAttribute("conductorPrefix", project.ConductorAutoNumPrefix ?? string.Empty),
            new XAttribute("conductorStep", project.ConductorAutoNumStep),
            new XAttribute("conductorAutoNum", project.ConductorAutoNumEnabled),
            new XAttribute("elementAutoNum", project.ElementAutoNumEnabled),
            new XAttribute("elementFormula", project.ElementAutoNumFormula ?? "%prefix%%autonum%"),
            new XAttribute("folioFormula", project.FolhaAutoNumFormula ?? "%id/%total"));

    private static XElement PageToXml(Page page, bool active)
    {
        var s = page.Settings;
        var t = page.TitleBlock;

        var settings = new XElement("settings",
            new XAttribute("size", s.Size),
            new XAttribute("customWidth", Str(s.CustomWidth)),
            new XAttribute("customHeight", Str(s.CustomHeight)),
            new XAttribute("folioNumber", s.FolioNumber ?? string.Empty),
            new XAttribute("autoIncrementFolio", s.AutoIncrementFolio),
            new XAttribute("folioTitle", s.FolioTitle ?? string.Empty),
            new XAttribute("folioSubtitle", s.FolioSubtitle ?? string.Empty),
            new XAttribute("folioPlant", s.FolioPlant ?? string.Empty),
            new XAttribute("folioSection", s.FolioSection ?? string.Empty),
            new XAttribute("folioLocation", s.FolioLocation ?? string.Empty),
            new XAttribute("showFrame", s.ShowFrame),
            new XAttribute("showGrid", s.ShowGrid),
            new XAttribute("showColumns", s.ShowColumns),
            new XAttribute("showRows", s.ShowRows),
            new XAttribute("columnsCount", s.ColumnsCount),
            new XAttribute("columnsWidth", Str(s.ColumnsWidth)),
            new XAttribute("columnsHeaderHeight", Str(s.ColumnsHeaderHeight)),
            new XAttribute("rowsCount", s.RowsCount),
            new XAttribute("rowsHeight", Str(s.RowsHeight)),
            new XAttribute("rowsHeaderWidth", Str(s.RowsHeaderWidth)),
            new XAttribute("marginLeft", Str(s.MarginLeft)),
            new XAttribute("marginRight", Str(s.MarginRight)),
            new XAttribute("marginTop", Str(s.MarginTop)),
            new XAttribute("marginBottom", Str(s.MarginBottom)),
            new XAttribute("conductorColor", s.ConductorColor),
            new XAttribute("conductorWidth", Str(s.ConductorWidth)),
            new XAttribute("elementScale", Str(s.ElementScale)),
            new XAttribute("backgroundColor", s.BackgroundColor ?? "#FFFFFF"),
            new XAttribute("titleBlockFile", s.TitleBlockFile ?? string.Empty),
            new XAttribute("titleBlockPosition", s.TitleBlockPosition),
            new XAttribute("folhaTemplateFile", s.FolhaTemplateFile ?? string.Empty),
            new XAttribute("folhaFrameInset", Str(s.FolhaFrameInset)));

        var titleblock = new XElement("titleblock",
            new XAttribute("title", t.Title ?? string.Empty),
            new XAttribute("company", t.Company ?? string.Empty),
            new XAttribute("obra", t.Obra ?? string.Empty),
            new XAttribute("drawingNumber", t.DrawingNumber ?? string.Empty),
            new XAttribute("revision", t.Revision ?? string.Empty),
            new XAttribute("scale", t.Scale ?? string.Empty),
            new XAttribute("sheet", t.Sheet ?? string.Empty),
            new XAttribute("totalSheets", t.TotalSheets ?? string.Empty),
            new XAttribute("sheetAutoNumbered", t.SheetAutoNumbered),
            new XAttribute("totalSheetsAutoNumbered", t.TotalSheetsAutoNumbered),
            new XAttribute("author", t.Author ?? string.Empty),
            new XAttribute("reviewedBy", t.ReviewedBy ?? string.Empty),
            new XAttribute("approvedBy", t.ApprovedBy ?? string.Empty),
            new XAttribute("date", t.Date.ToString("yyyy-MM-dd", Inv)));

        var elements = new XElement("elements",
            page.Objects.OfType<PlacedElement>().Select(e => new XElement("element",
                new XAttribute("id", e.Id),
                new XAttribute("source", e.Definition.SourcePath),
                new XAttribute("category", e.Definition.Category),
                new XAttribute("x", Str(e.X)),
                new XAttribute("y", Str(e.Y)),
                new XAttribute("rotation", Str(e.Rotation)),
                new XAttribute("scale", Str(e.Scale)),
                new XAttribute("label", e.Label ?? string.Empty),
                new XAttribute("comment", e.Comment ?? string.Empty),
                new XAttribute("function", e.Function ?? string.Empty),
                new XAttribute("description", e.Description ?? string.Empty),
                new XAttribute("manufacturer", e.Manufacturer ?? string.Empty),
                new XAttribute("designation", e.Designation ?? string.Empty),
                e.TextOverrides.Count == 0 ? null : new XElement("textOverrides",
                    e.TextOverrides.Select(kv => new XElement("t",
                        new XAttribute("i", kv.Key), kv.Value))),
                new XElement("links",
                    e.LinkedUuids.Select(id => new XElement("link", new XAttribute("uuid", id)))))));

        var conductors = new XElement("conductors",
            page.Objects.OfType<Conductor>().Select(c => new XElement("conductor",
                new XAttribute("id", c.Id),
                new XAttribute("fromEl", c.From.ElementId),
                new XAttribute("fromTerm", c.From.TerminalIndex),
                new XAttribute("toEl", c.To.ElementId),
                new XAttribute("toTerm", c.To.TerminalIndex),
                new XAttribute("color", c.Color ?? "black"),
                new XAttribute("width", Str(c.Width)),
                new XAttribute("text", c.Text ?? string.Empty),
                new XAttribute("textColor", c.TextColor ?? "#000000"),
                new XAttribute("wireType", c.WireType ?? string.Empty),
                new XAttribute("section", c.Section ?? string.Empty),
                new XAttribute("cable", c.Cable ?? string.Empty),
                new XAttribute("bus", c.Bus ?? string.Empty),
                new XAttribute("function", c.Function ?? string.Empty),
                new XAttribute("tensionProtocol", c.TensionProtocol ?? string.Empty),
                new XAttribute("numSize", c.NumSize),
                new XAttribute("displayText", c.DisplayText),
                new XAttribute("formula", c.Formula ?? string.Empty),
                new XAttribute("sequenceNumber", c.SequenceNumber),
                new XAttribute("path", ConductorPathToString(c.Path)),
                new XAttribute("continuity", c.Continuity),
                new XAttribute("preservePath", c.PreservePath),
                new XElement("junctions",
                    c.Junctions.Select(j => new XElement("junction",
                        new XAttribute("x", Str(j.Position.X)),
                        new XAttribute("y", Str(j.Position.Y)),
                        new XAttribute("conductor", j.ConductorId),
                        new XAttribute("termEl", j.TerminalElementId?.ToString() ?? string.Empty),
                        new XAttribute("termIdx", j.TerminalIndex)))))));

        var texts = new XElement("texts",
            page.Objects.OfType<TextItem>().Select(tx => new XElement("text",
                new XAttribute("id", tx.Id),
                new XAttribute("text", tx.Text ?? string.Empty),
                new XAttribute("x", Str(tx.X)),
                new XAttribute("y", Str(tx.Y)),
                new XAttribute("rotation", Str(tx.Rotation)),
                new XAttribute("fontFamily", tx.FontFamily ?? TextItem.DefaultFontFamily),
                new XAttribute("fontSizePt", Str(tx.FontSizePt)),
                new XAttribute("bold", tx.Bold),
                new XAttribute("italic", tx.Italic),
                new XAttribute("underline", tx.Underline),
                new XAttribute("strikethrough", tx.Strikethrough),
                new XAttribute("color", tx.Color ?? "#000000"),
                new XAttribute("textalign", tx.TextAlign ?? "left"),
                new XAttribute("importGroupId", tx.ImportGroupId?.ToString() ?? string.Empty),
                new XAttribute("locked", tx.IsLocked),
                new XAttribute("layerId", tx.LayerId?.ToString() ?? string.Empty))));

        var shapes = new XElement("shapes",
            page.Objects.OfType<ShapeItem>().Select(sh => new XElement("shape",
                new XAttribute("id", sh.Id),
                new XAttribute("kind", sh.Kind),
                new XAttribute("color", sh.Color ?? "black"),
                new XAttribute("penWidth", Str(sh.PenWidth)),
                new XAttribute("dashStyle", sh.DashStyle),
                new XAttribute("dashPattern", string.Join(",", sh.DashPattern.Select(Str))),
                new XAttribute("dashPhase", Str(sh.DashPhase)),
                new XAttribute("lineCap", sh.LineCap),
                new XAttribute("lineJoin", sh.LineJoin),
                new XAttribute("filling", sh.Filling ?? "none"),
                new XAttribute("closed", sh.Closed),
                new XAttribute("startAngle", Str(sh.StartAngle)),
                new XAttribute("sweepAngle", Str(sh.SweepAngle)),
                new XAttribute("importGroupId", sh.ImportGroupId?.ToString() ?? string.Empty),
                new XAttribute("pdfObject", sh.PdfObjectIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
                sh.ContourStarts is { Count: > 1 }
                    ? new XAttribute("contours", string.Join(",", sh.ContourStarts))
                    : null,
                new XAttribute("locked", sh.IsLocked),
                new XAttribute("layerId", sh.LayerId?.ToString() ?? string.Empty),
                new XElement("points",
                    sh.Points.Select(p => new XElement("point",
                        new XAttribute("x", Str(p.X)),
                        new XAttribute("y", Str(p.Y)))),
                sh.ControlPoints is { } cp
                    ? new XElement("controlPoints",
                        cp.Select(c => new XElement("cp",
                            new XAttribute("c1x", Str(c.C1.X)),
                            new XAttribute("c1y", Str(c.C1.Y)),
                            new XAttribute("c2x", Str(c.C2.X)),
                            new XAttribute("c2y", Str(c.C2.Y)))))
                    : null))));

        var images = new XElement("images",
            page.Objects.OfType<ImageItem>().Select(img => new XElement("image",
                new XAttribute("id", img.Id),
                new XAttribute("filePath", img.FilePath ?? string.Empty),
                new XAttribute("x", Str(img.X)),
                new XAttribute("y", Str(img.Y)),
                new XAttribute("width", Str(img.Width)),
                new XAttribute("height", Str(img.Height)),
                new XAttribute("layerId", img.LayerId?.ToString() ?? string.Empty))));

        var stripItems = new XElement("terminal_strip_items",
            page.Objects.OfType<TerminalStripItem>().Select(item => new XElement("terminal_strip_item",
                new XAttribute("id", item.Id),
                new XAttribute("stripUuid", item.StripUuid),
                new XAttribute("x", Str(item.X)),
                new XAttribute("y", Str(item.Y)),
                new XAttribute("width", Str(item.Width)),
                new XAttribute("rowHeight", Str(item.RowHeight)))));

        var layers = new XElement("layers",
            page.Layers.Select(l => new XElement("layer",
                new XAttribute("id", l.Id),
                new XAttribute("name", l.Name ?? string.Empty),
                new XAttribute("visible", l.IsVisible),
                new XAttribute("locked", l.IsLocked),
                new XAttribute("color", l.Color ?? "#808080"),
                new XAttribute("zOrder", l.ZOrder))));

        return new XElement("page",
            new XAttribute("name", page.Name ?? "Folha"),
            new XAttribute("active", active),
            settings, titleblock, layers, elements, conductors, texts, shapes, images, stripItems);
    }

    /// <summary>Lê os índices de início de contorno gravados no atributo "contours".</summary>
    private static List<int>? ParseContourStarts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var starts = new List<int>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var index))
                starts.Add(index);

        return starts.Count > 1 ? starts : null;
    }

    private static Page XmlToPage(XElement pageEl)
    {
        var page = new Page { Name = pageEl.Attribute("name")?.Value ?? "Folha" };

        var s = page.Settings;
        var se = pageEl.Element("settings");
        if (se is not null)
        {
            if (Enum.TryParse<PaperSize>(se.Attribute("size")?.Value, out var size)) s.Size = size;
            s.CustomWidth = Dbl(se.Attribute("customWidth"), s.CustomWidth);
            s.CustomHeight = Dbl(se.Attribute("customHeight"), s.CustomHeight);
            s.FolioNumber = se.Attribute("folioNumber")?.Value ?? string.Empty;
            s.AutoIncrementFolio = Bool(se.Attribute("autoIncrementFolio"), false);
            s.FolioTitle = se.Attribute("folioTitle")?.Value ?? string.Empty;
            s.FolioSubtitle = se.Attribute("folioSubtitle")?.Value ?? string.Empty;
            s.FolioPlant = se.Attribute("folioPlant")?.Value ?? string.Empty;
            s.FolioSection = se.Attribute("folioSection")?.Value ?? string.Empty;
            s.FolioLocation = se.Attribute("folioLocation")?.Value ?? string.Empty;
            s.ShowFrame = Bool(se.Attribute("showFrame"), true);
            s.ShowGrid = Bool(se.Attribute("showGrid"), true);
            s.ShowColumns = Bool(se.Attribute("showColumns"), true);
            s.ShowRows = Bool(se.Attribute("showRows"), true);
            s.ColumnsCount = Int(se.Attribute("columnsCount"), s.ColumnsCount);
            s.ColumnsWidth = Dbl(se.Attribute("columnsWidth"), s.ColumnsWidth);
            s.ColumnsHeaderHeight = Dbl(se.Attribute("columnsHeaderHeight"), s.ColumnsHeaderHeight);
            s.RowsCount = Int(se.Attribute("rowsCount"), s.RowsCount);
            s.RowsHeight = Dbl(se.Attribute("rowsHeight"), s.RowsHeight);
            s.RowsHeaderWidth = Dbl(se.Attribute("rowsHeaderWidth"), s.RowsHeaderWidth);
            s.MarginLeft = Dbl(se.Attribute("marginLeft"), s.MarginLeft);
            s.MarginRight = Dbl(se.Attribute("marginRight"), s.MarginRight);
            s.MarginTop = Dbl(se.Attribute("marginTop"), s.MarginTop);
            s.MarginBottom = Dbl(se.Attribute("marginBottom"), s.MarginBottom);
            s.ConductorColor = se.Attribute("conductorColor")?.Value ?? "black";
            s.ConductorWidth = Dbl(se.Attribute("conductorWidth"), s.ConductorWidth);
            s.ElementScale = Dbl(se.Attribute("elementScale"), s.ElementScale);
            s.BackgroundColor = se.Attribute("backgroundColor")?.Value ?? "#FFFFFF";
            s.TitleBlockFile = string.IsNullOrEmpty(se.Attribute("titleBlockFile")?.Value)
                ? null
                : se.Attribute("titleBlockFile")!.Value;
            if (Enum.TryParse<TitleBlockPosition>(se.Attribute("titleBlockPosition")?.Value, out var pos))
                s.TitleBlockPosition = pos;
            s.FolhaTemplateFile = string.IsNullOrEmpty(se.Attribute("folhaTemplateFile")?.Value)
                ? null
                : se.Attribute("folhaTemplateFile")!.Value;
            s.FolhaFrameInset = Dbl(se.Attribute("folhaFrameInset"), s.FolhaFrameInset);
        }

        // Resolve o template de MOLDURA (template.json) do catálogo, se existir.
        s.FolhaTemplate = string.IsNullOrEmpty(s.FolhaTemplateFile)
            ? null
            : FolhaTemplateCatalog.Default.Get(s.FolhaTemplateFile);

        var tb = page.TitleBlock;
        var te = pageEl.Element("titleblock");
        if (te is not null)
        {
            tb.Title = te.Attribute("title")?.Value ?? string.Empty;
            tb.Company = te.Attribute("company")?.Value ?? string.Empty;
            tb.Obra = te.Attribute("obra")?.Value ?? string.Empty;
            tb.DrawingNumber = te.Attribute("drawingNumber")?.Value ?? string.Empty;
            tb.Revision = te.Attribute("revision")?.Value ?? string.Empty;
            tb.Scale = te.Attribute("scale")?.Value ?? string.Empty;
            tb.Sheet = te.Attribute("sheet")?.Value ?? string.Empty;
            tb.TotalSheets = te.Attribute("totalSheets")?.Value ?? string.Empty;
            tb.SheetAutoNumbered = Bool(te.Attribute("sheetAutoNumbered"), true);
            tb.TotalSheetsAutoNumbered = Bool(te.Attribute("totalSheetsAutoNumbered"), true);
            tb.Author = te.Attribute("author")?.Value ?? string.Empty;
            tb.ReviewedBy = te.Attribute("reviewedBy")?.Value ?? string.Empty;
            tb.ApprovedBy = te.Attribute("approvedBy")?.Value ?? string.Empty;
            if (System.DateTime.TryParse(te.Attribute("date")?.Value, Inv, System.Globalization.DateTimeStyles.None, out var date))
                tb.Date = date;
        }

        var layersEl = pageEl.Element("layers");
        if (layersEl is not null)
        {
            foreach (var l in layersEl.Elements("layer"))
            {
                var layer = new Layer
                {
                    Id = GuidOrNew(l.Attribute("id")?.Value),
                    Name = l.Attribute("name")?.Value ?? string.Empty,
                    IsVisible = Bool(l.Attribute("visible"), true),
                    IsLocked = Bool(l.Attribute("locked"), false),
                    Color = l.Attribute("color")?.Value ?? "#808080",
                    ZOrder = (int)Dbl(l.Attribute("zOrder"), 0)
                };
                page.Layers.Add(layer);
            }
        }

        var elements = pageEl.Element("elements");
        if (elements is not null)
        {
            foreach (var el in elements.Elements("element"))
            {
                var source = el.Attribute("source")?.Value;
                var category = el.Attribute("category")?.Value ?? string.Empty;
                var def = string.IsNullOrEmpty(source) ? null
                    : (source.EndsWith(".opcad", StringComparison.OrdinalIgnoreCase)
                       || source.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        ? OpcadParser.Parse(source, category)
                        : null;
                if (def is null)
                    continue;
                var placed = new PlacedElement(def)
                {
                    Id = GuidOrNew(el.Attribute("id")?.Value),
                    X = Dbl(el.Attribute("x")),
                    Y = Dbl(el.Attribute("y")),
                    Rotation = Dbl(el.Attribute("rotation")),
                    Scale = Dbl(el.Attribute("scale"), 1.0),
                    Label = el.Attribute("label")?.Value ?? string.Empty,
                    Comment = el.Attribute("comment")?.Value ?? string.Empty,
                    Function = el.Attribute("function")?.Value ?? string.Empty,
                    Description = el.Attribute("description")?.Value ?? string.Empty,
                    Manufacturer = el.Attribute("manufacturer")?.Value ?? string.Empty,
                    Designation = el.Attribute("designation")?.Value ?? string.Empty
                };
                var textOverrides = el.Element("textOverrides");
                if (textOverrides is not null)
                {
                    foreach (var t in textOverrides.Elements("t"))
                    {
                        if (int.TryParse(t.Attribute("i")?.Value, out var idx))
                            placed.TextOverrides[idx] = t.Value;
                    }
                }
                var linksEl = el.Element("links");
                if (linksEl is not null)
                {
                    foreach (var link in linksEl.Elements("link"))
                    {
                        if (Guid.TryParse(link.Attribute("uuid")?.Value, out var g))
                            placed.LinkedUuids.Add(g);
                    }
                }
                page.AddElement(placed);
            }
        }

        var conductors = pageEl.Element("conductors");
        if (conductors is not null)
        {
            foreach (var c in conductors.Elements("conductor"))
            {
                var cond = new Conductor
                {
                    Id = GuidOrNew(c.Attribute("id")?.Value),
                    From = new ElementTerminalRef(GuidOrNew(c.Attribute("fromEl")?.Value), Int(c.Attribute("fromTerm"), 0)),
                    To = new ElementTerminalRef(GuidOrNew(c.Attribute("toEl")?.Value), Int(c.Attribute("toTerm"), 0)),
                    Color = c.Attribute("color")?.Value ?? "black",
                    Width = Dbl(c.Attribute("width"), 1.0),
                    Text = c.Attribute("text")?.Value ?? string.Empty,
                    TextColor = c.Attribute("textColor")?.Value ?? "#000000",
                    WireType = c.Attribute("wireType")?.Value ?? string.Empty,
                    Section = c.Attribute("section")?.Value ?? string.Empty,
                    Cable = c.Attribute("cable")?.Value ?? string.Empty,
                    Bus = c.Attribute("bus")?.Value ?? string.Empty,
                    Function = c.Attribute("function")?.Value ?? string.Empty,
                    TensionProtocol = c.Attribute("tensionProtocol")?.Value ?? string.Empty,
                    NumSize = Int(c.Attribute("numSize"), 9),
                    DisplayText = Bool(c.Attribute("displayText"), false),
                    Formula = c.Attribute("formula")?.Value ?? string.Empty,
                    SequenceNumber = Int(c.Attribute("sequenceNumber"), 0),
                    Path = ConductorPathFromString(c.Attribute("path")?.Value),
                    Continuity = Bool(c.Attribute("continuity"), true),
                    PreservePath = Bool(c.Attribute("preservePath"), false)
                };
                var junctions = c.Element("junctions");
                if (junctions is not null)
                {
                    foreach (var j in junctions.Elements("junction"))
                    {
                        var termEl = j.Attribute("termEl")?.Value;
                        cond.Junctions.Add(new JunctionPoint
                        {
                            Position = new Point2D(Dbl(j.Attribute("x"), 0), Dbl(j.Attribute("y"), 0)),
                            ConductorId = GuidOrNew(j.Attribute("conductor")?.Value),
                            TerminalElementId = string.IsNullOrEmpty(termEl) ? null : GuidOrNew(termEl),
                            TerminalIndex = Int(j.Attribute("termIdx"), -1)
                        });
                    }
                }
                page.Add(cond);
            }
        }

        var texts = pageEl.Element("texts");
        if (texts is not null)
        {
            foreach (var tx in texts.Elements("text"))
            {
                page.Add(new TextItem
                {
                    Id = GuidOrNew(tx.Attribute("id")?.Value),
                    Text = tx.Attribute("text")?.Value ?? string.Empty,
                    X = Dbl(tx.Attribute("x")),
                    Y = Dbl(tx.Attribute("y")),
                    Rotation = Dbl(tx.Attribute("rotation")),
                    FontFamily = tx.Attribute("fontFamily")?.Value ?? TextItem.DefaultFontFamily,
                    FontSizePt = Dbl(tx.Attribute("fontSizePt"), TextItem.DefaultFontSizePt),
                    Bold = Bool(tx.Attribute("bold"), false),
                    Italic = Bool(tx.Attribute("italic"), false),
                    Underline = Bool(tx.Attribute("underline"), false),
                    Strikethrough = Bool(tx.Attribute("strikethrough"), false),
                    Color = tx.Attribute("color")?.Value ?? "#000000",
                    TextAlign = tx.Attribute("textalign")?.Value ?? "left",
                    ImportGroupId = GuidNullable(tx.Attribute("importGroupId")?.Value),
                    IsLocked = Bool(tx.Attribute("locked"), false),
                    LayerId = GuidNullable(tx.Attribute("layerId")?.Value)
                });
            }
        }

        var shapes = pageEl.Element("shapes");
        if (shapes is not null)
        {
            foreach (var sh in shapes.Elements("shape"))
            {
                var shape = new ShapeItem
                {
                    Id = GuidOrNew(sh.Attribute("id")?.Value),
                    Color = sh.Attribute("color")?.Value ?? "black",
                    PenWidth = Dbl(sh.Attribute("penWidth"), 1.0),
                    Filling = sh.Attribute("filling")?.Value ?? "none",
                    Closed = Bool(sh.Attribute("closed"), false),
                    ImportGroupId = GuidNullable(sh.Attribute("importGroupId")?.Value),
                    PdfObjectIndex = int.TryParse(sh.Attribute("pdfObject")?.Value, out var pdfObj) ? pdfObj : null,
                    ContourStarts = ParseContourStarts(sh.Attribute("contours")?.Value),
                    IsLocked = Bool(sh.Attribute("locked"), false),
                    LayerId = GuidNullable(sh.Attribute("layerId")?.Value)
                };
                if (Enum.TryParse<LineDashStyle>(sh.Attribute("dashStyle")?.Value, out var dashStyle))
                    shape.DashStyle = dashStyle;
                shape.DashPattern = DoubleList(sh.Attribute("dashPattern")?.Value);
                shape.DashPhase = Dbl(sh.Attribute("dashPhase"), 0);
                if (Enum.TryParse<ShapeLineCap>(sh.Attribute("lineCap")?.Value, out var lineCap))
                    shape.LineCap = lineCap;
                if (Enum.TryParse<ShapeLineJoin>(sh.Attribute("lineJoin")?.Value, out var lineJoin))
                    shape.LineJoin = lineJoin;
                if (Enum.TryParse<ShapeKind>(sh.Attribute("kind")?.Value, out var kind))
                    shape.Kind = kind;
                shape.StartAngle = Dbl(sh.Attribute("startAngle"), 0);
                shape.SweepAngle = Dbl(sh.Attribute("sweepAngle"), 90);
                var pts = sh.Element("points");
                if (pts is not null)
                {
                    foreach (var p in pts.Elements("point"))
                        shape.Points.Add(new Point2D(Dbl(p.Attribute("x")), Dbl(p.Attribute("y"))));
                }
                var cpEl = sh.Element("controlPoints");
                if (cpEl is not null)
                {
                    var controlPoints = new List<(Point2D C1, Point2D C2)>();
                    foreach (var cp in cpEl.Elements("cp"))
                    {
                        controlPoints.Add((
                            new Point2D(Dbl(cp.Attribute("c1x")), Dbl(cp.Attribute("c1y"))),
                            new Point2D(Dbl(cp.Attribute("c2x")), Dbl(cp.Attribute("c2y")))));
                    }
                    if (controlPoints.Count > 0)
                        shape.ControlPoints = controlPoints;
                }
                page.Add(shape);
            }
        }

        var images = pageEl.Element("images");
        if (images is not null)
        {
            foreach (var img in images.Elements("image"))
            {
                page.Add(new ImageItem
                {
                    Id = GuidOrNew(img.Attribute("id")?.Value),
                    FilePath = img.Attribute("filePath")?.Value ?? string.Empty,
                    X = Dbl(img.Attribute("x")),
                    Y = Dbl(img.Attribute("y")),
                    Width = Dbl(img.Attribute("width")),
                    Height = Dbl(img.Attribute("height")),
                    LayerId = GuidNullable(img.Attribute("layerId")?.Value)
                });
            }
        }

        var stripItems = pageEl.Element("terminal_strip_items");
        if (stripItems is not null)
        {
            foreach (var item in stripItems.Elements("terminal_strip_item"))
            {
                page.Add(new TerminalStripItem
                {
                    Id = GuidOrNew(item.Attribute("id")?.Value),
                    StripUuid = GuidOrNew(item.Attribute("stripUuid")?.Value),
                    X = Dbl(item.Attribute("x")),
                    Y = Dbl(item.Attribute("y")),
                    Width = Dbl(item.Attribute("width"), 44),
                    RowHeight = Dbl(item.Attribute("rowHeight"), 6)
                });
            }
        }

        page.RecomputeConductors();
        return page;
    }

    private static string Str(double v) => v.ToString(Inv);
    private static double Dbl(XAttribute? a, double def = 0) => double.TryParse(a?.Value, NumberStyles.Float, Inv, out var v) ? v : def;
    private static int Int(XAttribute? a, int def) => int.TryParse(a?.Value, NumberStyles.Integer, Inv, out var v) ? v : def;
    private static bool Bool(XAttribute? a, bool def) => a is null ? def : a.Value == "true";
    private static Guid GuidOrNew(string? s) => Guid.TryParse(s, out var g) ? g : Guid.NewGuid();
    private static Guid? GuidNullable(string? s) => Guid.TryParse(s, out var g) ? g : null;
    private static List<double> DoubleList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new List<double>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => double.TryParse(token, NumberStyles.Float, Inv, out var number) ? number : double.NaN)
                .Where(number => !double.IsNaN(number) && number > 0)
                .ToList();

    /// <summary>Serializa os pontos do caminho de um condutor como "x1,y1;x2,y2;…".</summary>
    private static string ConductorPathToString(IReadOnlyList<Point2D> path)
    {
        if (path is null || path.Count == 0)
            return string.Empty;
        return string.Join(";", path.Select(p => $"{Str(p.X)},{Str(p.Y)}"));
    }

    /// <summary>Lê os pontos do caminho de um condutor a partir de "x1,y1;x2,y2;…".</summary>
    private static List<Point2D> ConductorPathFromString(string? s)
    {
        var result = new List<Point2D>();
        if (string.IsNullOrWhiteSpace(s))
            return result;
        foreach (var token in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(',');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, Inv, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, Inv, out var y))
                result.Add(new Point2D(x, y));
        }
        return result;
    }
}
