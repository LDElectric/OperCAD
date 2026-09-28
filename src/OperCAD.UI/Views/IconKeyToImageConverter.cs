using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OperCAD.UI.Views;

/// <summary>
/// Converte uma chave de ícone (string como "Icon.Class.PowerFeedings") para
/// o DrawingImage correspondente no ResourceDictionary.
/// </summary>
public sealed class IconKeyToImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
            return DependencyProperty.UnsetValue;

        if (System.Windows.Application.Current.TryFindResource(key) is ImageSource image)
            return image;

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
