namespace OperCAD.Core.Documents;

/// <summary>Campo de texto dinâmico dentro de um elemento.</summary>
public sealed class DynamicTextField
{
    /// <summary>Nome do campo (ex.: "Potência", "Tensão").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Valor do campo (preenchido pelo usuário).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Posição X local em relação ao hotspot do elemento.</summary>
    public double X { get; set; }

    /// <summary>Posição Y local em relação ao hotspot do elemento.</summary>
    public double Y { get; set; }

    /// <summary>Família da fonte.</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>Tamanho da fonte em pontos.</summary>
    public double FontSize { get; set; } = 3.5;

    /// <summary>Se o campo é visível na folha.</summary>
    public bool IsVisible { get; set; } = true;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool Strikethrough { get; set; }

    /// <summary>Cópia profunda do campo.</summary>
    public DynamicTextField DeepCopy() => new()
    {
        Name = Name,
        Value = Value,
        X = X,
        Y = Y,
        FontFamily = FontFamily,
        FontSize = FontSize,
        IsVisible = IsVisible,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strikethrough = Strikethrough
    };
}
