using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace OperCAD.UI.Controls;

/// <summary>Conversão de SKBitmap para BitmapSource (WPF).</summary>
public static class SkiaBitmapExtensions
{
    public static BitmapSource ToBitmapSource(this SKBitmap bmp)
    {
        var wb = new WriteableBitmap(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            var rect = new Int32Rect(0, 0, bmp.Width, bmp.Height);
            wb.WritePixels(rect, bmp.Bytes, bmp.RowBytes * bmp.Height, bmp.RowBytes);
        }
        finally
        {
            wb.Unlock();
        }
        wb.Freeze();
        return wb;
    }
}
