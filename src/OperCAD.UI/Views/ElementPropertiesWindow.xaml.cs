using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OperCAD.Application;
using OperCAD.Core.Documents;
using OperCAD.Core.Elements;

namespace OperCAD.UI.Views;

/// <summary>
/// Diálogo de propriedades do elemento: a seção IDENTIFICAÇÃO permite editar os textos
/// dinâmicos que o elemento possui (etiqueta, tags de terminais e demais informações),
/// exibindo o valor atual de cada um na caixa de edição. Textos dinâmicos "UserText" são
/// sobrescritos APENAS na instância — a definição do elemento não é alterada.
/// </summary>
public partial class ElementPropertiesWindow : Window
{
    private readonly PlacedElement _element;
    private readonly List<TextEntry> _entries = new();

    public ElementPropertiesWindow(PlacedElement element)
    {
        InitializeComponent();
        _element = element;

        ElementName.Text = ElementNameTranslator.Translate(element.Name);
        ElementSubInfo.Text = $"{element.Definition.Width:0} × {element.Definition.Height:0} mm · " +
                              $"{element.Definition.Terminals.Count} terminal(is)";

        TxtLabel.Text = element.Label;
        BuildIdentificationFields();

        TxtLabel.Focus();
    }

    /// <summary>
    /// Gera um campo de edição para cada texto existente no elemento
    /// (etiqueta no topo + um campo por texto dinâmico, com o valor atual).
    /// </summary>
    private void BuildIdentificationFields()
    {
        var prims = _element.Definition.Primitives;
        var seenInfo = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var occurrences = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < prims.Count; i++)
        {
            switch (prims[i])
            {
                case ElementDynamicText dt when dt.TextFrom == "ElementInfo":
                {
                    // A etiqueta (informação "label", ou ElementInfo sem nome) é o campo
                    // "Etiqueta" do topo — não duplicamos aqui.
                    if (string.IsNullOrEmpty(dt.InfoName) ||
                        string.Equals(dt.InfoName, "label", System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    // A mesma informação exibida em vários pontos representa um único valor.
                    if (!seenInfo.Add("E:" + dt.InfoName))
                        continue;

                    var infoName = dt.InfoName;
                    var entry = AddEntry(i, TextKind.ElementInfo, infoName,
                        FieldName(dt, infoName),
                        CurrentInfoValue(dt, infoName),
                        IsMultiline(dt, infoName),
                        occurrences);
                    if (entry is not null)
                        _entries.Add(entry);
                    break;
                }

                case ElementDynamicText dt:
                {
                    // Texto dinâmico UserText (ex.: tags de terminais) — sobrescrita por instância.
                    if (string.IsNullOrEmpty(dt.Text) && string.IsNullOrEmpty(dt.InfoName))
                        continue; // texto invisível e sem nome — nada a identificar

                    var value = _element.GetTextOverride(i) ?? dt.Text;
                    var entry = AddEntry(i, TextKind.UserText, dt.InfoName,
                        FieldName(dt, dt.InfoName),
                        value,
                        multiline: false,
                        occurrences);
                    if (entry is not null)
                        _entries.Add(entry);
                    break;
                }
            }
        }
    }

    private TextEntry? AddEntry(int index, TextKind kind, string infoName, string fieldName,
        string value, bool multiline, Dictionary<string, int> occurrences)
    {
        occurrences.TryGetValue(fieldName, out var n);
        occurrences[fieldName] = n + 1;
        if (n > 0)
            fieldName = $"{fieldName} {n + 1}";

        var box = AddFieldRow(fieldName, value, multiline);
        return new TextEntry(index, kind, infoName, box);
    }

    private static string FieldName(ElementDynamicText dt, string infoName)
    {
        if (dt.TextFrom == "ElementInfo")
        {
            return infoName.ToLowerInvariant() switch
            {
                "comment" => "Comentário",
                "function" => "Função",
                "description" => "Descrição",
                "manufacturer" => "Fabricante",
                "designation" => "Designação",
                "tag" => "Tag",
                _ => Prettify(infoName)
            };
        }

        return infoName.ToLowerInvariant() switch
        {
            "tag" => "Tag",
            _ when !string.IsNullOrWhiteSpace(infoName) => Prettify(infoName),
            _ => "Texto"
        };
    }

    private string CurrentInfoValue(ElementDynamicText dt, string infoName)
        => infoName.ToLowerInvariant() switch
        {
            "comment" => _element.Comment,
            "function" => _element.Function,
            "description" => _element.Description,
            "manufacturer" => _element.Manufacturer,
            "designation" => _element.Designation,
            "label" => _element.Label,
            _ => _element.DynamicTextValues.TryGetValue(infoName, out var v)
                ? v
                : dt.Text ?? string.Empty
        };

    private static bool IsMultiline(ElementDynamicText dt, string infoName)
        => dt.TextFrom == "ElementInfo" &&
           (infoName.ToLowerInvariant() is "comment" or "description");

    private static string Prettify(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Texto";
        var words = name.Split(new[] { '-', '_', '.', ' ' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w);
        return string.Join(" ", words);
    }

    private TextBox AddFieldRow(string label, string value, bool multiline)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("App.FieldLabel"),
            Margin = new Thickness(0, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 0);

        var box = new TextBox
        {
            Style = (Style)FindResource("App.TextBox"),
            Margin = new Thickness(0, 2, 0, 2),
            Text = value ?? string.Empty
        };
        Grid.SetColumn(box, 1);
        if (multiline)
        {
            box.TextWrapping = TextWrapping.Wrap;
            box.AcceptsReturn = true;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.MinHeight = 44;
        }

        grid.Children.Add(text);
        grid.Children.Add(box);
        FieldsPanel.Children.Add(grid);
        return box;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _element.Label = TxtLabel.Text.Trim();

        foreach (var entry in _entries)
        {
            var value = entry.Box.Text ?? string.Empty;
            switch (entry.Kind)
            {
                case TextKind.ElementInfo:
                    switch (entry.InfoName.ToLowerInvariant())
                    {
                        case "comment":
                            _element.Comment = value;
                            break;
                        case "function":
                            _element.Function = value;
                            break;
                        case "description":
                            _element.Description = value;
                            break;
                        case "manufacturer":
                            _element.Manufacturer = value;
                            break;
                        case "designation":
                            _element.Designation = value;
                            break;
                        default:
                            if (string.IsNullOrEmpty(value))
                                _element.DynamicTextValues.Remove(entry.InfoName);
                            else
                                _element.DynamicTextValues[entry.InfoName] = value;
                            break;
                    }
                    break;

                case TextKind.UserText:
                    // Sobrescreve apenas nesta instância — a definição do símbolo não muda.
                    _element.SetTextOverride(entry.PrimitiveIndex, value);
                    break;
            }
        }

        DialogResult = true;
    }

    private enum TextKind
    {
        ElementInfo,
        UserText
    }

    /// <summary>Campo de edição gerado para um texto do elemento.</summary>
    private sealed class TextEntry
    {
        public int PrimitiveIndex { get; }
        public TextKind Kind { get; }
        public string InfoName { get; }
        public TextBox Box { get; }

        public TextEntry(int primitiveIndex, TextKind kind, string infoName, TextBox box)
        {
            PrimitiveIndex = primitiveIndex;
            Kind = kind;
            InfoName = infoName;
            Box = box;
        }
    }
}