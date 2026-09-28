namespace OperCAD.Core.Documents;

/// <summary>
/// Imagem inserida na folha (análoga ao imagem do esquema do padrão CAD).
/// Referencia um arquivo no disco e é desenhada no retângulo (X, Y)-(X+W, Y+H) em mm.
/// </summary>
public sealed class ImageItem : DocumentObject
{
    /// <summary>Caminho do arquivo de imagem (png/jpg/bmp...).</summary>
    public string FilePath { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
