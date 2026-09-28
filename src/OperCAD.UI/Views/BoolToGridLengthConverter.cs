using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OperCAD.UI.Views;

/// <summary>
/// Converte bool → GridLength para colapsar colunas de painéis: se visível, retorna
/// a largura do parâmetro (ex.: "280"); se oculto, retorna 0 (coluna colapsa e a
/// folha ganha todo o espaço).
/// </summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visivel = value is true;
        var largura = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0;
        return new GridLength(visivel ? largura : 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
