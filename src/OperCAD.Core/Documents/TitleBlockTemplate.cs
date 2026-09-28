namespace OperCAD.Core.Documents;

/// <summary>Onde o legenda (legenda) é exibido na folha (como o padrão CAD: embaixo ou à direita).</summary>
public enum TitleBlockPosition { Bottom, Right }

/// <summary>Tipo de dimensão de uma coluna do template (como dimensão do template do padrão CAD).</summary>
public enum TitleBlockColumnLength { Absolute, RelativeToTotal, RelativeToRemaining }

/// <summary>
/// Célula de um template de legenda (como célula do template do padrão CAD): um retângulo
/// da grade que exibe um rótulo e/ou um valor (com variáveis %title, %author...).
/// </summary>
public sealed class TitleBlockCell
{
    public int Row { get; set; }
    public int Col { get; set; }

    /// <summary>Número de LINHAS EXTRA ocupadas (0 = apenas a própria).</summary>
    public int RowSpan { get; set; }

    /// <summary>Número de COLUNAS EXTRA ocupadas (0 = apenas a própria).</summary>
    public int ColSpan { get; set; }

    /// <summary>Nome do campo (author, title, date, folio...).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Texto do valor (pode conter variáveis %autor, %title...).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Rótulo exibido (traduzido pt_BR/pt/en).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Exibir o rótulo antes do valor.</summary>
    public bool DisplayLabel { get; set; } = true;

    /// <summary>Alinhamento horizontal: left | center | right.</summary>
    public string Align { get; set; } = "center";

    /// <summary>Alinhamento vertical: top | center | bottom.</summary>
    public string Valign { get; set; } = "center";

    /// <summary>Reduzir a fonte se o texto não couber na célula.</summary>
    public bool Hadjust { get; set; }

    /// <summary>Tamanho da fonte em pontos (0 = padrão).</summary>
    public int FontSize { get; set; }
}

/// <summary>
/// Template de legenda do padrão CAD (.titleblock): grade de linhas/colunas
/// e células com rótulos/valores. Fonte: biblioteca embutida no OperCAD
/// (pasta "titleblocks" ao lado do executável; cópia integral da do padrão CAD).
/// </summary>
public sealed class TitleBlockTemplate
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Nome do arquivo .titleblock (chave para persistência).</summary>
    public string FileName { get; set; } = string.Empty;

    public string Information { get; set; } = string.Empty;

    /// <summary>Alturas das linhas, em unidades padrão CAD (= mm no mundo do OperCAD).</summary>
    public List<int> RowHeights { get; } = new();

    /// <summary>Larguras das colunas (absolutas em px/mm ou percentuais).</summary>
    public List<(TitleBlockColumnLength Type, int Value)> ColumnWidths { get; } = new();

    /// <summary>Células não-vazias do template.</summary>
    public List<TitleBlockCell> Cells { get; } = new();

    /// <summary>XML bruto do bloco &lt;logos&gt; (preservado na re-escrita do template).</summary>
    public string RawLogosXml { get; set; } = string.Empty;

    public int Height => RowHeights.Sum();

    public override string ToString() => Name;
}

/// <summary>Logo inserido em uma célula da legenda.</summary>
public sealed class TitleBlockLogo
{
    /// <summary>Índice da célula onde o logo é inserido.</summary>
    public int CellIndex { get; set; }

    /// <summary>Dados binários da imagem do logo.</summary>
    public byte[] ImageData { get; set; } = Array.Empty<byte>();

    /// <summary>Nome do arquivo original do logo.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Posição X do logo dentro da célula (mm).</summary>
    public double X { get; set; }

    /// <summary>Posição Y do logo dentro da célula (mm).</summary>
    public double Y { get; set; }

    /// <summary>Largura do logo (mm, 0 = automático).</summary>
    public double Width { get; set; }

    /// <summary>Altura do logo (mm, 0 = automático).</summary>
    public double Height { get; set; }
}
