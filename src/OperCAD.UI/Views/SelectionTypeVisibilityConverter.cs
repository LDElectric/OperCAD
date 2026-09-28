using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OperCAD.UI.Views;

/// <summary>
/// Conversor que retorna Visible quando o SelectionType corresponde ao TargetType,
/// e Collapsed caso contrário. Usado para alternar entre os painéis de propriedades
/// da folha, do elemento e do condutor no dock direito.
/// </summary>
public sealed class SelectionTypeVisibilityConverter : IValueConverter
{
    /// <summary>O tipo alvo ("none", "element" ou "conductor").</summary>
    public string TargetType { get; set; } = "none";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var current = value as string ?? "none";
        return string.Equals(current, TargetType, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
