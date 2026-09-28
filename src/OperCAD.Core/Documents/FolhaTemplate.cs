using System.Text.Json.Serialization;

namespace OperCAD.Core.Documents;

/// <summary>
/// Template de moldura da folha (dirigido por dados, ex.: <c>template.json</c>).
/// Descreve a moldura técnica completa de uma folha: moldura externa com margem de
/// encadernação, faixa superior de colunas (números), faixas laterais de linhas
/// (letras A–F em ambos os lados), área de desenho e um bloco de legenda inferior
/// com células (rótulo/valor). Todas as coordenadas são frações da largura/altura
/// da área da moldura (0..1), para escalar em qualquer tamanho de papel.
/// </summary>
public sealed class FolhaTemplate
{
    /// <summary>Nome amigável do template (ex.: "Padrão ABNT (OBRA/CAD)").</summary>
    public string Name { get; set; } = "Moldura padrão";

    /// <summary>Nome do arquivo de origem (chave para persistência).</summary>
    public string FileName { get; set; } = "template.json";

    /// <summary>Tamanho de papel de referência (o template pode ser usado em outros).</summary>
    public PaperSize Size { get; set; } = PaperSize.A1;

    /// <summary>Dimensões personalizadas (usadas quando <see cref="Size"/> é Custom).</summary>
    public double CustomWidth { get; set; } = 1070;
    public double CustomHeight { get; set; } = 690;

    /// <summary>Recuo da moldura externa em relação à borda da folha (mm).</summary>
    public double FrameInset { get; set; } = 5;

    /// <summary>Posições (fração da largura) das linhas verticais da margem de encadernação.</summary>
    public List<double> BindingLines { get; } = new();

    /// <summary>Quantidade de colunas do quadro (faixa superior).</summary>
    public int ColumnsCount { get; set; } = 8;

    /// <summary>Altura da faixa superior (fração da altura da moldura).</summary>
    public double ColumnStripHeight { get; set; } = 0.0176;

    /// <summary>Quantidade de linhas do quadro (faixas laterais).</summary>
    public int RowsCount { get; set; } = 6;

    /// <summary>Largura da faixa lateral esquerda (fração da largura da moldura).</summary>
    public double RowStripWidth { get; set; } = 0.0115;

    /// <summary>Mostrar também a faixa lateral direita (letras).</summary>
    public bool ShowRightStrip { get; set; } = true;

    /// <summary>Início vertical da faixa de linhas (fração da altura; após a faixa superior).</summary>
    public double RowsStripStart { get; set; } = 0.0176;

    /// <summary>Fim vertical da faixa de linhas (fração da altura; antes da legenda).</summary>
    public double RowsStripEnd { get; set; } = 0.9167;

    /// <summary>Posição superior do bloco de legenda (fração da altura da moldura).</summary>
    public double TitleBlockTop { get; set; } = 0.9167;

    /// <summary>Posição inferior do bloco de legenda (fração da altura; 1 = fundo da moldura).</summary>
    public double TitleBlockBottom { get; set; } = 1.0;

    /// <summary>Posição esquerda do bloco de legenda (fração da largura).</summary>
    public double TitleBlockLeft { get; set; } = 0.0366;

    /// <summary>Células do bloco de legenda.</summary>
    public List<FolhaTemplateCell> TitleBlockCells { get; } = new();

    public override string ToString() => Name;
}

/// <summary>Uma célula retangular do bloco de legenda da moldura.</summary>
public sealed class FolhaTemplateCell
{
    /// <summary>Posição X (fração da largura da moldura).</summary>
    public double X { get; set; }

    /// <summary>Posição Y (fração da altura da moldura).</summary>
    public double Y { get; set; }

    /// <summary>Largura (fração da largura da moldura).</summary>
    public double W { get; set; }

    /// <summary>Altura (fração da altura da moldura).</summary>
    public double H { get; set; }

    /// <summary>Rótulo exibido na célula (ex.: "AUTOR", "ESCALA").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Campo da legenda cujo valor é exibido (author, title, company, drawingnumber,
    /// revision, scale, sheet, totalsheets, date...). Vazio = só rótulo.
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Alinhamento horizontal: left | center | right.</summary>
    public string Align { get; set; } = "left";

    /// <summary>Alinhamento vertical: top | center | bottom.</summary>
    public string Valign { get; set; } = "center";

    /// <summary>Desenhar o símbolo de norte (bússola) nesta célula.</summary>
    [JsonIgnore]
    public bool IsNorth { get; set; }

    /// <summary>Exibir o rótulo em maiúsculas pequenas (estilo de desenho técnico).</summary>
    public bool SmallCaps { get; set; }
}
