using System.Globalization;
using System.Windows;
using System.Windows.Input;
using OperCAD.Application;
using OperCAD.Core.Elements;
using OperCAD.UI.Controls;

namespace OperCAD.UI.Views;

/// <summary>
/// Editor de elementos WYSIWYG: desenha e edita primitivas, terminais e metadados.
/// Inclui aba de nomes multi-idioma.
/// </summary>
public partial class ElementEditorWindow : Window
{
    private bool _syncing;
    private string? _selectedLanguage;

    public ElementEditorWindow(ElementDefinition? def = null)
    {
        InitializeComponent();
        Canvas.Def = def ?? new ElementDefinition { Name = "novo_elemento" };

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Canvas.Undo()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Canvas.Redo()));
        Canvas.UndoStateChanged += (_, _) => UpdateUndoButtons();
        UpdateUndoButtons();

        var linkTypes = new[]
        {
            new { Label = "Simples (simple)", Value = "simple" },
            new { Label = "Mestre (master)", Value = "master" },
            new { Label = "Escravo (slave)", Value = "slave" },
            new { Label = "Terminal (terminal)", Value = "terminal" }
        };
        CmbLinkType.ItemsSource = linkTypes;
        CmbLinkType.DisplayMemberPath = "Label";

        var orientations = new[]
        {
            new { Label = "Norte (n)", Value = 'n' },
            new { Label = "Leste (e)", Value = 'e' },
            new { Label = "Sul (s)", Value = 's' },
            new { Label = "Oeste (w)", Value = 'w' }
        };
        CmbTermOrientation.ItemsSource = orientations;
        CmbTermOrientation.DisplayMemberPath = "Label";

        Canvas.SelectionChanged += (_, _) => SyncFields();
        SyncFields();
        RefreshLocalizedNames();
        Title = $"Editor de elementos — {ElementNameTranslator.Translate(Canvas.Def.Name)}";
    }

    private void SyncFields()
    {
        _syncing = true;
        var d = Canvas.Def;
        TxtName.Text = d.Name;
        TxtWidth.Text = d.Width.ToString("0.##", CultureInfo.InvariantCulture);
        TxtHeight.Text = d.Height.ToString("0.##", CultureInfo.InvariantCulture);
        TxtHotspotX.Text = d.HotspotX.ToString("0.##", CultureInfo.InvariantCulture);
        TxtHotspotY.Text = d.HotspotY.ToString("0.##", CultureInfo.InvariantCulture);
        foreach (var item in CmbLinkType.Items)
        {
            var value = (string)item.GetType().GetProperty("Value")!.GetValue(item)!;
            if (value == d.LinkType)
            {
                CmbLinkType.SelectedItem = item;
                break;
            }
        }

        if (Canvas.SelectedTerminal >= 0 && Canvas.SelectedTerminal < d.Terminals.Count)
        {
            var t = d.Terminals[Canvas.SelectedTerminal];
            TxtTermName.Text = t.Name;
            foreach (var item in CmbTermOrientation.Items)
            {
                var value = (char)item.GetType().GetProperty("Value")!.GetValue(item)!;
                if (value == t.Orientation)
                {
                    CmbTermOrientation.SelectedItem = item;
                    break;
                }
            }
        }
        _syncing = false;
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        var d = Canvas.Def;
        var name = string.IsNullOrWhiteSpace(TxtName.Text) ? d.Name : TxtName.Text.Trim();
        var width = Parse(TxtWidth.Text, d.Width);
        var height = Parse(TxtHeight.Text, d.Height);
        var hx = Parse(TxtHotspotX.Text, d.HotspotX);
        var hy = Parse(TxtHotspotY.Text, d.HotspotY);
        var linkType = d.LinkType;
        if (CmbLinkType.SelectedItem is not null)
            linkType = (string)CmbLinkType.SelectedItem.GetType().GetProperty("Value")!.GetValue(CmbLinkType.SelectedItem)!;

        Canvas.PushSnapshot();
        Canvas.Def = d with
        {
            Name = name,
            Width = width,
            Height = height,
            HotspotX = hx,
            HotspotY = hy,
            LinkType = linkType,
            Names = new Dictionary<string, string>(d.Names) { ["pt_BR"] = name }
        };
        Title = $"Editor de elementos — {ElementNameTranslator.Translate(Canvas.Def.Name)}";
    }

    private void OnTerminalFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || Canvas.SelectedTerminal < 0 || Canvas.SelectedTerminal >= Canvas.Def.Terminals.Count)
            return;
        var t = Canvas.Def.Terminals[Canvas.SelectedTerminal];
        var orientation = t.Orientation;
        if (CmbTermOrientation.SelectedItem is not null)
            orientation = (char)CmbTermOrientation.SelectedItem.GetType().GetProperty("Value")!.GetValue(CmbTermOrientation.SelectedItem)!;
        Canvas.UpdateSelectedTerminal(new ElementTerminal
        {
            Uuid = t.Uuid,
            Name = string.IsNullOrWhiteSpace(TxtTermName.Text) ? t.Name : TxtTermName.Text.Trim(),
            X = t.X,
            Y = t.Y,
            Orientation = orientation,
            Type = t.Type
        });
    }

    private static double Parse(string text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // ===== Barra de ferramentas =====

    private void OnNew(object sender, RoutedEventArgs e)
    {
        Canvas.PushSnapshot();
        Canvas.Def = new ElementDefinition { Name = "novo_elemento" };
        SyncFields();
        Title = "Editor de elementos — novo_elemento";
    }

    private void OnLine(object sender, RoutedEventArgs e) => Canvas.BeginTool(InsertTool.Line);
    private void OnRect(object sender, RoutedEventArgs e) => Canvas.BeginTool(InsertTool.Rectangle);
    private void OnEllipse(object sender, RoutedEventArgs e) => Canvas.BeginTool(InsertTool.Ellipse);
    private void OnPolygon(object sender, RoutedEventArgs e) => Canvas.BeginTool(InsertTool.Polygon);

    private void OnText(object sender, RoutedEventArgs e)
    {
        Canvas.BeginTool(InsertTool.None);
        var dlg = new TextItemWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            // posição inicial no centro do hotspot
            Canvas.AddPrimitive(new ElementText
            {
                Text = dlg.TextResult,
                X = Canvas.Def.HotspotX,
                Y = Canvas.Def.HotspotY,
                Font = $"Segoe UI,{dlg.FontSizeResult.ToString(CultureInfo.InvariantCulture)},-1,5,50,0,0,0,0,0,Regular"
            });
        }
    }

    private void OnTerminal(object sender, RoutedEventArgs e)
    {
        Canvas.BeginTool(InsertTool.None);
        var dlg = new TerminalEditorWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Canvas.AddTerminal(new ElementTerminal
            {
                Uuid = Guid.NewGuid(),
                Name = dlg.TerminalName,
                X = Canvas.Def.HotspotX,
                Y = Canvas.Def.HotspotY,
                Orientation = dlg.Orientation,
                Type = dlg.Type
            });
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e) => Canvas.DeleteSelection();

    private void OnUndo(object sender, RoutedEventArgs e) => Canvas.Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Canvas.Redo();

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = Canvas.CanUndo;
        BtnRedo.IsEnabled = Canvas.CanRedo;
    }

    private void OnFit(object sender, RoutedEventArgs e) => Canvas.FitToContent();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    #region Nomes Multi-Idioma

    private void RefreshLocalizedNames()
    {
        LstLocalizedNames.ItemsSource = Canvas.Def.LocalizedNames
            .Select(kvp => $"{kvp.Key}: {kvp.Value}")
            .ToList();
    }

    private void OnAddLanguage(object sender, RoutedEventArgs e)
    {
        var langCode = TxtLangCode.Text?.Trim();
        if (string.IsNullOrWhiteSpace(langCode))
        {
            MessageBox.Show("Informe o código do idioma (ex.: pt_BR, en, es, fr).", "Adicionar idioma",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Canvas.Def.LocalizedNames.ContainsKey(langCode))
        {
            MessageBox.Show($"O idioma '{langCode}' já existe.", "Adicionar idioma",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Canvas.PushSnapshot();
        Canvas.Def.LocalizedNames[langCode] = Canvas.Def.Name;
        RefreshLocalizedNames();
        TxtLangCode.Text = "";
    }

    private void OnLocalizedNameSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LstLocalizedNames.SelectedIndex < 0)
        {
            _selectedLanguage = null;
            TxtLocalizedNameValue.Text = "";
            return;
        }

        var items = Canvas.Def.LocalizedNames.ToList();
        if (LstLocalizedNames.SelectedIndex < items.Count)
        {
            _selectedLanguage = items[LstLocalizedNames.SelectedIndex].Key;
            TxtLocalizedNameValue.Text = items[LstLocalizedNames.SelectedIndex].Value;
        }
    }

    private void OnSetLocalizedName(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedLanguage))
        {
            MessageBox.Show("Selecione um idioma na lista.", "Definir nome",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Canvas.PushSnapshot();
        Canvas.Def.LocalizedNames[_selectedLanguage] = TxtLocalizedNameValue.Text ?? string.Empty;
        RefreshLocalizedNames();
    }

    private void OnRemoveLocalizedName(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedLanguage))
        {
            MessageBox.Show("Selecione um idioma na lista para remover.", "Remover idioma",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Canvas.PushSnapshot();
        Canvas.Def.LocalizedNames.Remove(_selectedLanguage);
        _selectedLanguage = null;
        TxtLocalizedNameValue.Text = "";
        RefreshLocalizedNames();
    }

    #endregion
}
