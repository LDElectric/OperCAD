using System.Windows;
using System.Windows.Controls;

namespace OperCAD.UI.Views;

/// <summary>Editor direto de um campo do quadro de legenda.</summary>
public partial class TitleBlockFieldWindow : Window
{
    private readonly int _maxLength;

    public string ValueResult { get; private set; } = string.Empty;

    public TitleBlockFieldWindow(string label, string value, int maxLength, bool multiline)
    {
        InitializeComponent();
        _maxLength = Math.Clamp(maxLength, 1, 200);
        LblField.Text = string.IsNullOrWhiteSpace(label) ? "CAMPO DA LEGENDA" : label;
        LblLimit.Text = $"máx. {_maxLength} caracteres";
        TxtValue.MaxLength = _maxLength;
        TxtValue.Text = value ?? string.Empty;
        TxtValue.AcceptsReturn = multiline;
        TxtValue.VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        Height = multiline ? 250 : 210;
        Loaded += (_, _) =>
        {
            TxtValue.Focus();
            TxtValue.CaretIndex = TxtValue.Text.Length;
            TxtValue.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // MaxLength também vale para colagens; TrimEnd evita linhas vazias consumindo espaço.
        ValueResult = (TxtValue.Text ?? string.Empty).TrimEnd();
        DialogResult = true;
    }
}
