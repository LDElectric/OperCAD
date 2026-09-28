using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OperCAD.UI.Views;

/// <summary>Modo de layout para colagem múltipla.</summary>
public enum MultiPasteLayout
{
    Horizontal,
    Vertical,
    Grade
}

/// <summary>
/// Resultado da janela de colagem múltipla: configurações escolhidas pelo usuário.
/// </summary>
public sealed class MultiPasteResult
{
    /// <summary>Número de cópias a colar.</summary>
    public int Copies { get; init; } = 1;

    /// <summary>Modo de layout.</summary>
    public MultiPasteLayout Layout { get; init; } = MultiPasteLayout.Horizontal;

    /// <summary>Espaçamento entre cópias em mm.</summary>
    public double Spacing { get; init; } = 20;
}

/// <summary>
/// Janela de colagem múltipla: permite ao usuário especificar quantas cópias
/// colar e o layout (horizontal, vertical ou grade), com pré-visualização.
/// </summary>
public partial class MultiPasteWindow : Window
{
    /// <summary>Retorna o resultado configurado pelo usuário (null se cancelado).</summary>
    public MultiPasteResult? Result { get; private set; }

    public MultiPasteWindow()
    {
        InitializeComponent();
        UpdatePreview();
    }

    private int GetCopies()
    {
        if (int.TryParse(TxtCopies.Text, out var n) && n > 0)
            return Math.Min(n, 100); // Limite máximo de 100 cópias
        return 1;
    }

    private double GetSpacing()
    {
        if (double.TryParse(TxtSpacing.Text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0)
            return s;
        return 20;
    }

    private MultiPasteLayout GetLayout() => CmbLayout.SelectedIndex switch
    {
        1 => MultiPasteLayout.Vertical,
        2 => MultiPasteLayout.Grade,
        _ => MultiPasteLayout.Horizontal
    };

    private void UpdatePreview()
    {
        PreviewCanvas.Children.Clear();

        var copies = GetCopies();
        var spacing = GetSpacing();
        var layout = GetLayout();

        // Parâmetros de visualização
        var canvasW = PreviewCanvas.ActualWidth > 0 ? PreviewCanvas.ActualWidth : 360;
        var canvasH = PreviewCanvas.ActualHeight > 0 ? PreviewCanvas.ActualHeight : 160;
        var elementW = 40.0; // Largura do retângulo de preview
        var elementH = 30.0; // Altura do retângulo de preview
        var scale = Math.Min(canvasW / (elementW * (copies + 1)), canvasH / (elementH * 2));
        scale = Math.Min(scale, 4); // Limite máximo de escala

        var positions = ComputePositions(copies, spacing, layout);

        // Desenha as cópias
        for (var i = 0; i < positions.Count; i++)
        {
            var (x, y) = positions[i];
            var screenX = x * scale + canvasW / 2 - positions.Average(p => p.X) * scale;
            var screenY = y * scale + canvasH / 2 - positions.Average(p => p.Y) * scale;

            var rect = new Rectangle
            {
                Width = elementW * scale,
                Height = elementH * scale,
                Fill = i == 0
                    ? new SolidColorBrush(Color.FromRgb(47, 155, 255))
                    : new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(rect, screenX - elementW * scale / 2);
            Canvas.SetTop(rect, screenY - elementH * scale / 2);
            PreviewCanvas.Children.Add(rect);

            // Número da cópia
            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(label, screenX - 4);
            Canvas.SetTop(label, screenY - 7);
            PreviewCanvas.Children.Add(label);
        }
    }

    /// <summary>Calcula as posições de cada cópia no mundo (coordenadas relativas).</summary>
    private List<(double X, double Y)> ComputePositions(int copies, double spacing, MultiPasteLayout layout)
    {
        var positions = new List<(double, double)>();

        switch (layout)
        {
            case MultiPasteLayout.Horizontal:
                for (var i = 0; i < copies; i++)
                    positions.Add((i * spacing, 0));
                break;

            case MultiPasteLayout.Vertical:
                for (var i = 0; i < copies; i++)
                    positions.Add((0, -i * spacing));
                break;

            case MultiPasteLayout.Grade:
                var cols = (int)Math.Ceiling(Math.Sqrt(copies));
                for (var i = 0; i < copies; i++)
                {
                    var row = i / cols;
                    var col = i % cols;
                    positions.Add((col * spacing, -row * spacing));
                }
                break;
        }

        return positions;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new MultiPasteResult
        {
            Copies = GetCopies(),
            Layout = GetLayout(),
            Spacing = GetSpacing()
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
