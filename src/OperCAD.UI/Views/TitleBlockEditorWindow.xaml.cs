using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OperCAD.Application;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>
/// Editor de modelos de legenda: edita nome, grade (colunas/linhas) e células, e salva alterações.
/// Inclui: visualização em canvas, undo/redo, mesclar/dividir células, inserção de logo.
/// </summary>
public partial class TitleBlockEditorWindow : Window
{
    private readonly TitleBlockTemplate _template;
    private string? _filePath;
    private int _selectedCellIndex = -1;

    // Pilha de desfazer/refazer (snapshots do template)
    private readonly List<TitleBlockTemplateSnapshot> _undoStack = new();
    private int _undoIndex = -1;
    private const int MaxUndoLevels = 50;

    // Logos inseridos nas células
    private readonly List<TitleBlockLogo> _logos = new();

    public TitleBlockEditorWindow(TitleBlockTemplate template, string? filePath = null)
    {
        InitializeComponent();
        _template = template;
        _filePath = filePath;

        CmbPropAlign.ItemsSource = new[] { "left", "center", "right" };
        CmbPropValign.ItemsSource = new[] { "top", "center", "bottom" };

        // Salvar estado inicial para desfazer
        PushSnapshot();

        SyncFields();
        RefreshCells();
        RenderCanvas();
        UpdateUndoButtons();
        Title = $"Editor de legendas — {template.Name}";
    }

    #region Undo/Redo

    private sealed class TitleBlockTemplateSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public string Information { get; init; } = string.Empty;
        public List<int> RowHeights { get; init; } = new();
        public List<(TitleBlockColumnLength Type, int Value)> ColumnWidths { get; init; } = new();
        public List<TitleBlockCellSnapshot> Cells { get; init; } = new();
    }

    private sealed class TitleBlockCellSnapshot
    {
        public int Row { get; init; }
        public int Col { get; init; }
        public int RowSpan { get; init; }
        public int ColSpan { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public bool DisplayLabel { get; init; }
        public string Align { get; init; } = "center";
        public string Valign { get; init; } = "center";
        public bool Hadjust { get; init; }
        public int FontSize { get; init; }
    }

    private void PushSnapshot()
    {
        // Remover estados à frente do índice atual (refazer)
        if (_undoIndex < _undoStack.Count - 1)
            _undoStack.RemoveRange(_undoIndex + 1, _undoStack.Count - _undoIndex - 1);

        var snapshot = new TitleBlockTemplateSnapshot
        {
            Name = _template.Name,
            Information = _template.Information,
            RowHeights = new List<int>(_template.RowHeights),
            ColumnWidths = new List<(TitleBlockColumnLength, int)>(_template.ColumnWidths),
            Cells = _template.Cells.Select(c => new TitleBlockCellSnapshot
            {
                Row = c.Row,
                Col = c.Col,
                RowSpan = c.RowSpan,
                ColSpan = c.ColSpan,
                Name = c.Name,
                Value = c.Value,
                Label = c.Label,
                DisplayLabel = c.DisplayLabel,
                Align = c.Align,
                Valign = c.Valign,
                Hadjust = c.Hadjust,
                FontSize = c.FontSize
            }).ToList()
        };

        _undoStack.Add(snapshot);
        _undoIndex = _undoStack.Count - 1;

        // Limitar tamanho da pilha
        if (_undoStack.Count > MaxUndoLevels)
        {
            _undoStack.RemoveAt(0);
            _undoIndex--;
        }

        UpdateUndoButtons();
    }

    private void RestoreSnapshot(int index)
    {
        if (index < 0 || index >= _undoStack.Count)
            return;

        var snapshot = _undoStack[index];
        _template.Name = snapshot.Name;
        _template.Information = snapshot.Information;
        _template.RowHeights.Clear();
        _template.RowHeights.AddRange(snapshot.RowHeights);
        _template.ColumnWidths.Clear();
        _template.ColumnWidths.AddRange(snapshot.ColumnWidths);
        _template.Cells.Clear();
        foreach (var cellSnap in snapshot.Cells)
        {
            _template.Cells.Add(new TitleBlockCell
            {
                Row = cellSnap.Row,
                Col = cellSnap.Col,
                RowSpan = cellSnap.RowSpan,
                ColSpan = cellSnap.ColSpan,
                Name = cellSnap.Name,
                Value = cellSnap.Value,
                Label = cellSnap.Label,
                DisplayLabel = cellSnap.DisplayLabel,
                Align = cellSnap.Align,
                Valign = cellSnap.Valign,
                Hadjust = cellSnap.Hadjust,
                FontSize = cellSnap.FontSize
            });
        }

        SyncFields();
        RefreshCells();
        RenderCanvas();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undoIndex > 0)
        {
            _undoIndex--;
            RestoreSnapshot(_undoIndex);
            UpdateUndoButtons();
        }
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_undoIndex < _undoStack.Count - 1)
        {
            _undoIndex++;
            RestoreSnapshot(_undoIndex);
            UpdateUndoButtons();
        }
    }

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = _undoIndex > 0;
        BtnRedo.IsEnabled = _undoIndex < _undoStack.Count - 1;
    }

    #endregion

    #region Campos e Células

    private void SyncFields()
    {
        TxtName.Text = _template.Name;
        TxtInformation.Text = _template.Information;
        TxtCols.Text = TitleBlockTemplateWriter.ColumnsSpec(_template);
        TxtRows.Text = string.Join(";", _template.RowHeights);
    }

    private void RefreshCells()
    {
        // Adicionar propriedade de exibição para span
        var displayCells = _template.Cells.Select((c, i) => new
        {
            Index = i,
            c.Row,
            c.Col,
            CellSpanDisplay = c.RowSpan > 0 || c.ColSpan > 0
                ? $"{c.RowSpan}x{c.ColSpan}"
                : "-",
            c.Name,
            c.Value,
            c.Label
        }).ToList();

        LstCells.ItemsSource = displayCells;
    }

    private void OnApplyGrid(object sender, RoutedEventArgs e)
    {
        PushSnapshot();
        var rows = ParseRows(TxtRows.Text);
        var cols = ParseColumns(TxtCols.Text);
        if (rows.Count == 0 || cols.Count == 0)
        {
            MessageBox.Show(this, "Grade inválida. Exemplos:\nColunas: t6%;t4%;r100%;50px;\nLinhas: 10;10;10;",
                "Grade", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _template.RowHeights.Clear();
        _template.RowHeights.AddRange(rows);
        _template.ColumnWidths.Clear();
        _template.ColumnWidths.AddRange(cols);
        // Remove células fora da nova grade
        foreach (var cell in _template.Cells
                     .Where(c => c.Row >= _template.RowHeights.Count || c.Col >= _template.ColumnWidths.Count)
                     .ToList())
            _template.Cells.Remove(cell);
        RefreshCells();
        RenderCanvas();
    }

    private static List<int> ParseRows(string text) => (text ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Regex.Match(d.Trim(), @"^(\d+)(px)?$", RegexOptions.IgnoreCase))
        .Where(m => m.Success)
        .Select(m => int.Parse(m.Groups[1].Value))
        .ToList();

    private static List<(TitleBlockColumnLength, int)> ParseColumns(string text)
    {
        var result = new List<(TitleBlockColumnLength, int)>();
        foreach (var v in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = v.Trim();
            var abs = Regex.Match(s, @"^(\d+)(px)?$", RegexOptions.IgnoreCase);
            if (abs.Success)
            {
                result.Add((TitleBlockColumnLength.Absolute, int.Parse(abs.Groups[1].Value)));
                continue;
            }
            var rel = Regex.Match(s, @"^([rt])(\d+)%$", RegexOptions.IgnoreCase);
            if (rel.Success)
            {
                var type = rel.Groups[1].Value.Equals("t", StringComparison.OrdinalIgnoreCase)
                    ? TitleBlockColumnLength.RelativeToTotal
                    : TitleBlockColumnLength.RelativeToRemaining;
                result.Add((type, int.Parse(rel.Groups[2].Value)));
            }
        }
        return result;
    }

    private void OnCellSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstCells.SelectedIndex >= 0 && LstCells.SelectedIndex < _template.Cells.Count)
        {
            _selectedCellIndex = LstCells.SelectedIndex;
            SyncCellProperties(_template.Cells[_selectedCellIndex]);
        }
        else
        {
            _selectedCellIndex = -1;
            ClearCellProperties();
        }
    }

    private void SyncCellProperties(TitleBlockCell cell)
    {
        TxtPropName.Text = cell.Name;
        TxtPropValue.Text = cell.Value;
        TxtPropLabel.Text = cell.Label;
        TxtPropFontSize.Text = cell.FontSize > 0 ? cell.FontSize.ToString() : "";
        CmbPropAlign.SelectedItem = cell.Align;
        CmbPropValign.SelectedItem = cell.Valign;
        ChkPropDisplayLabel.IsChecked = cell.DisplayLabel;
        ChkPropHadjust.IsChecked = cell.Hadjust;

        // Logo
        var logo = _logos.FirstOrDefault(l => l.CellIndex == _selectedCellIndex);
        if (logo != null)
        {
            TxtLogoX.Text = logo.X.ToString("0.##", CultureInfo.InvariantCulture);
            TxtLogoY.Text = logo.Y.ToString("0.##", CultureInfo.InvariantCulture);
            TxtLogoWidth.Text = logo.Width.ToString("0.##", CultureInfo.InvariantCulture);
            TxtLogoHeight.Text = logo.Height.ToString("0.##", CultureInfo.InvariantCulture);
            TxtLogoFileName.Text = logo.FileName;
            TxtLogoStatus.Text = "Inserido";
        }
        else
        {
            TxtLogoX.Text = "0";
            TxtLogoY.Text = "0";
            TxtLogoWidth.Text = "20";
            TxtLogoHeight.Text = "20";
            TxtLogoFileName.Text = "";
            TxtLogoStatus.Text = "Nenhum";
        }
    }

    private void ClearCellProperties()
    {
        TxtPropName.Text = "";
        TxtPropValue.Text = "";
        TxtPropLabel.Text = "";
        TxtPropFontSize.Text = "";
        CmbPropAlign.SelectedItem = null;
        CmbPropValign.SelectedItem = null;
        ChkPropDisplayLabel.IsChecked = false;
        ChkPropHadjust.IsChecked = false;
        TxtLogoX.Text = "0";
        TxtLogoY.Text = "0";
        TxtLogoWidth.Text = "20";
        TxtLogoHeight.Text = "20";
        TxtLogoFileName.Text = "";
        TxtLogoStatus.Text = "Nenhum";
    }

    private void OnPropertyChanged(object sender, RoutedEventArgs e)
    {
        if (_selectedCellIndex < 0 || _selectedCellIndex >= _template.Cells.Count)
            return;

        PushSnapshot();
        var cell = _template.Cells[_selectedCellIndex];
        cell.Name = TxtPropName.Text ?? string.Empty;
        cell.Value = TxtPropValue.Text ?? string.Empty;
        cell.Label = TxtPropLabel.Text ?? string.Empty;

        if (int.TryParse(TxtPropFontSize.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fontSize))
            cell.FontSize = fontSize;

        if (CmbPropAlign.SelectedItem is string align)
            cell.Align = align;

        if (CmbPropValign.SelectedItem is string valign)
            cell.Valign = valign;

        cell.DisplayLabel = ChkPropDisplayLabel.IsChecked == true;
        cell.Hadjust = ChkPropHadjust.IsChecked == true;

        RefreshCells();
        RenderCanvas();
    }

    #endregion

    #region Canvas de Visualização

    private void RenderCanvas()
    {
        TitleBlockCanvas.Children.Clear();

        if (_template.RowHeights.Count == 0 || _template.ColumnWidths.Count == 0)
            return;

        // Calcular dimensões totais da legenda
        var totalHeight = _template.RowHeights.Sum();
        var totalWidth = CalculateTotalWidth();

        if (totalWidth <= 0 || totalHeight <= 0)
            return;

        // Fator de escala para caber no canvas
        var canvasWidth = TitleBlockCanvas.ActualWidth > 0 ? TitleBlockCanvas.ActualWidth : 800;
        var canvasHeight = TitleBlockCanvas.ActualHeight > 0 ? TitleBlockCanvas.ActualHeight : 400;
        var scaleX = canvasWidth / totalWidth;
        var scaleY = canvasHeight / totalHeight;
        var scale = Math.Min(scaleX, scaleY) * 0.9; // 90% para margem

        var offsetX = (canvasWidth - totalWidth * scale) / 2.0;
        var offsetY = (canvasHeight - totalHeight * scale) / 2.0;

        // Calcular larguras das colunas
        var colWidths = CalculateColumnWidths(totalWidth);

        // Desenhar grade
        var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 1);

        // Linhas horizontais
        double y = 0;
        foreach (var rowHeight in _template.RowHeights)
        {
            var screenY = offsetY + y * scale;
            TitleBlockCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = offsetX,
                Y1 = screenY,
                X2 = offsetX + totalWidth * scale,
                Y2 = screenY,
                Stroke = pen.Brush,
                StrokeThickness = pen.Thickness
            });
            y += rowHeight;
        }
        // Linha final
        TitleBlockCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = offsetX,
            Y1 = offsetY + totalHeight * scale,
            X2 = offsetX + totalWidth * scale,
            Y2 = offsetY + totalHeight * scale,
            Stroke = pen.Brush,
            StrokeThickness = pen.Thickness
        });

        // Linhas verticais
        double x = 0;
        foreach (var colWidth in colWidths)
        {
            var screenX = offsetX + x * scale;
            TitleBlockCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = screenX,
                Y1 = offsetY,
                X2 = screenX,
                Y2 = offsetY + totalHeight * scale,
                Stroke = pen.Brush,
                StrokeThickness = pen.Thickness
            });
            x += colWidth;
        }
        // Linha final
        TitleBlockCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = offsetX + totalWidth * scale,
            Y1 = offsetY,
            X2 = offsetX + totalWidth * scale,
            Y2 = offsetY + totalHeight * scale,
            Stroke = pen.Brush,
            StrokeThickness = pen.Thickness
        });

        // Desenhar células com texto
        foreach (var cell in _template.Cells)
        {
            if (cell.Row >= _template.RowHeights.Count || cell.Col >= colWidths.Count)
                continue;

            // Calcular posição da célula
            double cellX = 0;
            for (int c = 0; c < cell.Col; c++)
                cellX += colWidths[c];

            double cellY = 0;
            for (int r = 0; r < cell.Row; r++)
                cellY += _template.RowHeights[r];

            var cellWidth = colWidths[cell.Col];
            var cellHeight = _template.RowHeights[cell.Row];

            // Adicionar span
            for (int s = 1; s <= cell.ColSpan && cell.Col + s < colWidths.Count; s++)
                cellWidth += colWidths[cell.Col + s];
            for (int s = 1; s <= cell.RowSpan && cell.Row + s < _template.RowHeights.Count; s++)
                cellHeight += _template.RowHeights[cell.Row + s];

            // Texto da célula
            var displayText = cell.DisplayLabel && !string.IsNullOrEmpty(cell.Label)
                ? $"{cell.Label}: {cell.Value}"
                : cell.Value;

            if (!string.IsNullOrEmpty(displayText))
            {
                var fontSize = cell.FontSize > 0 ? cell.FontSize : 9;
                var tb = new TextBlock
                {
                    Text = displayText,
                    FontSize = fontSize,
                    Foreground = System.Windows.Media.Brushes.Black,
                    TextAlignment = cell.Align switch
                    {
                        "left" => System.Windows.TextAlignment.Left,
                        "right" => System.Windows.TextAlignment.Right,
                        _ => System.Windows.TextAlignment.Center
                    }
                };

                // Posicionar texto na célula
                var textX = offsetX + cellX * scale + 2;
                var textY = offsetY + cellY * scale + 2;
                var textWidth = cellWidth * scale - 4;
                var textHeight = cellHeight * scale - 4;

                if (textWidth > 0 && textHeight > 0)
                {
                    tb.Measure(new Size(textWidth, textHeight));
                    Canvas.SetLeft(tb, textX);
                    Canvas.SetTop(tb, textY);
                    TitleBlockCanvas.Children.Add(tb);
                }
            }

            // Desenhar logo se houver
            var logo = _logos.FirstOrDefault(l => l.CellIndex == _template.Cells.IndexOf(cell));
            if (logo?.ImageData != null && logo.ImageData.Length > 0)
            {
                try
                {
                    using var ms = new MemoryStream(logo.ImageData);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = ms;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var image = new System.Windows.Controls.Image
                    {
                        Source = bitmap,
                        Width = logo.Width * scale,
                        Height = logo.Height * scale
                    };

                    var imgX = offsetX + (cellX + logo.X) * scale;
                    var imgY = offsetY + (cellY + logo.Y) * scale;
                    Canvas.SetLeft(image, imgX);
                    Canvas.SetTop(image, imgY);
                    TitleBlockCanvas.Children.Add(image);
                }
                catch
                {
                    // Ignorar erros de imagem
                }
            }
        }
    }

    private double CalculateTotalWidth()
    {
        double total = 0;
        foreach (var (type, value) in _template.ColumnWidths)
        {
            total += type switch
            {
                TitleBlockColumnLength.Absolute => value,
                TitleBlockColumnLength.RelativeToTotal => _template.Height * value / 100.0,
                TitleBlockColumnLength.RelativeToRemaining => total * value / 100.0,
                _ => value
            };
        }
        return total;
    }

    private List<double> CalculateColumnWidths(double totalWidth)
    {
        var widths = new List<double>();
        double usedWidth = 0;

        foreach (var (type, value) in _template.ColumnWidths)
        {
            widths.Add(type switch
            {
                TitleBlockColumnLength.Absolute => value,
                TitleBlockColumnLength.RelativeToTotal => totalWidth * value / 100.0,
                TitleBlockColumnLength.RelativeToRemaining => (totalWidth - usedWidth) * value / 100.0,
                _ => value
            });
            usedWidth += widths.Last();
        }

        return widths;
    }

    #endregion

    #region Ferramentas

    private void OnSelectTool(object sender, RoutedEventArgs e)
    {
        // Nenhuma ação necessária — a seleção é o modo padrão do editor.
    }

    private void OnMergeCells(object sender, RoutedEventArgs e)
    {
        if (_selectedCellIndex < 0 || _selectedCellIndex >= _template.Cells.Count)
        {
            MessageBox.Show(this, "Selecione uma célula para mesclar.", "Mesclar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cell = _template.Cells[_selectedCellIndex];

        // Verificar se há célula adjacente para mesclar
        var adjacentCell = _template.Cells.FirstOrDefault(c =>
            c != cell &&
            Math.Abs(c.Row - cell.Row) <= 1 &&
            Math.Abs(c.Col - cell.Col) <= 1);

        if (adjacentCell == null)
        {
            MessageBox.Show(this, "Não há célula adjacente para mesclar.", "Mesclar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PushSnapshot();

        // Mesclar: expandir a célula selecionada
        var minRow = Math.Min(cell.Row, adjacentCell.Row);
        var minCol = Math.Min(cell.Col, adjacentCell.Col);
        var maxRow = Math.Max(cell.Row, adjacentCell.Row);
        var maxCol = Math.Max(cell.Col, adjacentCell.Col);

        cell.Row = minRow;
        cell.Col = minCol;
        cell.RowSpan = maxRow - minRow;
        cell.ColSpan = maxCol - minCol;

        // Remover a célula adjacente
        _template.Cells.Remove(adjacentCell);

        RefreshCells();
        RenderCanvas();
    }

    private void OnSplitCell(object sender, RoutedEventArgs e)
    {
        if (_selectedCellIndex < 0 || _selectedCellIndex >= _template.Cells.Count)
        {
            MessageBox.Show(this, "Selecione uma célula mesclada para dividir.", "Dividir",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cell = _template.Cells[_selectedCellIndex];
        if (cell.RowSpan <= 0 && cell.ColSpan <= 0)
        {
            MessageBox.Show(this, "A célula não está mesclada.", "Dividir",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PushSnapshot();

        // Dividir: criar células individuais para cada posição coberta
        var cellsToAdd = new List<TitleBlockCell>();
        for (int r = cell.Row; r <= cell.Row + cell.RowSpan; r++)
        {
            for (int c = cell.Col; c <= cell.Col + cell.ColSpan; c++)
            {
                if (r == cell.Row && c == cell.Col)
                    continue; // Pular a célula original

                cellsToAdd.Add(new TitleBlockCell
                {
                    Row = r,
                    Col = c,
                    RowSpan = 0,
                    ColSpan = 0,
                    Name = cell.Name,
                    Value = cell.Value,
                    Label = cell.Label,
                    DisplayLabel = cell.DisplayLabel,
                    Align = cell.Align,
                    Valign = cell.Valign,
                    Hadjust = cell.Hadjust,
                    FontSize = cell.FontSize
                });
            }
        }

        // Resetar a célula original
        cell.RowSpan = 0;
        cell.ColSpan = 0;

        // Adicionar novas células
        foreach (var newCell in cellsToAdd)
            _template.Cells.Add(newCell);

        RefreshCells();
        RenderCanvas();
    }

    #endregion

    #region Logo

    private void OnInsertLogo(object sender, RoutedEventArgs e)
    {
        if (_selectedCellIndex < 0 || _selectedCellIndex >= _template.Cells.Count)
        {
            MessageBox.Show(this, "Selecione uma célula para inserir o logo.", "Inserir Logo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp;*.svg)|*.png;*.jpg;*.jpeg;*.bmp;*.svg|PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|BMP (*.bmp)|*.bmp|SVG (*.svg)|*.svg",
            Title = "Selecionar logo"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var imageData = File.ReadAllBytes(dlg.FileName);
            var logo = new TitleBlockLogo
            {
                CellIndex = _selectedCellIndex,
                ImageData = imageData,
                FileName = Path.GetFileName(dlg.FileName),
                X = 0,
                Y = 0,
                Width = 20,
                Height = 20
            };

            // Remover logo existente na célula
            _logos.RemoveAll(l => l.CellIndex == _selectedCellIndex);
            _logos.Add(logo);

            PushSnapshot();
            SyncCellProperties(_template.Cells[_selectedCellIndex]);
            RenderCanvas();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Erro ao carregar logo: {ex.Message}", "Inserir Logo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRemoveLogo(object sender, RoutedEventArgs e)
    {
        if (_selectedCellIndex < 0)
            return;

        var logo = _logos.FirstOrDefault(l => l.CellIndex == _selectedCellIndex);
        if (logo == null)
        {
            MessageBox.Show(this, "Nenhum logo inserido nesta célula.", "Remover Logo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PushSnapshot();
        _logos.Remove(logo);
        SyncCellProperties(_template.Cells[_selectedCellIndex]);
        RenderCanvas();
    }

    private void OnLogoPropertyChanged(object sender, RoutedEventArgs e)
    {
        if (_selectedCellIndex < 0)
            return;

        var logo = _logos.FirstOrDefault(l => l.CellIndex == _selectedCellIndex);
        if (logo == null)
            return;

        if (double.TryParse(TxtLogoX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            logo.X = x;
        if (double.TryParse(TxtLogoY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            logo.Y = y;
        if (double.TryParse(TxtLogoWidth.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
            logo.Width = w;
        if (double.TryParse(TxtLogoHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            logo.Height = h;

        RenderCanvas();
    }

    #endregion

    #region Células - CRUD

    private void OnAddCell(object sender, RoutedEventArgs e)
    {
        PushSnapshot();
        var cell = new TitleBlockCell { Row = 0, Col = 0, DisplayLabel = true };
        var dialog = new TitleBlockCellWindow(cell) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _template.Cells.Add(cell);
            RefreshCells();
            RenderCanvas();
        }
    }

    private void OnEditCell(object sender, RoutedEventArgs e)
    {
        if (LstCells.SelectedIndex < 0 || LstCells.SelectedIndex >= _template.Cells.Count)
            return;
        var cell = _template.Cells[LstCells.SelectedIndex];
        var dialog = new TitleBlockCellWindow(cell) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            PushSnapshot();
            RefreshCells();
            RenderCanvas();
        }
    }

    private void OnRemoveCell(object sender, RoutedEventArgs e)
    {
        if (LstCells.SelectedIndex < 0 || LstCells.SelectedIndex >= _template.Cells.Count)
            return;
        PushSnapshot();
        _template.Cells.RemoveAt(LstCells.SelectedIndex);
        _selectedCellIndex = -1;
        RefreshCells();
        RenderCanvas();
    }

    #endregion

    #region Salvar

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var path = _filePath;
        if (string.IsNullOrEmpty(path))
        {
            OnSaveAs(sender, e);
            return;
        }
        Save(path);
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Legenda externa (*.titleblock)|*.titleblock",
            FileName = (string.IsNullOrWhiteSpace(_template.Name) ? "legenda" : _template.Name) + ".titleblock",
            Title = "Salvar legenda"
        };
        if (dlg.ShowDialog() != true)
            return;
        Save(dlg.FileName);
    }

    private void Save(string path)
    {
        _template.Name = TxtName.Text.Trim();
        _template.Information = TxtInformation.Text ?? string.Empty;
        OnApplyGrid(this, new RoutedEventArgs());
        try
        {
            TitleBlockTemplateWriter.Save(_template, path);
            _filePath = path;
            TitleBlockTemplateCatalog.Default.Invalidate();
            MessageBox.Show(this, $"Legenda salva em {path}.", "Salvar legenda",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Erro ao salvar: " + ex.Message, "Salvar legenda",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion
}
