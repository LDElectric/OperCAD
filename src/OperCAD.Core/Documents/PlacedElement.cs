using OperCAD.Core.Elements;

namespace OperCAD.Core.Documents;

/// <summary>Instância de um elemento posicionado em uma folha (referencia a definição da biblioteca).</summary>
public sealed class PlacedElement : DocumentObject
{
    public ElementDefinition Definition { get; }

    /// <summary>Posição X do ponto de inserção (hotspot) em mm.</summary>
    public double X { get; set; }

    /// <summary>Posição Y do ponto de inserção (hotspot) em mm.</summary>
    public double Y { get; set; }

    /// <summary>Rotação em graus (0, 90, 180, 270).</summary>
    public double Rotation { get; set; }

    /// <summary>Escala de inserção desta instância (zoom CAD). 1.0 = tamanho natural
    /// (na grade); outros valores redimensionam este elemento individualmente, como a
    /// escala de inserção de um bloco em CAD. Não afeta a grade nem os demais elementos.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Menor escala de inserção aceita (evita símbolo degenerado/impressionável).</summary>
    public const double MinInsertionScale = 0.1;

    /// <summary>Maior escala de inserção aceita (evita símbolo gigante fora da folha).</summary>
    public const double MaxInsertionScale = 20.0;

    /// <summary>
    /// MUDANÇA DE ESCALA do símbolo (SCALE do CAD): multiplica a escala de inserção mantendo o
    /// hotspot (X, Y) exatamente onde está — nada de re-snap no grid, o desenho não "anda".
    /// A geometria E o traçado das primitivas crescem juntos, pois a escala entra na matriz de
    /// desenho (ver <c>ElementRenderer.BuildMatrix</c>).
    /// </summary>
    /// <returns>true se a escala mudou (respeita Min/MaxInsertionScale).</returns>
    public bool ScaleBy(double factor)
    {
        if (factor <= 0 || Math.Abs(factor - 1.0) < 1e-9)
            return false;
        var scale = Math.Clamp(Scale * factor, MinInsertionScale, MaxInsertionScale);
        if (Math.Abs(scale - Scale) < 1e-9)
            return false;
        Scale = scale;
        return true;
    }

    /// <summary>Etiqueta do elemento (ex.: "M1", "K1").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Comentário/explicação do elemento.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Função do elemento (campo de informação padrão CAD).</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>Descrição do elemento (campo de informação padrão CAD).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Fabricante (campo de informação padrão CAD).</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>Designação/artigo (campo de informação padrão CAD).</summary>
    public string Designation { get; set; } = string.Empty;

    /// <summary>Indica se o rótulo (etiqueta) está congelado — não será alterado pela renumeração.</summary>
    public bool IsLabelFrozen { get; set; }

    /// <summary>Indica se o elemento está visível na folha.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Ids (Uuids) dos elementos vinculados a este — referência cruzada
    /// mestre↔escravo estilo padrão CAD (ex.: bobina K1 ↔ contatos K2, K3).
    /// O vínculo é sempre bidirecional.
    /// </summary>
    public List<Guid> LinkedUuids { get; set; } = new();

    /// <summary>Valores dos campos de texto dinâmicos deste elemento.</summary>
    public Dictionary<string, string> DynamicTextValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sobrescritas de texto por índice de primitiva (textos fixos e dinâmicos "UserText")
    /// aplicadas apenas NESTA instância. Não altera a definição (<see cref="Definition"/>),
    /// então outros elementos que usam o mesmo símbolo permanecem com o texto padrão.
    /// </summary>
    public Dictionary<int, string> TextOverrides { get; } = new();

    /// <summary>Obtém a sobrescrita de texto da primitiva no índice, ou null se não houver.</summary>
    public string? GetTextOverride(int primitiveIndex)
        => TextOverrides.TryGetValue(primitiveIndex, out var value) ? value : null;

    /// <summary>
    /// Define a sobrescrita de texto da primitiva no índice. Um valor vazio oculta o
    /// texto nesta instância (sem afetar a definição do elemento).
    /// </summary>
    public void SetTextOverride(int primitiveIndex, string value)
        => TextOverrides[primitiveIndex] = value ?? string.Empty;

    /// <summary>Remove a sobrescrita — volta ao texto padrão da definição.</summary>
    public void ClearTextOverride(int primitiveIndex)
        => TextOverrides.Remove(primitiveIndex);

    public PlacedElement(ElementDefinition definition)
    {
        Definition = definition;
        Name = definition.Name;
    }

    /// <summary>Vincula dois elementos (referência cruzada bidirecional).</summary>
    public void LinkTo(PlacedElement other)
    {
        if (other == this)
            return;
        if (!LinkedUuids.Contains(other.Id))
            LinkedUuids.Add(other.Id);
        if (!other.LinkedUuids.Contains(Id))
            other.LinkedUuids.Add(Id);
    }

    /// <summary>Desvincula dois elementos (remove o vínculo bidirecional).</summary>
    public void UnlinkFrom(PlacedElement other)
    {
        LinkedUuids.Remove(other.Id);
        other.LinkedUuids.Remove(Id);
    }

    /// <summary>Indica se o elemento é do tipo mestre (ex.: bobina).</summary>
    public bool IsMaster => Definition.LinkType == "master";

    /// <summary>Indica se o elemento é do tipo escravo (ex.: contato).</summary>
    public bool IsSlave => Definition.LinkType == "slave";

    /// <summary>Indica se participa da referência cruzada (mestre ou escravo).</summary>
    public bool IsReport => Definition.LinkType is "master" or "slave";
}
