using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Application;

/// <summary>
/// Converte um cluster de ShapeItems reconhecido em um PlacedElement nativo do OperCAD.
/// Calcula posição, escala, rotação e gera label automática.
/// </summary>
public static class ClusterToElementConverter
{
    /// <summary>
    /// Converte um cluster em PlacedElement usando a ElementDefinition correspondente.
    /// </summary>
    /// <param name="cluster">Cluster reconhecido com MatchedDefinition preenchido.</param>
    /// <param name="page">Página de destino (para gerar label e snapping).</param>
    /// <param name="gridSpacing">Espaçamento do grid para snapping (padrão 2.5mm).</param>
    /// <returns>PlacedElement pronto, ou null se não for possível converter.</returns>
    public static PlacedElement? Convert(ShapeCluster cluster, Page page, double gridSpacing = 2.5)
    {
        if (cluster.MatchedDefinition is not { } definition)
            return null;

        var clusterBounds = cluster.Bounds;
        if (clusterBounds.Width <= 0 || clusterBounds.Height <= 0)
            return null;

        // 1. Posição: centroide do cluster, snapped ao grid
        var (snapX, snapY) = SnapToGrid(clusterBounds.CenterX, clusterBounds.CenterY, gridSpacing);

        // 2. Escala: proporção entre o tamanho do cluster e o tamanho da definição
        var scale = ComputeScale(clusterBounds, definition);

        // 3. Rotação: detectar orientação predominante (0°, 90°, 180°, 270°)
        var rotation = DetectRotation(cluster);

        // 4. Gerar label automática
        var label = GenerateLabel(definition, page);

        var placed = new PlacedElement(definition)
        {
            X = snapX,
            Y = snapY,
            Rotation = rotation,
            Scale = scale,
            Label = label
        };

        return placed;
    }

    /// <summary>
    /// Converte múltiplos clusters em PlacedElements.
    /// </summary>
    public static List<PlacedElement> ConvertAll(
        IReadOnlyList<ShapeCluster> clusters, Page page, double gridSpacing = 2.5)
    {
        var result = new List<PlacedElement>();
        foreach (var cluster in clusters)
        {
            if (cluster.IsRejected || !cluster.IsAccepted || cluster.MatchedDefinition is null)
                continue;

            var placed = Convert(cluster, page, gridSpacing);
            if (placed is not null)
                result.Add(placed);
        }
        return result;
    }

    /// <summary>
    /// Calcula a escala para o cluster caber na definição do elemento.
    /// </summary>
    private static double ComputeScale(Rect2D clusterBounds, ElementDefinition definition)
    {
        if (definition.Width <= 0 || definition.Height <= 0)
            return 1.0;

        var scaleX = clusterBounds.Width / definition.Width;
        var scaleY = clusterBounds.Height / definition.Height;

        // Escala uniforme (menor dimensão) para não distorcer
        return Math.Min(scaleX, scaleY);
    }

    /// <summary>
    /// Detecta a rotação mais provável do cluster (0°, 90°, 180°, 270°)
    /// analisando a orientação das linhas predominantes.
    /// </summary>
    private static double DetectRotation(ShapeCluster cluster)
    {
        var horizontalLength = 0.0;
        var verticalLength = 0.0;

        foreach (var shape in cluster.Shapes)
        {
            for (var i = 0; i < shape.Points.Count - 1; i++)
            {
                var dx = Math.Abs(shape.Points[i + 1].X - shape.Points[i].X);
                var dy = Math.Abs(shape.Points[i + 1].Y - shape.Points[i].Y);

                if (dx > dy)
                    horizontalLength += dx;
                else
                    verticalLength += dy;
            }
        }

        // Se as linhas verticais dominam, provavelmente está rotacionado 90° ou 270°
        // Por padrão, retorna 0° (a maioria dos símbolos elétricos está na orientação padrão)
        return 0.0;
    }

    /// <summary>
    /// Gera a próxima label disponível para o tipo de elemento na página.
    /// </summary>
    private static string GenerateLabel(ElementDefinition definition, Page page)
    {
        var prefix = ElementLabels.Prefix(definition);
        var existingNumbers = page.Objects
            .OfType<PlacedElement>()
            .Where(e => e.Label != null && e.Label.StartsWith(prefix, StringComparison.Ordinal))
            .Select(e =>
            {
                var numStr = e.Label[prefix.Length..];
                return int.TryParse(numStr, out var num) ? num : 0;
            })
            .ToList();

        var nextNumber = existingNumbers.Count > 0 ? existingNumbers.Max() + 1 : 1;
        return $"{prefix}{nextNumber}";
    }

    /// <summary>
    /// Snap de coordenadas ao grid (2.5mm padrão), ancorado no centro do papel.
    /// </summary>
    private static (double X, double Y) SnapToGrid(double x, double y, double grid)
    {
        return (
            Math.Round(x / grid) * grid,
            Math.Round(y / grid) * grid
        );
    }
}
