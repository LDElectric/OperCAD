namespace OperCAD.Drawing;

/// <summary>
/// Regra do preenchimento das formas desenhadas pelas ferramentas de forma.
///
/// Requisito do usuário: círculos, retângulos, elipses e demais formas geométricas
/// fechadas são <b>VAZADAS por padrão</b>; o painel oferece a opção "Preenchido (sólido)"
/// para preenchê-las com a cor escolhida.
///
/// O modo da ferramenta é independente do preenchimento da forma SELECIONADA — inspecionar
/// um símbolo preenchido importado do PDF não pode fazer as próximas formas nascerem
/// preenchidas.
/// </summary>
public static class DrawingFillRules
{
    /// <summary>Valor de <c>ShapeItem.Filling</c> que significa "vazado" (sem preenchimento).</summary>
    public const string Hollow = "none";

    /// <summary>
    /// Preenchimento a aplicar em uma forma NOVA.
    /// </summary>
    /// <param name="toolFilled">A ferramenta está no modo "Preenchido (sólido)"?</param>
    /// <param name="fillColor">Cor escolhida no painel para o preenchimento.</param>
    public static string FillingForNewShapes(bool toolFilled, string? fillColor)
        => toolFilled && !string.IsNullOrWhiteSpace(fillColor) ? fillColor! : Hollow;
}
