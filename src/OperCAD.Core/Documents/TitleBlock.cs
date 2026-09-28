namespace OperCAD.Core.Documents;

/// <summary>Dados da legenda (quadro de identificação) da folha.</summary>
public sealed class TitleBlock
{
    public string Company { get; set; } = string.Empty;
    public string Obra { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DrawingNumber { get; set; } = string.Empty;
    public string Revision { get; set; } = "0";
    public string Scale { get; set; } = "1:1";
    public string Sheet { get; set; } = "1";
    public string TotalSheets { get; set; } = "1";
    /// <summary>Quando true, o número da página acompanha a posição da folha no projeto.</summary>
    public bool SheetAutoNumbered { get; set; } = true;
    /// <summary>Quando true, o total acompanha a quantidade de folhas do projeto.</summary>
    public bool TotalSheetsAutoNumbered { get; set; } = true;
    public string Author { get; set; } = string.Empty;
    public string ReviewedBy { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;

    /// <summary>Estilos customizados aplicados individualmente a cada campo da legenda.</summary>
    public Dictionary<string, TitleBlockFieldStyle> FieldStyles { get; set; } = new();

    /// <summary>Rótulos customizados por página (ex.: "Empresa" → "Cliente"). Chave = nome do campo (ex.: "company").</summary>
    public Dictionary<string, string> FieldLabels { get; set; } = new();

    // Propriedades para rótulos editáveis (facilita binding no XAML)
    public string AuthorLabel { get => GetLabel("author", "Autor"); set => SetLabel("author", value); }
    public string ReviewedByLabel { get => GetLabel("revpor", "REV. POR"); set => SetLabel("revpor", value); }
    public string ApprovedByLabel { get => GetLabel("aprovpor", "APROV. POR"); set => SetLabel("aprovpor", value); }
    public string ScaleLabel { get => GetLabel("scale", "ESCALA"); set => SetLabel("scale", value); }
    public string CompanyLabel { get => GetLabel("company", "EMPREENDIMENTO"); set => SetLabel("company", value); }
    public string DescriptionLabel { get => GetLabel("title", "DESCRIÇÃO DO DESENHO"); set => SetLabel("title", value); }
    public string DrawingNumberLabel { get => GetLabel("drawingnumber", "REGISTRO - N° DO DESENHO"); set => SetLabel("drawingnumber", value); }
    public string RevisionLabel { get => GetLabel("revision", "REV"); set => SetLabel("revision", value); }
    public string SheetLabel { get => GetLabel("sheet", "PÁGINA"); set => SetLabel("sheet", value); }
    public string TotalSheetsLabel { get => GetLabel("totalsheets", "DE TOTAL DE PÁGINAS"); set => SetLabel("totalsheets", value); }

    private string GetLabel(string field, string defaultValue)
        => FieldLabels.TryGetValue(field, out var label) && !string.IsNullOrWhiteSpace(label) ? label : defaultValue;

    private void SetLabel(string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            FieldLabels.Remove(field);
        else
            FieldLabels[field] = value;
    }
}

public sealed class TitleBlockFieldStyle
{
    public double? FontSizePt { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public string? FontFamily { get; set; }
}
