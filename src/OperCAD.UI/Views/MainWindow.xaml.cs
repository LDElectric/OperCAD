using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.TerminalStrips;
using OperCAD.Core.Undo;
using OperCAD.Drawing;
using OperCAD.UI.ViewModels;
using Page = OperCAD.Core.Documents.Page;

namespace OperCAD.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private bool _allowClose;
    private bool _isClosing;
    private bool _closeRequestPending;
    private readonly string? _initialFile;

    // Estado do clique/arrasto na barra de elementos (clique → fantasma; arrasto → DnD)
    private Point _barMouseDownPos;
    private FrameworkElement? _barDragSource;
    private bool _barDragStarted;

    public MainWindow(string? initialFile = null)
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.AttachSheetViewport(Sheet);
        Sheet.ElementActivated += OnElementActivated;
        Sheet.ConductorActivated += OnConductorActivated;
        Sheet.TitleBlockActivated += OnTitleBlockActivated;
        Sheet.TitleBlockFieldActivated += OnTitleBlockFieldActivated;
        Sheet.TitleBlockFieldCommitted += OnTitleBlockFieldCommitted;
        Sheet.TerminalStripActivated += OnTerminalStripActivated;
        Sheet.TextPlacementRequested += OnTextPlacementRequested;
        Sheet.ImagePlacementRequested += OnImagePlacementRequested;
        Sheet.ObjectContextRequested += OnObjectContextRequested;
        Sheet.EmptySpacePropertiesRequested += OnEmptySpacePropertiesRequested;
        Sheet.SelectionChanged += OnSelectionChanged;
        Sheet.DrawToolChanged += OnDrawToolChanged;
        _viewModel.DiagramPropertiesRequested += OnDiagramPropertiesRequested;
        _viewModel.ConfigRequested += OnConfigRequested;
        _viewModel.PrintRequested += OnPrintRequested;
        _viewModel.ExportRequested += OnExportRequested;
        _viewModel.PdfExportRequested += OnPdfExportRequested;
        _viewModel.ElementTriageRequested += OnElementTriageRequested;
        _viewModel.OpenInNewWindowRequested += OnOpenInNewWindowRequested;
        _viewModel.CloseWindowRequested += OnCloseWindowRequested;
        _viewModel.ElementPropertiesRequested += OnElementActivated;
        _viewModel.ConductorPropertiesRequested += OnConductorActivated;
        _viewModel.SearchRequested += OnSearchRequested;
        _viewModel.PdfWizardRequested += OnPdfWizardRequested;
        _viewModel.ToggleFullScreenRequested += OnToggleFullScreenFromCommand;
        _viewModel.ProjectPropertiesRequested += OnProjectPropertiesRequested;
        _viewModel.TitleBlockEditorRequested += OnTitleBlockEditorRequested;
        _viewModel.PotentialSelectorRequested += OnPotentialSelectorRequested;
        _viewModel.AutoNumberFormulaRequested += OnAutoNumberFormulaRequested;
        _viewModel.FormulaEditorRequested += OnFormulaEditorRequested;
        _viewModel.StatusElementNameChanged += OnStatusElementNameChanged;
        _viewModel.RotateWithAngleRequested += ShowRotateDialog;
        Loaded += MainWindow_Loaded;
        _initialFile = initialFile;
    }

    private void OnOpenInNewWindowRequested(string? path)
    {
        var window = new MainWindow(path);
        window.Show();
    }

    private void OnPdfWizardRequested(string pdfPath)
    {
        // Passo 1: Mostrar prévia com detecção automática
        var preview = new PdfPreviewDialog(pdfPath) { Owner = this };
        if (preview.ShowDialog() == true)
        {
            // Usuário clicou "Importar" na prévia — usar resultado da detecção automática
            if (preview.ImportedShapes.Count > 0 || preview.ImportedTexts.Count > 0)
            {
                _viewModel.ImportShapesDirectly(
                    preview.ImportedShapes, preview.ImportedTexts, preview.ImportedPageSettings);
            }
            return;
        }

        // Passo 2: Usuário clicou "Selecionar Manualmente" ou fechou — abrir wizard manual
        if (preview.WantsManualSelection)
        {
            var wizard = new PdfImportWizard(pdfPath) { Owner = this };
            if (wizard.ShowDialog() == true)
            {
                if (wizard.ImportedShapes.Count > 0 || wizard.ImportedTexts.Count > 0)
                {
                    _viewModel.ImportShapesDirectly(
                        wizard.ImportedShapes, wizard.ImportedTexts, wizard.ImportedPageSettings);
                }
            }
        }
    }

    private void OnCloseWindowRequested()
    {
        if (_isClosing || _closeRequestPending || !IsLoaded)
            return;

        _closeRequestPending = true;
        _allowClose = true;
        // ConfirmClose pode ser chamado dentro de OnClosing. Close() síncrono seria uma
        // reentrada no mesmo ciclo de fechamento e o WPF lança InvalidOperationException.
        // A chamada única é agendada depois que o Closing atual terminar.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _closeRequestPending = false;
            if (_isClosing || !IsLoaded)
                return;
            Close();
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    /// <summary>Duplo-clique em um item de borne → abre o editor do borne (como o padrão CAD).</summary>
    private void OnTerminalStripActivated(TerminalStrip strip)
    {
        var dialog = new TerminalStripWindow(strip, new List<FreeTerminal>())
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
            _viewModel.RefreshSheet();
    }

    /// <summary>Abre o diálogo de propriedades do esquema ativo (como o padrão CAD).</summary>
    private void OnDiagramPropertiesRequested(Page page)
    {
        var before = FolhaSnapshot.Of(page);
        var dialog = new DiagramPropertiesWindow(page)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            var after = FolhaSnapshot.Of(page);
            _viewModel.PushFolhaProperties(page, before, after);
            _viewModel.ApplyElementScale();
            Sheet.FitToPage();
            _viewModel.MarkModified();
            _viewModel.StatusMessage = "Propriedades do esquema aplicadas.";
        }
    }

    /// <summary>Duplo-clique em um elemento → abre o diálogo de propriedades (como o padrão CAD).</summary>
    private void OnElementActivated(PlacedElement element)
    {
        var before = ElementInfoSnapshot.Of(element);
        var dialog = new ElementPropertiesWindow(element)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            var after = ElementInfoSnapshot.Of(element);
            _viewModel.PushElementProperties(element, before, after);
            _viewModel.RefreshSheet();
            _viewModel.MarkModified();
            _viewModel.StatusMessage = "Propriedades do elemento aplicadas.";
        }
    }

    /// <summary>Duplo-clique em um condutor → abre o diálogo de propriedades do condutor.</summary>
    private void OnConductorActivated(Conductor conductor)
    {
        var before = ConductorInfoSnapshot.Of(conductor);
        var dialog = new ConductorPropertiesWindow(conductor)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            var after = ConductorInfoSnapshot.Of(conductor);
            _viewModel.PushConductorProperties(conductor, before, after);
            _viewModel.RefreshSheet();
            _viewModel.MarkModified();
            _viewModel.StatusMessage = "Propriedades do condutor aplicadas.";
        }
    }

    /// <summary>Duplo-clique no legenda → abre "Propriedades da folha" (como o padrão CAD).</summary>
    private void OnTitleBlockActivated(Page page)
    {
        var before = FolhaSnapshot.Of(page);
        var dialog = new DiagramPropertiesWindow(page)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            var after = FolhaSnapshot.Of(page);
            _viewModel.PushFolhaProperties(page, before, after);
            _viewModel.ApplyElementScale();
            Sheet.FitToPage();
            _viewModel.MarkModified();
            _viewModel.StatusMessage = "Propriedades do folio aplicadas.";
        }
    }

    /// <summary>Duplo-clique numa caixa da legenda → abre edição inline diretamente na folha.</summary>
    private void OnTitleBlockFieldActivated(Page page, DrawingFrameRenderer.TitleBlockFieldHit hit)
    {
        var field = hit.Field.ToLowerInvariant();
        var current = GetTitleBlockValue(page, field);
        var maxLength = EstimateTitleBlockMaxLength(field, hit.Bounds);
        var multiline = field is "company" or "title";

        // Obtém o estilo personalizado existente para o campo (ou cria um novo)
        if (!page.TitleBlock.FieldStyles.TryGetValue(field, out var style))
            style = new OperCAD.Core.Documents.TitleBlockFieldStyle();

        // Aplica família de fonte padrão da barra se o campo ainda não tem estilo definido
        if (style.FontFamily is null)
            style.FontFamily = _viewModel.DrawingProperties.SelectedFontFamily;

        Sheet.BeginInlineTitleBlockEdit(page, field, hit.Bounds, current, maxLength, multiline, style);
    }

    /// <summary>Chamado quando o usuário conclui a edição inline da legenda (Enter, Tab, foco perdido).</summary>
    private void OnTitleBlockFieldCommitted(object? sender, ValueTuple<string, string, OperCAD.Core.Documents.TitleBlockFieldStyle> e)
    {
        var page = _viewModel.ActivePage;
        if (page is null) return;

        var (field, newValue, style) = e;
        var current = GetTitleBlockValue(page, field);
        if (newValue == current && PageHasSameStyle(page, field, style))
            return;

        var before = FolhaSnapshot.Of(page);
        SetTitleBlockValue(page, field, newValue);
        // Persiste o estilo do campo
        page.TitleBlock.FieldStyles[field] = style;
        var after = FolhaSnapshot.Of(page);
        _viewModel.PushFolhaProperties(page, before, after);
        _viewModel.MarkModified();
        Sheet.InvalidateVisual();
        var label = TitleBlockFieldLabel(field);
        _viewModel.StatusMessage = $"Campo {label.ToLowerInvariant()} atualizado.";
    }

    private static bool PageHasSameStyle(Page page, string field, OperCAD.Core.Documents.TitleBlockFieldStyle style)
    {
        if (!page.TitleBlock.FieldStyles.TryGetValue(field, out var existing)) return false;
        return existing.FontSizePt == style.FontSizePt
            && existing.Bold == style.Bold
            && existing.Italic == style.Italic
            && existing.FontFamily == style.FontFamily;
    }

    private static string TitleBlockFieldLabel(string field) => field switch
    {
        "author" => "AUTOR",
        "revpor" => "REV. POR",
        "aprovpor" => "APROV. POR",
        "company" => "EMPREENDIMENTO",
        "title" => "DESCRIÇÃO DO DESENHO",
        "drawingnumber" => "REGISTRO Nº",
        "revision" => "REVISÃO",
        "sheet" => "PÁGINA",
        "totalsheets" => "TOTAL DE PÁGINAS",
        _ => "CAMPO DA LEGENDA"
    };

    private static string GetTitleBlockValue(Page page, string field) => field switch
    {
        "author" => page.TitleBlock.Author,
        "revpor" => page.TitleBlock.ReviewedBy,
        "aprovpor" => page.TitleBlock.ApprovedBy,
        "company" => page.TitleBlock.Company,
        "title" => page.TitleBlock.Title,
        "drawingnumber" => page.TitleBlock.DrawingNumber,
        "revision" => page.TitleBlock.Revision,
        "sheet" => page.TitleBlock.Sheet,
        "totalsheets" => page.TitleBlock.TotalSheets,
        _ => string.Empty
    };

    private static void SetTitleBlockValue(Page page, string field, string value)
    {
        var t = page.TitleBlock;
        switch (field)
        {
            case "author": t.Author = value; break;
            case "revpor": t.ReviewedBy = value; break;
            case "aprovpor": t.ApprovedBy = value; break;
            case "company": t.Company = value; break;
            case "title": t.Title = value; break;
            case "drawingnumber": t.DrawingNumber = value; break;
            case "revision": t.Revision = value; break;
            case "sheet": t.Sheet = value; t.SheetAutoNumbered = false; break;
            case "totalsheets": t.TotalSheets = value; t.TotalSheetsAutoNumbered = false; break;
        }
    }

    /// <summary>
    /// Limite conservador derivado do retângulo real da célula. Campos grandes aceitam quebra;
    /// campos curtos ficam limitados para nunca estourar a caixa na fonte da legenda.
    /// </summary>
    private static int EstimateTitleBlockMaxLength(string field, OperCAD.Core.Geometry.Rect2D bounds)
    {
        var charsPerLine = Math.Max(4, (int)Math.Floor(bounds.Width / 1.25));
        if (field is not ("company" or "title"))
            return Math.Clamp(charsPerLine, 4, 32);
        var lines = Math.Clamp((int)Math.Floor(bounds.Height / 3.0), 1, 6);
        return Math.Clamp(charsPerLine * lines, 12, 160);
    }

    /// <summary>Pedido de inserção de texto independente na folha (direto, sem diálogo).</summary>
    private void OnTextPlacementRequested()
    {
        var item = new TextItem
        {
            Text = "Texto",
            FontSizePt = _viewModel.DrawingProperties.SelectedFontSize,
            FontFamily = _viewModel.DrawingProperties.SelectedFontFamily,
            Color = _viewModel.DrawingProperties.SelectedColor
        };
        _viewModel.BeginTextPlacement(item);
    }

    /// <summary>Pedido de inserção de imagem na folha (barra "Adicionar" → Imagem).</summary>
    private void OnImagePlacementRequested()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar imagem",
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os arquivos (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _viewModel.BeginImagePlacement(new OperCAD.Core.Documents.ImageItem
            {
                FilePath = dlg.FileName,
                Width = 30,
                Height = 30
            });
        }
    }

    /// <summary>Botão "Adicionar texto" da barra — ativa modo de posicionamento direto na folha.</summary>
    private void OnAddTextTool(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb && tb.IsChecked == true)
        {
            // Cria um TextItem vazio e entra em modo de posicionamento
            var item = new TextItem
            {
                Text = "Texto",
                FontSizePt = _viewModel.DrawingProperties.SelectedFontSize,
                FontFamily = _viewModel.DrawingProperties.SelectedFontFamily,
                Color = _viewModel.DrawingProperties.SelectedColor
            };
            _viewModel.BeginTextPlacement(item);
        }
        else
        {
            Sheet.CancelPendingPlacement();
        }
    }

    /// <summary>Botão "Adicionar imagem" da barra (abre o diálogo e entra em modo de posicionamento).</summary>
    private void OnAddImageTool(object sender, RoutedEventArgs e)
    {
        OnImagePlacementRequested();
        if (DataContext is ViewModels.MainWindowViewModel vm && !vm.IsImagePending)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
                tb.IsChecked = false;
        }
    }

    private void OnToggleLineTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartLineToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleRectangleTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartRectangleToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleEllipseTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartEllipseToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleTriangleTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartTriangleToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleCurveTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartCurveToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleArcTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartArcToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleEllipseArcTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartEllipseArcToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleRoundedRectangleTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartRoundedRectangleToolCommand.Execute(null);
                ShowDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    private void OnToggleMeasureTool(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            if (tb.IsChecked == true)
            {
                vm.EndDrawToolCommand.Execute(null);
                vm.StartMeasureToolCommand.Execute(null);
                // Régua não precisa de opções de estilo - esconde o flyout
                HideDrawToolStyleFlyout();
            }
            else
            {
                vm.EndDrawToolCommand.Execute(null);
                HideDrawToolStyleFlyout();
            }
        }
    }

    /// <summary>
    /// Clique com o botão direito sobre um objeto → menu de contexto com
    /// propriedades, copiar, recortar, colar e excluir.
    /// </summary>
    private void OnObjectContextRequested(DocumentObject obj)
    {
        var menu = new ContextMenu();
        var dataContext = DataContext as ViewModels.MainWindowViewModel;

        var props = new MenuItem { Header = "Propriedades..." };
        switch (obj)
        {
            case PlacedElement el:
                props.Click += (_, _) => OnElementActivated(el);
                break;
            case Conductor cond:
                props.Click += (_, _) => OnConductorActivated(cond);
                break;
            case TextItem t:
                props.Click += (_, _) => OnEditTextItem(t);
                break;
            case ShapeItem shape:
                props.Click += (_, _) =>
                {
                    // Seleciona a forma e abre o painel de propriedades
                    Sheet.SelectOnly(shape);
                    Sheet.NotifySelectionChanged();
                    dataContext.IsRightPanelVisible = true;
                };
                break;
            default:
                props.IsEnabled = false;
                break;
        }
        menu.Items.Add(props);

        if (obj is TextItem textObj)
        {
            var edit = new MenuItem { Header = "Editar texto" };
            edit.Click += (_, _) => OnEditTextItem(textObj);
            menu.Items.Add(edit);
        }

        if (obj is ShapeItem shapeItem)
        {
            menu.Items.Add(new Separator());

            // Submenu "Cor"
            var colorSub = new MenuItem { Header = "Cor" };
            var shapeColors = new[]
            {
                ("Preto", "#000000"),
                ("Vermelho", "#FF0000"),
                ("Azul", "#0000FF"),
                ("Verde", "#00AA00"),
                ("Amarelo", "#FFD700"),
                ("Laranja", "#FF8C00"),
                ("Roxo", "#800080"),
                ("Cinza", "#808080"),
                ("Branco", "#FFFFFF"),
                ("Ciano", "#00AAAA")
            };
            foreach (var (name, hex) in shapeColors)
            {
                var item = new MenuItem { Header = name, Tag = hex };
                item.Click += (_, _) =>
                {
                    var before = ShapeItemSnapshot.Of(shapeItem);
                    shapeItem.Color = hex;
                    var after = ShapeItemSnapshot.Of(shapeItem);
                    Sheet.UndoStack?.Push(new ChangeShapePropertiesCommand(shapeItem, before, after));
                    Sheet.NotifyEditStateChanged();
                    Sheet.Refresh();
                };
                colorSub.Items.Add(item);
            }
            menu.Items.Add(colorSub);

            // Opções de preenchimento
            var fillSub = new MenuItem { Header = "Preenchimento" };
            var fillNone = new MenuItem { Header = "Nenhum" };
            fillNone.Click += (_, _) =>
            {
                var before = ShapeItemSnapshot.Of(shapeItem);
                shapeItem.Filling = "none";
                Sheet.UndoStack?.Push(new ChangeShapePropertiesCommand(shapeItem, before, ShapeItemSnapshot.Of(shapeItem)));
                Sheet.NotifyEditStateChanged();
                Sheet.Refresh();
            };
            fillSub.Items.Add(fillNone);
            foreach (var (name, hex) in shapeColors.Take(6))
            {
                var item = new MenuItem { Header = name, Tag = hex };
                item.Click += (_, _) =>
                {
                    var before = ShapeItemSnapshot.Of(shapeItem);
                    shapeItem.Filling = hex;
                    Sheet.UndoStack?.Push(new ChangeShapePropertiesCommand(shapeItem, before, ShapeItemSnapshot.Of(shapeItem)));
                    Sheet.NotifyEditStateChanged();
                    Sheet.Refresh();
                };
                fillSub.Items.Add(item);
            }
            menu.Items.Add(fillSub);

            // Espessura
            var widthSub = new MenuItem { Header = "Espessura" };
            foreach (var w in new[] { 0.07, 0.15, 0.29, 0.50, 0.75, 1.0 })
            {
                var item = new MenuItem { Header = $"{w} mm" };
                item.Click += (_, _) =>
                {
                    var before = ShapeItemSnapshot.Of(shapeItem);
                    shapeItem.PenWidth = w;
                    Sheet.UndoStack?.Push(new ChangeShapePropertiesCommand(shapeItem, before, ShapeItemSnapshot.Of(shapeItem)));
                    Sheet.NotifyEditStateChanged();
                    Sheet.Refresh();
                };
                widthSub.Items.Add(item);
            }
            menu.Items.Add(widthSub);
        }

        if (obj is Conductor conductor)
        {
            menu.Items.Add(new Separator());

            // Submenu "Cor"
            var colorSub = new MenuItem { Header = "Cor" };
            var conductorColors = new[]
            {
                ("Preto", "#000000"),
                ("Vermelho", "#FF0000"),
                ("Azul", "#0000FF"),
                ("Verde", "#00AA00"),
                ("Amarelo", "#FFD700"),
                ("Laranja", "#FF8C00"),
                ("Roxo", "#800080"),
                ("Marrom", "#8B4513"),
                ("Cinza", "#808080"),
                ("Rosa", "#FF69B4")
            };
            foreach (var (name, hex) in conductorColors)
            {
                var item = new MenuItem { Header = name, Tag = hex };
                item.Click += (_, _) =>
                {
                    var before = ConductorInfoSnapshot.Of(conductor);
                    conductor.Color = hex;
                    var after = ConductorInfoSnapshot.Of(conductor);
                    Sheet.UndoStack?.Push(new ChangeConductorPropertiesCommand(conductor, before, after));
                    Sheet.InvalidateVisual();
                };
                colorSub.Items.Add(item);
            }
            menu.Items.Add(colorSub);

            // Submenu "Espessura"
            var widthSub = new MenuItem { Header = "Espessura" };
            var thicknessOptions = new[]
            {
                ("Fino (1.2 mm)", 1.2),
                ("Médio (1.8 mm)", 1.8),
                ("Grosso (2.5 mm)", 2.5)
            };
            foreach (var (label, mm) in thicknessOptions)
            {
                var item = new MenuItem { Header = label, Tag = mm };
                item.Click += (_, _) =>
                {
                    var before = ConductorInfoSnapshot.Of(conductor);
                    conductor.Width = mm;
                    var after = ConductorInfoSnapshot.Of(conductor);
                    Sheet.UndoStack?.Push(new ChangeConductorPropertiesCommand(conductor, before, after));
                    Sheet.InvalidateVisual();
                };
                widthSub.Items.Add(item);
            }
            menu.Items.Add(widthSub);
        }

        if (dataContext is not null)
        {
            // Isolar UMA peça do conjunto (para apagar/editar só o rótulo que veio junto na cópia).
            // Mesmo efeito do Ctrl+clique, aqui para ficar descobrível pelo botão direito.
            var isolateItem = new MenuItem { Header = "Selecionar somente esta peça (sair do grupo)" };
            isolateItem.Click += (_, _) =>
            {
                Sheet.SelectOnly(obj);
                Sheet.NotifySelectionChanged();
                _viewModel.StatusMessage =
                    "Somente esta peça selecionada — pode apagar (Delete) ou editar só ela.";
            };
            menu.Items.Add(isolateItem);
            menu.Items.Add(new Separator());

            menu.Items.Add(new MenuItem { Header = "Recortar", Command = dataContext.CutCommand });
            menu.Items.Add(new MenuItem { Header = "Copiar", Command = dataContext.CopyCommand });
            menu.Items.Add(new MenuItem { Header = "Colar", Command = dataContext.PasteCommand });
            menu.Items.Add(new MenuItem { Header = "Colar aqui", Command = dataContext.PasteHereCommand });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Excluir", Command = dataContext.DeleteSelectionCommand });

            // Atalhos rápidos (o submenu fica logo abaixo para as demais rotações).
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "Girar 90° (conjunto)",
                Command = dataContext.RotateSelectionCommand
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Separar rótulos do objeto",
                Command = dataContext.SplitSelectedShapesCommand
            });
            var groupItem = new MenuItem { Header = "Agrupar seleção (símbolo)" };
            groupItem.Click += (_, _) =>
            {
                _viewModel.StatusMessage = Sheet.GroupSelection()
                    ? "Seleção agrupada como símbolo."
                    : "Selecione ao menos 2 primitivas para agrupar.";
            };
            menu.Items.Add(groupItem);
            var ungroupItem = new MenuItem { Header = "Desagrupar seleção" };
            ungroupItem.Click += (_, _) =>
            {
                _viewModel.StatusMessage = Sheet.UngroupSelection()
                    ? "Seleção desagrupada."
                    : "Nada para desagrupar (a seleção não pertence a um grupo).";
            };
            menu.Items.Add(ungroupItem);

            // Submenu "Rotacionar": 4 rotações fixas + ângulo livre + rotação com o mouse
            var rotateSub = new MenuItem { Header = "Rotacionar" };
            foreach (var angle in new[] { 0, 90, 180, 270 })
            {
                var item = new MenuItem { Header = $"{angle}°", Tag = angle };
                item.Click += OnRotateFixed;
                rotateSub.Items.Add(item);
            }
            rotateSub.Items.Add(new Separator());
            var freeItem = new MenuItem { Header = "Ângulo livre..." };
            freeItem.Click += OnRotateSelection;
            rotateSub.Items.Add(freeItem);
            rotateSub.Items.Add(new Separator());
            var mouseRot = new MenuItem { Header = "Rotacionar com o mouse" };
            mouseRot.IsCheckable = true;
            mouseRot.IsChecked = Sheet.IsMouseRotationEnabled;
            mouseRot.Click += (_, _) =>
            {
                Sheet.IsMouseRotationEnabled = mouseRot.IsChecked;
                _viewModel.StatusMessage = mouseRot.IsChecked
                    ? "Rotação com o mouse ativada. Arraste um elemento selecionado para girá-lo."
                    : "Rotação com o mouse desativada.";
            };
            rotateSub.Items.Add(mouseRot);
            menu.Items.Add(rotateSub);

            // Submenu "Espelhar"
            var mirrorSub = new MenuItem { Header = "Espelhar" };
            var mirrorH = new MenuItem { Header = "Horizontalmente" };
            mirrorH.Click += (_, _) => dataContext.MirrorSelectionHorizontalCommand.Execute(null);
            mirrorSub.Items.Add(mirrorH);
            var mirrorV = new MenuItem { Header = "Verticalmente" };
            mirrorV.Click += (_, _) => dataContext.MirrorSelectionVerticalCommand.Execute(null);
            mirrorSub.Items.Add(mirrorV);
            menu.Items.Add(mirrorSub);

            // Submenu "Ordem" (z-order): trazer/enviar para frente e trás
            var orderSub = new MenuItem { Header = "Ordem" };
            orderSub.Items.Add(new MenuItem { Header = "Trazer para a frente", Command = dataContext.BringToFrontCommand });
            orderSub.Items.Add(new MenuItem { Header = "Trazer adiante", Command = dataContext.RaiseCommand });
            orderSub.Items.Add(new MenuItem { Header = "Enviar para trás", Command = dataContext.LowerCommand });
            orderSub.Items.Add(new MenuItem { Header = "Enviar para o fundo", Command = dataContext.SendToBackCommand });
            menu.Items.Add(orderSub);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Abre a janela de propriedades/edição de um texto independente e aplica as
    /// alterações com undo (fonte, tamanho, cor, formatação, alinhamento e rotação).
    /// </summary>
    private void OnEditTextItem(TextItem t)
    {
        var dlg = new TextItemWindow(t) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.TextItem is { } updated)
        {
            var before = TextItemSnapshot.Of(t);
            t.Text = updated.Text;
            t.FontFamily = updated.FontFamily;
            t.FontSizePt = updated.FontSizePt;
            t.Color = updated.Color;
            t.Bold = updated.Bold;
            t.Italic = updated.Italic;
            t.Underline = updated.Underline;
            t.Strikethrough = updated.Strikethrough;
            t.Rotation = updated.Rotation;
            t.TextAlign = updated.TextAlign;
            var after = TextItemSnapshot.Of(t);
            Sheet.UndoStack?.Push(new ChangeTextItemPropertiesCommand(t, before, after));
            Sheet.InvalidateVisual();
        }
    }

    private void OnEmptySpacePropertiesRequested()
    {
        // Clique direito em área em branco da folha → menu de contexto com a
        // opção "Propriedades da folha..." e operações de grade.
        var menu = new ContextMenu();
        var props = new MenuItem { Header = "Propriedades da folha..." };
        props.Click += (_, _) =>
        {
            if (_viewModel.ActivePage is { } page)
                OnDiagramPropertiesRequested(page);
        };
        menu.Items.Add(props);
        menu.Items.Add(new Separator());
        var addCol = new MenuItem { Header = "Adicionar coluna à grade" };
        addCol.Click += OnAddGridColumn;
        menu.Items.Add(addCol);
        var remCol = new MenuItem { Header = "Remover coluna da grade" };
        remCol.Click += OnRemoveGridColumn;
        menu.Items.Add(remCol);
        var addRow = new MenuItem { Header = "Adicionar linha à grade" };
        addRow.Click += OnAddGridRow;
        menu.Items.Add(addRow);
        var remRow = new MenuItem { Header = "Remover linha da grade" };
        remRow.Click += OnRemoveGridRow;
        menu.Items.Add(remRow);
        menu.IsOpen = true;
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        var obj = Sheet.SelectedObject;
        _viewModel.UpdateSelectedObject(obj);
        var selTexts = Sheet.Page?.Objects.OfType<TextItem>().Count(t => t.IsSelected) ?? 0;
        _viewModel.HasSelection = Sheet.SelectedElements.Count > 0 || Sheet.SelectedConductor is not null || selTexts > 0;
        SyncTextFormattingToolbar(obj);
    }

    private void SyncTextFormattingToolbar(DocumentObject? obj)
    {
        // Com vários textos selecionados, mostra as propriedades do primeiro deles;
        // sem seleção, mostra o padrão que será usado no próximo texto.
        var selected = SelectedTextItems();
        if (obj is TextItem single && selected.Count == 0)
            selected.Add(single);

        if (selected.Count > 0)
        {
            var t = selected[0];
            if (BtnBold is not null) BtnBold.IsChecked = t.Bold;
            if (BtnItalic is not null) BtnItalic.IsChecked = t.Italic;
            if (BtnUnderline is not null) BtnUnderline.IsChecked = t.Underline;
            if (TxtFontSize is not null) TxtFontSize.Text = t.FontSizePt.ToString("0.#");
        }
        else
        {
            if (BtnBold is not null) BtnBold.IsChecked = false;
            if (BtnItalic is not null) BtnItalic.IsChecked = false;
            if (BtnUnderline is not null) BtnUnderline.IsChecked = false;
            if (TxtFontSize is not null)
                TxtFontSize.Text = _viewModel.DrawingProperties.SelectedFontSize.ToString("0.#");
        }
    }

    private void SyncShapeFormatPopup(DocumentObject? obj)
    {
        // No longer used — style flyout is on the toolbar now
    }

    private void UpdateShapeFormatHighlights(ShapeItem s) { }

    private void ApplyShapeDashStyle(Core.Documents.LineDashStyle style)
    {
        // If a shape is selected, change its style; otherwise change the drawing tool default
        var selectedShape = Sheet.Page?.Objects.OfType<ShapeItem>().FirstOrDefault(s => s.IsSelected);
        if (selectedShape is { } shape)
        {
            var before = Core.Undo.ShapeItemSnapshot.Of(shape);
            shape.DashStyle = style;
            var after = Core.Undo.ShapeItemSnapshot.Of(shape);
            Sheet.UndoStack?.Push(new Core.Undo.ChangeShapePropertiesCommand(shape, before, after));
            Sheet.InvalidateVisual();
        }
        else
        {
            _viewModel.DrawingProperties.SelectedDashStyle = style;
        }
        SyncDrawToolStyleHighlights();
    }

    private void ApplyShapeThickness(double width)
    {
        var selectedShape = Sheet.Page?.Objects.OfType<ShapeItem>().FirstOrDefault(s => s.IsSelected);
        if (selectedShape is { } shape)
        {
            var before = Core.Undo.ShapeItemSnapshot.Of(shape);
            shape.PenWidth = width;
            var after = Core.Undo.ShapeItemSnapshot.Of(shape);
            Sheet.UndoStack?.Push(new Core.Undo.ChangeShapePropertiesCommand(shape, before, after));
            Sheet.InvalidateVisual();
        }
        else
        {
            _viewModel.DrawingProperties.SelectedPenWidth = width;
        }
        SyncDrawToolStyleHighlights();
    }

    private void SyncDrawToolStyleHighlights()
    {
        var dash = _viewModel.DrawingProperties.SelectedDashStyle;
        var thick = _viewModel.DrawingProperties.SelectedPenWidth;
        void SetBg(System.Windows.Controls.Button? btn, bool active)
            => btn!.Background = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightBlue)
                : System.Windows.Media.Brushes.Transparent;
        SetBg(BtnStyleSolid, dash == Core.Documents.LineDashStyle.Solid);
        SetBg(BtnStyleDashed, dash == Core.Documents.LineDashStyle.Dashed);
        SetBg(BtnStyleDotted, dash == Core.Documents.LineDashStyle.Dotted);
        SetBg(BtnStyleDashDot, dash == Core.Documents.LineDashStyle.DashDot);
        SetBg(BtnThickThin, Math.Abs(thick - 0.07) < 0.01);
        SetBg(BtnThickDefault, Math.Abs(thick - 0.29) < 0.01);
        SetBg(BtnThickThick, Math.Abs(thick - 1.0) < 0.01);
    }

    /// <summary>
    /// Mostra/esconde o painel de estilo baseado no estado da ferramenta de desenho.
    /// Chamado pelos handlers de toggle das ferramentas de desenho.
    /// </summary>
    private void ShowDrawToolStyleFlyout()
    {
        DrawToolStyleFlyout.Visibility = Visibility.Visible;
        SyncDrawToolStyleHighlights();
    }

    private void HideDrawToolStyleFlyout()
    {
        DrawToolStyleFlyout.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Fecha o flyout de estilo quando clica fora dele (no canvas, em outro botão, etc.).
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Se o clique foi dentro do flyout ou da barra de estilo, não fecha
        if (DrawToolStyleFlyout.IsMouseOver)
            return;

        // Se o clique foi em um botão de ferramenta de desenho, deixa o handler do botão decidir
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ToggleButton tb)
            {
                var tip = tb.ToolTip?.ToString() ?? "";
                if (tip.Contains("linha") || tip.Contains("retângulo") || tip.Contains("elipse") ||
                    tip.Contains("triângulo") || tip.Contains("arco") || tip.Contains("Régua") ||
                    tip.Contains("arredondado"))
                    return;
            }
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        HideDrawToolStyleFlyout();
    }

    /// <summary>
    /// Fecha o flyout de estilo com a tecla Escape.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DrawToolStyleFlyout.Visibility == Visibility.Visible)
        {
            HideDrawToolStyleFlyout();
            e.Handled = true;
            return;
        }

        // Ctrl+G agrupa a seleção como um símbolo; Ctrl+Shift+G desagrupa.
        if (e.Key == Key.G && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var ungroup = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var ok = ungroup ? Sheet.UngroupSelection() : Sheet.GroupSelection();
            _viewModel.StatusMessage = ok
                ? (ungroup ? "Seleção desagrupada." : "Seleção agrupada como símbolo.")
                : (ungroup
                    ? "Nada para desagrupar (a seleção não pertence a um grupo)."
                    : "Selecione ao menos 2 primitivas para agrupar.");
            e.Handled = true;
        }
    }

    /// <summary>
    /// Quando a ferramenta de desenho muda (ex: Escape no canvas), sincroniza o flyout.
    /// </summary>
    private void OnDrawToolChanged(object? sender, EventArgs e)
    {
        if (Sheet.DrawTool == Controls.InsertTool.None)
            HideDrawToolStyleFlyout();
        else
            ShowDrawToolStyleFlyout();
    }

    private void OnDrawToolStyleSolid(object sender, RoutedEventArgs e)
        => ApplyShapeDashStyle(Core.Documents.LineDashStyle.Solid);
    private void OnDrawToolStyleDashed(object sender, RoutedEventArgs e)
        => ApplyShapeDashStyle(Core.Documents.LineDashStyle.Dashed);
    private void OnDrawToolStyleDotted(object sender, RoutedEventArgs e)
        => ApplyShapeDashStyle(Core.Documents.LineDashStyle.Dotted);
    private void OnDrawToolStyleDashDot(object sender, RoutedEventArgs e)
        => ApplyShapeDashStyle(Core.Documents.LineDashStyle.DashDot);
    private void OnDrawToolThickThin(object sender, RoutedEventArgs e)
        => ApplyShapeThickness(0.07);
    private void OnDrawToolThickDefault(object sender, RoutedEventArgs e)
        => ApplyShapeThickness(0.29);
    private void OnDrawToolThickThick(object sender, RoutedEventArgs e)
        => ApplyShapeThickness(1.0);

    private void ShapeFormatPopup_Closed(object sender, EventArgs e)
    {
    }

    private void OnConfigRequested()
    {
        var dialog = new ConfigurationWindow
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.ApplyGridSettings();
            _viewModel.StatusMessage = "Configurações salvas.";
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.RestoreWindowSettings(out var left, out var top, out var width, out var height, out var maximized);
        if (left >= 0 && top >= 0)
        {
            Left = left;
            Top = top;
        }
        if (width > 0 && height > 0)
        {
            Width = width;
            Height = height;
        }
        if (maximized)
            WindowState = WindowState.Maximized;

        // Abre o arquivo inicial ou cria workspace em branco DEPOIS que o viewport já tem tamanho,
        // para que FitToPage() funcione corretamente e a folha apareça na tela.
        if (!string.IsNullOrWhiteSpace(_initialFile))
            _viewModel.OpenInitialFile(_initialFile);
        else
            _viewModel.OpenInitialFile(null);

        // Re-ajusta a folha para o tamanho final após redimensionamento/maximização.
        Dispatcher.BeginInvoke(new Action(Sheet.FitToPage), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnPrintRequested()
    {
        var settings = AppSettingsService.Default.Load();
        var dialog = new PrintWindow(_viewModel.Project, settings)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
            _viewModel.StatusMessage = "Impressão concluída.";
    }

    private void OnExportRequested()
    {
        var settings = AppSettingsService.Default.Load();
        var dialog = new ExportWindow(_viewModel.Project, settings)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
            _viewModel.StatusMessage = "Exportação concluída.";
    }

    private void OnPdfExportRequested()
    {
        if (_viewModel.Project is null)
            return;
        var dialog = new PdfExportWindow(_viewModel.Project)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
            _viewModel.StatusMessage = "PDF exportado.";
    }

    private void OnElementTriageRequested()
    {
        var dialog = new ElementTriageWindow(GetSelectedShapes)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    /// <summary>Formas atualmente selecionadas na folha ativa (para marcar/triar).</summary>
    private IReadOnlyList<OperCAD.Core.Documents.ShapeItem> GetSelectedShapes()
        => _viewModel.ActivePage?.Objects
            .OfType<OperCAD.Core.Documents.ShapeItem>()
            .Where(s => s.IsSelected)
            .ToList() ?? new List<OperCAD.Core.Documents.ShapeItem>();

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        // Close() deve passar pelo OnClosing para perguntar sobre alterações não salvas.
        _allowClose = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
        {
            // Este é o fechamento já aprovado. Nunca cancele aqui: cancelar o segundo
            // Closing deixa a janela em estado inconsistente e permite nova reentrada.
            base.OnClosing(e);
            return;
        }

        _viewModel.SaveWindowSettings(Left, Top, Width, Height, WindowState == WindowState.Maximized);
        if (_allowClose)
        {
            _isClosing = true;
            _viewModel.StopAutoSave();
            TrayIconHelper.NotifyWindowClosing();
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        _viewModel.ConfirmClose();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _closeRequestPending = false;
        _viewModel.StopAutoSave();
        TrayIconHelper.NotifyWindowClosing();
        base.OnClosed(e);
    }

    /// <summary>Categoria selecionada na árvore da paleta → atualiza a lista de elementos.</summary>
    private void OnPaletteCategorySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.ElementCategoryNode node)
            _viewModel.Palette.SelectedCategory = node;
    }

    /// <summary>Botão de classe clicado na barra de classes → seleciona a classe e mostra seus elementos.</summary>
    private void OnClassButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ViewModels.ElementClassViewModel classVm)
            _viewModel.ElementToolbar.SelectedClass = classVm;
    }

    /// <summary>Seletor de modo → "Elétrica" (Elétrico + Lógica).</summary>
    private void OnEletricaModeClick(object sender, RoutedEventArgs e)
        => _viewModel.ElementToolbar.SelectedMode = ViewModels.ElementMode.Eletrica;

    /// <summary>Seletor de modo → "Fluidos" (Pneumático + Hidráulico).</summary>
    private void OnFluidosModeClick(object sender, RoutedEventArgs e)
        => _viewModel.ElementToolbar.SelectedMode = ViewModels.ElementMode.Fluidos;

    /// <summary>MouseButtonDown em elemento da barra → registra o início (clique ou arrasto).</summary>
    private void OnElementDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ViewModels.ElementToolbarItemViewModel item)
            return;

        // Ferramenta especial "Condutor": ativa a ferramenta de desenho de conexão
        // imediatamente (crosshair), em vez de arrastar um símbolo físico.
        if (item.IsConductorTool)
        {
            _viewModel.ElementToolbar.RequestConductorTool();
            e.Handled = true;
            return;
        }

        _barMouseDownPos = e.GetPosition(fe);
        _barDragSource = fe;
        _barDragStarted = false;
    }

    /// <summary>MouseMove na barra → inicia o arrasto (DnD) se o mouse sair do lugar.</summary>
    private void OnElementBarMove(object sender, MouseEventArgs e)
    {
        if (_barDragSource is null || _barDragStarted)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(_barDragSource);
        if (Math.Abs(pos.X - _barMouseDownPos.X) + Math.Abs(pos.Y - _barMouseDownPos.Y) < 5)
            return;
        _barDragStarted = true;
        var fe = _barDragSource;
        var item = fe.Tag as ViewModels.ElementToolbarItemViewModel;
        _barDragSource = null;
        if (item?.Definition is { } def)
        {
            var data = new DataObject("ElementDefinition", def);
            DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
        }
    }

    /// <summary>
    /// Soltar o mouse sem arrastar = CLIQUE na barra → ativa o fantasma vermelho
    /// (fluxo CADe-SIMU: clicar no símbolo da barra faz ele aparecer em vermelho
    /// na folha, acompanhar o mouse e ser posicionado com o clique).
    /// </summary>
    private void OnElementBarClick(object sender, MouseButtonEventArgs e)
    {
        if (_barDragStarted)
            return; // foi um arrasto, não um clique
        if (sender is not FrameworkElement fe || fe.Tag is not ViewModels.ElementToolbarItemViewModel item)
            return;
        if (item.IsConductorTool)
            return; // já tratado no MouseDown
        _barDragSource = null;
        _viewModel.ElementToolbar.InsertElementCommand.Execute(item);
    }

    /// <summary>Folha selecionada na árvore do projeto → troca a folha ativa.</summary>
    private void OnProjectPageSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var page = e.NewValue switch
        {
            OperCAD.Core.Documents.Page p => p,
            ViewModels.PageTreeItemViewModel node => node.Page,
            _ => null
        };
        if (page is null)
            return;
        var idx = _viewModel.Pages.IndexOf(page);
        if (idx >= 0)
            _viewModel.ActivePageIndex = idx;
    }

    /// <summary>Seleção na árvore da aba "Elementos" → clicar num elemento insere-o.</summary>
    private void OnPaletteTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.PaletteTreeNode { Element: not null } node)
            _viewModel.Palette.SelectedElement = node.Element;
    }

    // ── Arrasto na árvore de elementos ──────────────────────────────────
    private Point _paletteMouseDownPos;
    private bool _paletteDragStarted;

    /// <summary>MouseDown na árvore → prepara início de arrasto se for elemento.</summary>
    private void OnPaletteTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe) return;
        var treeViewItem = FindVisualAncestor<System.Windows.Controls.TreeViewItem>(fe);
        if (treeViewItem?.DataContext is not PaletteTreeNode { Element: not null } node) return;
        _paletteMouseDownPos = e.GetPosition(PaletteTreeView);
        _paletteDragStarted = false;
    }

    /// <summary>MouseMove na árvore → inicia DnD se arrastou um elemento.</summary>
    private void OnPaletteTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (_paletteDragStarted || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(PaletteTreeView);
        if (Math.Abs(pos.X - _paletteMouseDownPos.X) + Math.Abs(pos.Y - _paletteMouseDownPos.Y) < 5) return;

        // Encontra o TreeViewItem sob o cursor e verifica se é um elemento
        var hit = e.OriginalSource as DependencyObject;
        var treeViewItem = FindVisualAncestor<System.Windows.Controls.TreeViewItem>(hit);
        if (treeViewItem?.DataContext is not PaletteTreeNode { Element: { Definition: var def } }) return;

        _paletteDragStarted = true;
        var data = new DataObject("ElementDefinition", def);
        DragDrop.DoDragDrop(PaletteTreeView, data, DragDropEffects.Copy);
    }

    /// <summary>Clique direito sobre uma aba de folha → menu de contexto (renomear/excluir).</summary>
    private void OnSheetTabRightClick(object sender, MouseButtonEventArgs e)
    {
        var tab = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (tab?.DataContext is not Page page)
            return;

        var menu = new ContextMenu { PlacementTarget = tab };
        var rename = new MenuItem { Header = "Renomear folha..." };
        rename.Click += (_, _) => RenamePage(page);
        var del = new MenuItem { Header = "Excluir folha" };
        del.Click += (_, _) => _viewModel.DeletePage(page);
        menu.Items.Add(rename);
        menu.Items.Add(del);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RenamePage(Page page)
    {
        var dialog = new PageRenameDialog(page.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.PageName))
            _viewModel.RenamePage(page, dialog.PageName);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T t)
                return t;
            source = source switch
            {
                System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D =>
                    System.Windows.Media.VisualTreeHelper.GetParent(source),
                _ => System.Windows.LogicalTreeHelper.GetParent(source)
            };
        }
        return null;
    }

    /// <summary>Campo da legenda editado → atualiza a folha desenhada.</summary>
    private void OnFolhaEdited(object sender, RoutedEventArgs e) => _viewModel.RefreshSheet();

    private void OnToggleFullScreen(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _viewModel.StatusMessage = "Tela cheia ativada (F11 para sair).";
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            _viewModel.StatusMessage = "Tela cheia desativada.";
        }
    }

    private void OnToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSelectionMode = true;
        Sheet.IsReadOnly = false;
        _viewModel.StatusMessage = "Modo seleção ativado.";
    }

    private void OnToggleVisualizationMode(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSelectionMode = false;
        Sheet.IsReadOnly = true;
        _viewModel.StatusMessage = "Modo visualização ativado.";
    }

    private void OnHelpClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ManualWindow
        {
            Owner = this
        };
        dialog.Show();
    }

    private void OnSearchRequested()
    {
        if (_viewModel.ActivePage is null)
        {
            _viewModel.StatusMessage = "Abra um projeto primeiro.";
            return;
        }
        var dialog = new SearchReplaceWindow(_viewModel.ActivePage)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnProjectPropertiesRequested()
    {
        var dialog = new ProjectPropertiesWindow(_viewModel.Project)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.MarkModified();
            _viewModel.StatusMessage = "Propriedades do projeto atualizadas.";
        }
    }

    private void OnTitleBlockEditorRequested()
    {
        var templates = TitleBlockTemplateCatalog.Default.LoadAll();
        var template = templates.Count > 0 ? templates[0] : new OperCAD.Core.Documents.TitleBlockTemplate { Name = "Nova legenda" };
        var dialog = new TitleBlockEditorWindow(template)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnPotentialSelectorRequested()
    {
        var colors = new List<string>();
        foreach (var page in _viewModel.Project.Pages)
        {
            foreach (var obj in page.Objects)
            {
                if (obj is OperCAD.Core.Documents.Conductor c && !string.IsNullOrEmpty(c.Color))
                {
                    if (!colors.Contains(c.Color))
                        colors.Add(c.Color);
                }
            }
        }
        if (colors.Count == 0)
        {
            _viewModel.StatusMessage = "Nenhuma cor de condutor encontrada no projeto.";
            return;
        }
        var dialog = new PotentialSelectorWindow(colors)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.StatusMessage = $"Mesclar potencial: {dialog.SelectedSource} → {dialog.SelectedTarget}";
        }
    }

    private void OnAutoNumberFormulaRequested()
    {
        var formula = new OperCAD.Application.LabelFormula();
        formula.Parts.Add(new OperCAD.Application.LabelFormulaPart
        {
            Type = OperCAD.Application.LabelPartType.Prefix,
            FixedText = "K"
        });
        formula.Parts.Add(new OperCAD.Application.LabelFormulaPart
        {
            Type = OperCAD.Application.LabelPartType.Counter,
            Format = "0"
        });
        ProjectReports.RenumberWithFormula(_viewModel.Project, formula);
        _viewModel.RefreshSheet();
        _viewModel.MarkModified();
        _viewModel.StatusMessage = "Elementos renumerados com fórmula.";
    }

    private void OnStatusElementNameChanged(string? name)
    {
        if (name is not null)
            _viewModel.Palette.LocateInPalette(name);
    }

    private void OnToggleFullScreenFromCommand()
    {
        if (WindowState == WindowState.Normal)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _viewModel.StatusMessage = "Tela cheia ativada (F11 para sair).";
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            _viewModel.StatusMessage = "Tela cheia desativada.";
        }
    }

    private void OnAboutClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnUndoItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedIndex >= 0)
            _viewModel.UndoToIndexCommand.Execute(lb.SelectedIndex);
    }

    private void OnAddGridColumn(object sender, RoutedEventArgs e)
    {
        Sheet.AddGridColumn();
        _viewModel.RefreshSheet();
        _viewModel.MarkModified();
        _viewModel.StatusMessage = "Coluna adicionada à grade.";
    }

    private void OnRemoveGridColumn(object sender, RoutedEventArgs e)
    {
        Sheet.RemoveGridColumn();
        _viewModel.RefreshSheet();
        _viewModel.MarkModified();
        _viewModel.StatusMessage = "Coluna removida da grade.";
    }

    private void OnAddGridRow(object sender, RoutedEventArgs e)
    {
        Sheet.AddGridRow();
        _viewModel.RefreshSheet();
        _viewModel.MarkModified();
        _viewModel.StatusMessage = "Linha adicionada à grade.";
    }

    private void OnRemoveGridRow(object sender, RoutedEventArgs e)
    {
        Sheet.RemoveGridRow();
        _viewModel.RefreshSheet();
        _viewModel.MarkModified();
        _viewModel.StatusMessage = "Linha removida da grade.";
    }

    private void OnGroupSelectedTexts(object sender, RoutedEventArgs e)
    {
        Sheet.GroupSelectedTexts();
        _viewModel.RefreshSheet();
        _viewModel.MarkModified();
        _viewModel.StatusMessage = "Textos selecionados agrupados.";
    }

    private void OnFormulaEditorRequested()
    {
        var variables = _viewModel.Project.Variables.Keys.ToList();
        var dialog = new FormulaEditorWindow(variables)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.StatusMessage = $"Fórmula: {dialog.ResultFormula}";
        }
    }

    private void ShowRotateDialog()
    {
        var dialog = new RotateDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Sheet.RotateSelectionTo(dialog.AngleDegrees);
            _viewModel.StatusMessage = $"Seleção rotacionada para {dialog.AngleDegrees}°.";
        }
    }

    private void OnRotateSelection(object sender, RoutedEventArgs e) => ShowRotateDialog();

    /// <summary>Rotação fixa do submenu "Rotacionar" (0°/90°/180°/270°, absoluta).</summary>
    private void OnRotateFixed(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: int angle })
        {
            Sheet.RotateSelectionTo(angle);
            _viewModel.StatusMessage = $"Seleção rotacionada para {angle}°.";
        }
    }

    /// <summary>DragOver na folha → permite soltar se for um ElementDefinition.</summary>
    private void OnSheetDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ElementDefinition")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Drop na folha → insere o elemento arrastado da barra de classes.</summary>
    private void OnSheetDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("ElementDefinition") &&
            e.Data.GetData("ElementDefinition") is ElementDefinition def)
        {
            // ⚠ FIX DnD: NÃO usar e.GetPosition(Sheet) — o DragEventArgs.GetPosition do WPF
            // devolve coordenada relativa à janela (deslocada pela posição do Sheet na janela),
            // fazendo o elemento cair longe do mouse. Usa-se a posição FÍSICA do cursor
            // (GetCursorPos) convertida para o canvas — a fonte da verdade.
            _viewModel.InsertElementAtCursor(def);
        }
        e.Handled = true;
    }

    #region Text formatting toolbar

    private void OnFontSizeIncrease(object sender, RoutedEventArgs e) => StepFontSize(+1);

    private void OnFontSizeDecrease(object sender, RoutedEventArgs e) => StepFontSize(-1);

    /// <summary>
    /// Passo de 1 pt sobre a seleção: cada texto selecionado cresce/diminui de 1 em 1,
    /// preservando as diferenças entre eles. Sem seleção, muda o padrão do próximo texto.
    /// O desenho é atualizado na hora e a alteração pode ser desfeita (Ctrl+Z).
    /// </summary>
    private void StepFontSize(int delta)
    {
        var texts = SelectedTextItems();
        if (texts.Count == 0)
        {
            SetDefaultFontSize(_viewModel.DrawingProperties.SelectedFontSize + delta);
            return;
        }

        ApplyFontSize(texts, t => t.FontSizePt + delta);
    }

    private void OnFontSizeChanged(object sender, RoutedEventArgs e)
    {
        if (TxtFontSize is null ||
            !double.TryParse(TxtFontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size))
            return;

        // Se houver uma caixa de legenda em edição inline, aplica o novo tamanho a ela também
        Sheet.UpdateInlineTitleBlockStyle(fontSizePt: size, bold: null, italic: null, fontFamily: null);

        var texts = SelectedTextItems();
        if (texts.Count == 0)
            SetDefaultFontSize(size);
        else
            ApplyFontSize(texts, _ => size);
    }

    private void OnFontSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            OnFontSizeChanged(sender, e);
            e.Handled = true;
        }
    }

    private List<TextItem> SelectedTextItems()
        => Sheet.Page?.Objects.OfType<TextItem>().Where(t => t.IsSelected).ToList() ?? new List<TextItem>();

    /// <summary>Sem seleção: o tamanho digitado vira o padrão do próximo texto.</summary>
    private void SetDefaultFontSize(double sizePt)
    {
        var clamped = Math.Clamp(sizePt, TextItem.MinFontSizePt, TextItem.MaxFontSizePt);
        _viewModel.DrawingProperties.SelectedFontSize = clamped;
        if (TxtFontSize is not null)
            TxtFontSize.Text = clamped.ToString("0.#");
    }

    /// <summary>Aplica um novo corpo aos textos selecionados, com undo e redesenho imediato.</summary>
    private void ApplyFontSize(List<TextItem> texts, Func<TextItem, double> next)
    {
        var commands = new List<IUndoableCommand>();
        foreach (var t in texts)
        {
            var before = TextItemSnapshot.Of(t);
            t.FontSizePt = Math.Clamp(next(t), TextItem.MinFontSizePt, TextItem.MaxFontSizePt);
            var after = TextItemSnapshot.Of(t);
            if (after != before)
                commands.Add(new ChangeTextItemPropertiesCommand(t, before, after));
        }

        if (commands.Count == 0)
            return;

        Sheet.UndoStack?.Push(commands.Count == 1
            ? commands[0]
            : new CompositeCommand("Tamanho da fonte", commands));

        var shown = texts[0].FontSizePt;
        _viewModel.DrawingProperties.SelectedFontSize = shown;
        if (TxtFontSize is not null)
            TxtFontSize.Text = shown.ToString("0.#");

        Sheet.InvalidateVisual();
        _viewModel.MarkModified();
    }

    private void OnToggleBold(object sender, RoutedEventArgs e)
        => ApplyTextFlag(BtnBold?.IsChecked == true, (t, v) => t.Bold = v);

    private void OnToggleItalic(object sender, RoutedEventArgs e)
        => ApplyTextFlag(BtnItalic?.IsChecked == true, (t, v) => t.Italic = v);

    private void OnToggleUnderline(object sender, RoutedEventArgs e)
        => ApplyTextFlag(BtnUnderline?.IsChecked == true, (t, v) => t.Underline = v);

    /// <summary>Aplica um estilo (negrito/itálico/sublinhado) a todos os textos selecionados.</summary>
    private void ApplyTextFlag(bool value, Action<TextItem, bool> setter)
    {
        var commands = new List<IUndoableCommand>();
        foreach (var t in SelectedTextItems())
        {
            var before = TextItemSnapshot.Of(t);
            setter(t, value);
            var after = TextItemSnapshot.Of(t);
            if (after != before)
                commands.Add(new ChangeTextItemPropertiesCommand(t, before, after));
        }

        if (commands.Count == 0)
            return;

        Sheet.UndoStack?.Push(commands.Count == 1
            ? commands[0]
            : new CompositeCommand("Estilo do texto", commands));
        Sheet.InvalidateVisual();
        _viewModel.MarkModified();
    }

    #endregion

    private void OnDrawingColorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string color)
            _viewModel.DrawingProperties.SelectedColor = color;
    }

    private void OnDrawingMoreColors(object sender, RoutedEventArgs e)
    {
        System.Windows.Media.Color? initialColor = null;
        try
        {
            var colorStr = _viewModel?.DrawingProperties?.SelectedColor;
            if (!string.IsNullOrEmpty(colorStr))
            {
                var brush = new System.Windows.Media.BrushConverter()
                    .ConvertFromString(colorStr)
                    as System.Windows.Media.SolidColorBrush;
                if (brush is not null)
                    initialColor = brush.Color;
            }
        }
        catch { }

        try
        {
            var dlg = new ColorPickerWindow(initialColor) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                var c = dlg.SelectedColor;
                if (_viewModel?.DrawingProperties is not null)
                    _viewModel.DrawingProperties.SelectedColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }
        catch { }
    }

    private void OnDrawingDashStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            var style = tag switch
            {
                "Dashed" => Core.Documents.LineDashStyle.Dashed,
                "Dotted" => Core.Documents.LineDashStyle.Dotted,
                "DashDot" => Core.Documents.LineDashStyle.DashDot,
                _ => Core.Documents.LineDashStyle.Solid
            };
            _viewModel.DrawingProperties.SelectedDashStyle = style;
        }
    }

    /// <summary>
    /// Modo de preenchimento escolhido no painel: "solid" = formas fechadas preenchidas com
    /// a cor escolhida; "none" = vazadas (PADRÃO). Vale para as PRÓXIMAS formas (a ferramenta
    /// lê <c>FillingForNewShapes</c>) e também é aplicado à seleção atual, como as demais
    /// propriedades de desenho.
    /// </summary>
    private void OnDrawingFillingModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb)
            return;

        var filled = (rb.Tag as string) == "solid";
        var props = _viewModel.DrawingProperties;
        props.ToolFilled = filled;
        props.HasFilling = filled;
        props.Filling = filled ? props.FillingColor : "none";
    }

    private void OnDrawingFillingColorClick(object sender, MouseButtonEventArgs e)
    {
        System.Windows.Media.Color? initialColor = null;
        try
        {
            var brush = new System.Windows.Media.BrushConverter()
                .ConvertFromString(_viewModel.DrawingProperties.FillingColor)
                as System.Windows.Media.Brush;
            if (brush is System.Windows.Media.SolidColorBrush solid)
                initialColor = solid.Color;
        }
        catch { }

        var dlg = new ColorPickerWindow(initialColor) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var c = dlg.SelectedColor;
            _viewModel.DrawingProperties.FillingColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Handlers da lista de projetos (painel esquerdo)
    // ══════════════════════════════════════════════════════════════════════

    private void OnProjectListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Duplo-clique num projeto abre-o.
        var vm = GetProjectItemFromSender(e.OriginalSource as DependencyObject);
        if (vm is not null)
            vm.OpenCommand.Execute(null);
        e.Handled = true;
    }
    private void OnProjectListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Seleciona o item sob o cursor e abre o menu de contexto (mesmo padrão
        // do menu da folha: construído em código, sem colunas de ícone/atalho).
        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ProjectListItemViewModel vm)
            return;
        ProjectListBox.SelectedItem = vm;

        var menu = new ContextMenu { PlacementTarget = item };

        var open = new MenuItem { Header = "Abrir" };
        open.Click += (_, _) => vm.OpenCommand.Execute(null);

        var rename = new MenuItem { Header = "Renomear" };
        rename.Click += (_, _) => vm.BeginRenameCommand.Execute(null);

        var delete = new MenuItem { Header = "Excluir" };
        delete.Click += (_, _) => vm.DeleteCommand.Execute(null);

        menu.Items.Add(open);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnProjectListKeyDown(object sender, KeyEventArgs e)
    {
        // Tecla Delete: exclui o projeto selecionado (sempre com confirmação).
        if (e.Key != Key.Delete)
            return;

        // Não excluir enquanto o usuário edita o nome dentro de um TextBox.
        if (Keyboard.FocusedElement is TextBox)
            return;

        if (ProjectListBox.SelectedItem is ProjectListItemViewModel vm)
        {
            vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnProjectRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProjectListItemViewModel vm)
        {
            if (vm.IsEditing)
                vm.CommitRenameCommand.Execute(null);
        }
    }

    private void OnProjectRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ProjectListItemViewModel vm)
            return;

        if (e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNewProjectFromPanel(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectNameWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ProjectName is not null)
        {
            _viewModel.ProjectList.CreateProject(dialog.ProjectName);
        }
    }

    private static ProjectListItemViewModel? GetProjectItemFromSender(DependencyObject? sender)
    {
        if (sender is null)
            return null;

        var current = sender;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.DataContext is ProjectListItemViewModel vm)
                return vm;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

/// <summary>Diálogo simples para renomear uma folha (menu de contexto da aba).</summary>
internal sealed class PageRenameDialog : Window
{
    private readonly TextBox _input;

    public string PageName => _input.Text.Trim();

    public PageRenameDialog(string currentName)
    {
        Title = "Renomear folha";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.White;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var label = new TextBlock
        {
            Text = "Nome da folha:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 10)
        };
        Grid.SetRow(label, 0);
        Grid.SetColumn(label, 0);

        _input = new TextBox { Text = currentName, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(_input, 0);
        Grid.SetColumn(_input, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 1);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);

        var cancel = new Button { Content = "Cancelar", Width = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        grid.Children.Add(label);
        grid.Children.Add(_input);
        grid.Children.Add(buttons);
        Content = grid;

        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
}
