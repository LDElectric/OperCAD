namespace OperCAD.Core.Documents;

/// <summary>
/// Texto independente da folha (análogo ao texto independente do padrão CAD).
/// A posição (X, Y) é o canto inferior esquerdo do texto, em mm do mundo.
/// </summary>
public sealed class TextItem : DocumentObject
{
    /// <summary>
    /// Família padrão dos textos do OperCAD: grafia técnica sem serifa, igual à das
    /// etiquetas vetorizadas dos PDFs importados.
    /// </summary>
    public const string DefaultFontFamily = "Arial";

    /// <summary>
    /// Corpo padrão em pontos. Calibrado pelas etiquetas dos PDFs de projeto: as letras
    /// medem ≈1,2–1,3 mm de altura de maiúscula, o que dá ≈5 pt com Arial
    /// (ver <c>FontePadraoTests</c> e o diagnóstico <c>diag_fonte_texto.txt</c>).
    /// </summary>
    public const double DefaultFontSizePt = 5.0;

    /// <summary>Menor e maior corpo aceito na edição de textos.</summary>
    public const double MinFontSizePt = 1.0;
    public const double MaxFontSizePt = 200.0;

    public string Text { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Rotação em graus (sentido anti-horário no mundo).</summary>
    public double Rotation { get; set; }

    public string FontFamily { get; set; } = DefaultFontFamily;

    /// <summary>Tamanho da fonte em pontos tipográficos.</summary>
    public double FontSizePt { get; set; } = DefaultFontSizePt;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool Strikethrough { get; set; }

    public string Color { get; set; } = "#000000";

    /// <summary>Alinhamento horizontal do texto ("left", "center", "right").</summary>
    public string TextAlign { get; set; } = "left";

    public Guid? ImportGroupId { get; set; }
    public bool IsLocked { get; set; }

    /// <summary>
    /// MUDANÇA DE ESCALA do texto (Ctrl++/Ctrl+-): multiplica o corpo da fonte mantendo o ponto
    /// de ancoragem (X, Y) — o texto cresce/encolhe "no lugar", sem sair do alinhamento.
    /// </summary>
    /// <returns>true se o corpo mudou (respeita <see cref="MinFontSizePt"/> e <see cref="MaxFontSizePt"/>).</returns>
    public bool ScaleFont(double factor)
    {
        if (factor <= 0 || Math.Abs(factor - 1.0) < 1e-9)
            return false;
        var size = Math.Clamp(FontSizePt * factor, MinFontSizePt, MaxFontSizePt);
        if (Math.Abs(size - FontSizePt) < 1e-9)
            return false;
        FontSizePt = size;
        return true;
    }
}
