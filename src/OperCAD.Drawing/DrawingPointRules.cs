using OperCAD.Core.Geometry;

namespace OperCAD.Drawing;

/// <summary>
/// Regras PURAS do ponto de desenho das ferramentas de forma (linha, retângulo, elipse,
/// polilinha, arco). Ficam aqui — e não dentro do controle — para serem a mesma regra no
/// clique e na pré-visualização e para poderem ser testadas.
///
/// Ordem de prioridade:
///   1. <b>snap a objeto</b> (vértice, meio de linha, centro de elipse/retângulo/arco) —
///      é o que permite começar e terminar exatamente sobre outra linha;
///   2. <b>ferramenta LINHA</b>: sem objeto próximo, o ponto é exatamente o centro do
///      crosshair (sem deslocar para a grade) — permite puxar uma linha precisamente em
///      cima de outra;
///   3. demais ferramentas: seguem a grade;
///   4. a <b>restrição angular</b> (passo de 5°) só se aplica quando NÃO houve snap, senão
///      ela deslocaria o ponto engatado e perderia a precisão.
/// </summary>
public static class DrawingPointRules
{
    /// <summary>Passo da restrição angular das ferramentas de desenho, em graus.</summary>
    public const double AngleStepDegrees = 5.0;

    /// <summary>
    /// Decide o ponto da ferramenta de desenho para uma posição de cursor.
    /// </summary>
    /// <param name="cursor">Centro do crosshair (posição crua do cursor no mundo).</param>
    /// <param name="objectSnap">Ponto de snap a objeto existente, se houver.</param>
    /// <param name="gridSnap">Cursor já alinhado à grade (usado pelas formas fechadas).</param>
    /// <param name="lineTool">Verdadeiro quando a ferramenta ativa é a LINHA.</param>
    /// <param name="applyAngular">Aplicar a restrição angular em relação ao último ponto?</param>
    /// <param name="lastPoint">Último ponto colocado da ferramenta (origem do ângulo).</param>
    public static (Point2D Point, bool Snapped) Resolve(
        Point2D cursor,
        Point2D? objectSnap,
        Point2D gridSnap,
        bool lineTool,
        bool applyAngular,
        Point2D lastPoint)
    {
        if (objectSnap.HasValue)
            return (objectSnap.Value, true);

        if (lineTool)
        {
            var p = applyAngular ? SnapAngle(lastPoint, cursor, AngleStepDegrees) : cursor;
            return (p, false);
        }

        return (gridSnap, false);
    }

    /// <summary>
    /// Alinha <paramref name="current"/> a um múltiplo de <paramref name="stepDegrees"/> em
    /// relação a <paramref name="origin"/>, mantendo a distância (origem → cursor).
    /// </summary>
    public static Point2D SnapAngle(Point2D origin, Point2D current, double stepDegrees)
    {
        var dx = current.X - origin.X;
        var dy = current.Y - origin.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 0.01 || stepDegrees <= 0)
            return current;

        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        var snapped = Math.Round(angle / stepDegrees) * stepDegrees;
        var rad = snapped * Math.PI / 180.0;
        return new Point2D(origin.X + dist * Math.Cos(rad), origin.Y + dist * Math.Sin(rad));
    }
}
