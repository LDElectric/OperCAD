using System.Globalization;
using System.Windows;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>Diálogo para adicionar um texto independente à folha (barra Inserir).</summary>
public partial class TextItemWindow : Window
{
    public string TextResult { get; private set; } = string.Empty;
    public double FontSizeResult { get; private set; } = 9;

    /// <summary>Item de texto criado (null se cancelado).</summary>
    public TextItem? TextItem { get; private set; }

    public TextItemWindow()
    {
        InitializeComponent();
    }

    /// <summary>Pré-preenche o diálogo para edição de um texto existente.</summary>
    public TextItemWindow(TextItem existing) : this()
    {
        TxtText.Text = existing.Text;
        TxtFontFamily.Text = existing.FontFamily;
        TxtSize.Text = existing.FontSizePt.ToString("0.#", CultureInfo.InvariantCulture);
        TxtColor.Text = existing.Color;
        ChkBold.IsChecked = existing.Bold;
        ChkItalic.IsChecked = existing.Italic;
        ChkUnderline.IsChecked = existing.Underline;
        ChkStrikethrough.IsChecked = existing.Strikethrough;
        TxtRotation.Text = existing.Rotation.ToString("0.##", CultureInfo.InvariantCulture);

        if (existing.FontSizePt >= 8) TxtSize.Text = existing.FontSizePt.ToString("0.#", CultureInfo.InvariantCulture);

        switch (existing.TextAlign)
        {
            case "center": RdoAlignCenter.IsChecked = true; break;
            case "right": RdoAlignRight.IsChecked = true; break;
            default: RdoAlignLeft.IsChecked = true; break;
        }
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(TxtColor.Text);
            var dlg = new ColorPickerWindow(current);
            if (dlg.ShowDialog() == true)
                TxtColor.Text = $"#{dlg.SelectedColor.R:X2}{dlg.SelectedColor.G:X2}{dlg.SelectedColor.B:X2}";
        }
        catch
        {
            var dlg = new ColorPickerWindow();
            if (dlg.ShowDialog() == true)
                TxtColor.Text = $"#{dlg.SelectedColor.R:X2}{dlg.SelectedColor.G:X2}{dlg.SelectedColor.B:X2}";
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = TxtText.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Digite o texto a inserir.", "Adicionar texto",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var size = 9.0;
        if (double.TryParse(TxtSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            parsed is >= 1 and <= 200)
        {
            size = parsed;
        }

        var rotation = 0.0;
        if (double.TryParse(TxtRotation.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
            rotation = r;

        var align = "left";
        if (RdoAlignCenter.IsChecked == true) align = "center";
        else if (RdoAlignRight.IsChecked == true) align = "right";

        var fontFamily = string.IsNullOrWhiteSpace(TxtFontFamily.Text) ? "Segoe UI" : TxtFontFamily.Text.Trim();
        var color = string.IsNullOrWhiteSpace(TxtColor.Text) ? "#000000" : TxtColor.Text.Trim();

        TextResult = text;
        FontSizeResult = size;
        TextItem = new TextItem
        {
            Text = text,
            FontFamily = fontFamily,
            FontSizePt = size,
            Color = color,
            Bold = ChkBold.IsChecked == true,
            Italic = ChkItalic.IsChecked == true,
            Underline = ChkUnderline.IsChecked == true,
            Strikethrough = ChkStrikethrough.IsChecked == true,
            Rotation = rotation,
            TextAlign = align
        };
        DialogResult = true;
    }
}
