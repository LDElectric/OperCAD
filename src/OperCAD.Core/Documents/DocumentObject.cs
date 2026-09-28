namespace OperCAD.Core.Documents;

/// <summary>Objeto que pode ser desenhado em uma folha (símbolo, condutor, forma...).</summary>
public abstract class DocumentObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Nome (identificação) do objeto, ex.: "M1", "K1".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Estado transitório de seleção na folha (não persistido).</summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// ID da camada à qual este objeto pertence. Null = camada padrão (sempre visível/desbloqueada).
    /// Usado para controle de visibilidade e bloqueio por camada.
    /// </summary>
    public Guid? LayerId { get; set; }
}
