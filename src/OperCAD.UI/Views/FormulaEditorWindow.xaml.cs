using System.Windows;

namespace OperCAD.UI.Views;

public partial class FormulaEditorWindow : Window
{
    public string ResultFormula { get; private set; } = string.Empty;

    public FormulaEditorWindow(List<string> availableVariables)
    {
        InitializeComponent();
        LstVariables.ItemsSource = availableVariables;
    }

    private void InsertAtCursor(string text)
    {
        var caretIndex = TxtFormula.CaretIndex;
        TxtFormula.Text = TxtFormula.Text.Insert(caretIndex, text);
        TxtFormula.CaretIndex = caretIndex + text.Length;
        TxtFormula.Focus();
    }

    private void OnVariableDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LstVariables.SelectedItem is string selected)
            InsertAtCursor($"{{{selected}}}");
    }

    private void OnInsertClick(object sender, RoutedEventArgs e)
    {
        if (LstVariables.SelectedItem is string selected)
            InsertAtCursor($"{{{selected}}}");
    }

    private void OnOperatorClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn)
            InsertAtCursor(btn.Content?.ToString() ?? string.Empty);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ResultFormula = TxtFormula.Text;
        DialogResult = true;
    }
}
