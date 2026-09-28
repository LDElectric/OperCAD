using System.Text.Json;
using System.Text.Json.Serialization;
using OperCAD.Core.Documents;

namespace OperCAD.Application;

/// <summary>
/// Carrega templates de moldura de folha a partir de um JSON (ex.: <c>template.json</c>),
/// com o formato dirigido por dados do OperCAD: geometria da moldura (margem de
/// encadernação, faixas de colunas/linhas) e células do bloco de legenda.
/// </summary>
public static class FolhaTemplateLoader
{
    private sealed class Dto
    {
        public int Version { get; set; }
        public string Name { get; set; } = "Moldura padrão";
        public string FileName { get; set; } = "template.json";
        public string Size { get; set; } = "A1";
        public double CustomWidth { get; set; } = 1070;
        public double CustomHeight { get; set; } = 690;
        public double FrameInset { get; set; } = 5;
        public List<double>? BindingLines { get; set; }
        public int ColumnsCount { get; set; } = 8;
        public double ColumnStripHeight { get; set; } = 0.0176;
        public int RowsCount { get; set; } = 6;
        public double RowStripWidth { get; set; } = 0.0115;
        public bool ShowRightStrip { get; set; } = true;
        public double RowsStripStart { get; set; } = 0.0176;
        public double RowsStripEnd { get; set; } = 0.9167;
        public double TitleBlockTop { get; set; } = 0.9167;
        public double TitleBlockBottom { get; set; } = 1.0;
        public double TitleBlockLeft { get; set; } = 0.0366;
        public List<CellDto>? TitleBlockCells { get; set; }
    }

    private sealed class CellDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Align { get; set; } = "left";
        public string Valign { get; set; } = "center";
        public bool IsNorth { get; set; }
    }

    /// <summary>Interpreta uma string de PaperSize ("A1", "A4"...).</summary>
    public static PaperSize ParseSize(string? size) => (size ?? "A1").ToUpperInvariant() switch
    {
        "A4" => PaperSize.A4,
        "A3" => PaperSize.A3,
        "A2" => PaperSize.A2,
        "A0" => PaperSize.A0,
        "CUSTOM" => PaperSize.Custom,
        _ => PaperSize.A1
    };

    /// <summary>Lê um template de moldura de um arquivo JSON.</summary>
    public static FolhaTemplate? ParseFile(string path)
    {
        if (!System.IO.File.Exists(path))
            return null;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            return Parse(json, System.IO.Path.GetFileName(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lê um template de moldura de uma string JSON.</summary>
    public static FolhaTemplate? Parse(string json, string fileName = "template.json")
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var dto = JsonSerializer.Deserialize<Dto>(json, options);
            if (dto is null)
                return null;

            var tpl = new FolhaTemplate
            {
                Name = dto.Name,
                FileName = string.IsNullOrWhiteSpace(dto.FileName) ? fileName : dto.FileName,
                Size = ParseSize(dto.Size),
                CustomWidth = dto.CustomWidth,
                CustomHeight = dto.CustomHeight,
                FrameInset = dto.FrameInset,
                ColumnsCount = Math.Max(1, dto.ColumnsCount),
                ColumnStripHeight = Math.Max(0.005, dto.ColumnStripHeight),
                RowsCount = Math.Max(1, dto.RowsCount),
                RowStripWidth = Math.Max(0.005, dto.RowStripWidth),
                ShowRightStrip = dto.ShowRightStrip,
                RowsStripStart = dto.RowsStripStart,
                RowsStripEnd = dto.RowsStripEnd,
                TitleBlockTop = dto.TitleBlockTop,
                TitleBlockBottom = dto.TitleBlockBottom,
                TitleBlockLeft = dto.TitleBlockLeft
            };

            if (dto.BindingLines is not null)
                foreach (var b in dto.BindingLines)
                    tpl.BindingLines.Add(b);

            if (dto.TitleBlockCells is not null)
            {
                foreach (var c in dto.TitleBlockCells)
                {
                    tpl.TitleBlockCells.Add(new FolhaTemplateCell
                    {
                        X = c.X,
                        Y = c.Y,
                        W = c.W,
                        H = c.H,
                        Label = c.Label ?? string.Empty,
                        Field = c.Field ?? string.Empty,
                        Align = c.Align ?? "left",
                        Valign = c.Valign ?? "center",
                        IsNorth = c.IsNorth
                    });
                }
            }

            return tpl;
        }
        catch
        {
            return null;
        }
    }
}
