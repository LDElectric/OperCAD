using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OperCAD.Core.Documents;

namespace OperCAD.UI.Views;

/// <summary>Converte string hex (#RRGGBB) para SolidColorBrush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value as string ?? "#000000";
        try
        {
            return new BrushConverter().ConvertFromString(hex) as Brush ?? Brushes.Black;
        }
        catch
        {
            return Brushes.Black;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converte LineDashStyle para bool (IsChecked de RadioButton) baseado no ConverterParameter.</summary>
public sealed class DashStyleToRadioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LineDashStyle style && parameter is string param)
        {
            var target = param switch
            {
                "Solid" => LineDashStyle.Solid,
                "Dashed" => LineDashStyle.Dashed,
                "Dotted" => LineDashStyle.Dotted,
                "DashDot" => LineDashStyle.DashDot,
                _ => LineDashStyle.Solid
            };
            return style == target;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string param)
        {
            return param switch
            {
                "Solid" => LineDashStyle.Solid,
                "Dashed" => LineDashStyle.Dashed,
                "Dotted" => LineDashStyle.Dotted,
                "DashDot" => LineDashStyle.DashDot,
                _ => LineDashStyle.Solid
            };
        }
        return value!;
    }
}
