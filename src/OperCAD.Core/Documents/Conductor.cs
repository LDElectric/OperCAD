using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Condutor (ligação elétrica/fluida) entre dois terminais de elementos.
/// O caminho (Path) é gerado pelo roteador ortogonal (padrão padrão CAD).
/// </summary>
public sealed class Conductor : DocumentObject
{
    public ElementTerminalRef From { get; set; } = null!;
    public ElementTerminalRef To { get; set; } = null!;

    /// <summary>Pontos do caminho em mm (inclui os dois terminais).</summary>
    public List<Point2D> Path { get; set; } = new();

    public string Color { get; set; } = "black";
    public double Width { get; set; } = 0.29;

    /// <summary>Número/etiqueta exibido(a) no condutor (autonumeração, como o "num" do padrão CAD).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Número sequencial usado pela autonumeração.</summary>
    public int SequenceNumber { get; set; }

    /// <summary>Número do cabo (propriedade "cable" do padrão CAD).</summary>
    public string Cable { get; set; } = string.Empty;

    /// <summary>Função do condutor (propriedade "function" do padrão CAD).</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>Barramento (propriedade "bus" do padrão CAD).</summary>
    public string Bus { get; set; } = string.Empty;

    /// <summary>Protocolo de tensão (propriedade "tension_protocol" do padrão CAD).</summary>
    public string TensionProtocol { get; set; } = string.Empty;

    /// <summary>Seção do condutor (propriedade "conductor_section" do padrão CAD).</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Cor do texto/número (propriedade "text_color" do padrão CAD).</summary>
    public string TextColor { get; set; } = "#000000";

    /// <summary>Tamanho do número (propriedade "numsize" do padrão CAD, em pt).</summary>
    public int NumSize { get; set; } = 9;

    /// <summary>Exibir o texto no condutor (propriedade "displaytext" do padrão CAD).</summary>
    public bool DisplayText { get; set; } = false;

    /// <summary>Fórmula de autonumeração (propriedade "formula" do padrão CAD).</summary>
    public string Formula { get; set; } = string.Empty;

    /// <summary>Tipo de fio (propriedade "wire_type" do padrão CAD): flex, rigid, stranded, coaxial, fiber.</summary>
    public string WireType { get; set; } = string.Empty;

    /// <summary>
    /// Continuidade elétrica explícita do condutor. Um condutor com vários segmentos
    /// continua sendo eletricamente UMA única conexão (A─B). True = conduz eletricamente.
    /// Base para futuros testes de continuidade, detecção de circuito aberto/curto,
    /// propagação de estados e simulação (não implementados agora).
    /// </summary>
    public bool Continuity { get; set; } = true;

    /// <summary>
    /// Preserva a rota desenhada à mão (não recalcula ao mover elementos). Condutores
    /// criados pela ferramenta de desenho livre definem a rota explicitamente; o roteador
    /// automático só relayout condutores sem esta marca.
    /// </summary>
    public bool PreservePath { get; set; }

    /// <summary>
    /// ID do elemento da biblioteca que originou este condutor (quando criado a partir
    /// de um elemento .opcad com IsConductor=true). Usado para determinar a espessura
    /// visual correta (fina, como a linha original do elemento) em vez da espessura
    /// grossa padrão dos condutores da ferramenta especial.
    /// </summary>
    public Guid? SourceElementId { get; set; }

    /// <summary>
    /// Junções topológicas explícitas: pontos deste condutor que formam um nó elétrico
    /// com outros condutores. O simples cruzamento geométrico de dois <see cref="Path"/>s
    /// NÃO cria continuidade; uma junção precisa ser registrada aqui.
    /// </summary>
    public List<JunctionPoint> Junctions { get; set; } = new();

    public Conductor()
    {
        Name = "Condutor";
    }

    /// <summary>
    /// Remove pontos colineares redundantes do Path, simplificando a geometria
    /// sem alterar a rota visual. Mantém no mínimo 2 pontos (início e fim).
    /// </summary>
    public void SimplifyPath()
    {
        if (Path.Count < 3)
            return;

        var simplified = new List<Point2D> { Path[0] };
        for (var i = 1; i < Path.Count - 1; i++)
        {
            var prev = Path[i - 1];
            var curr = Path[i];
            var next = Path[i + 1];

            var d1x = curr.X - prev.X;
            var d1y = curr.Y - prev.Y;
            var d2x = next.X - curr.X;
            var d2y = next.Y - curr.Y;

            // Se o produto vetorial for ~= 0, os três pontos são colineares
            if (Math.Abs(d1x * d2y - d1y * d2x) > 1e-6)
                simplified.Add(curr);
        }
        simplified.Add(Path[^1]);

        if (simplified.Count < Path.Count)
        {
            Path.Clear();
            Path.AddRange(simplified);
        }
    }

    /// <summary>
    /// Retorna a espessura visual apropriada para este condutor.
    /// Usa 0.29mm (line-weight:normal) — mesmo padrão dos elementos legados.
    /// </summary>
    public double GetVisualWidth()
    {
        return 0.29;
    }
}

/// <summary>
/// Junção elétrica explícita em um ponto do condutor. Representa a relação topológica
/// que conecta eletricamente este condutor a outro (ou a um terminal de elemento)
/// naquele ponto. Um cruzamento geométrico sem junção não cria continuidade.
/// </summary>
public sealed class JunctionPoint
{
    /// <summary>Posição da junção no mundo (mm).</summary>
    public Point2D Position { get; set; }

    /// <summary>Id do outro condutor conectado nesta junção (ou <see cref="Guid.Empty"/> se liga a um terminal).</summary>
    public Guid ConductorId { get; set; }

    /// <summary>Elemento do terminal ligado nesta junção (ou null se liga a outro condutor).</summary>
    public Guid? TerminalElementId { get; set; }

    /// <summary>Índice do terminal ligado (ou -1).</summary>
    public int TerminalIndex { get; set; } = -1;
}
