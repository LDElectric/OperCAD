using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OperCAD.Application;
using OperCAD.Core.Elements;
using OperCAD.Drawing;
using SkiaSharp;

namespace OperCAD.UI.ViewModels;

/// <summary>Item de elemento da paleta (com preview gerado sob demanda).</summary>
public sealed class ElementItemViewModel : ObservableObject
{
    private BitmapSource? _preview;
    private bool _isSelected;

    public ElementDefinition Definition { get; private set; }

    public string Name => ElementNameTranslator.Translate(Definition.Name);

    /// <summary>Nome do arquivo do elemento (sem extensão) — usado na busca e no Info.</summary>
    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Definition.SourcePath);

    public string Category => Definition.Category;

    public string Info =>
        $"{Definition.Width:0} × {Definition.Height:0} mm · {Definition.Terminals.Count} terminal(is)";

    public BitmapSource Preview => _preview ??= RenderPreview();

    /// <summary>Indica se o elemento está sendo inserido (destaque na paleta).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged();
        }
    }

    public ElementItemViewModel(ElementDefinition definition)
    {
        Definition = definition;
    }

    /// <summary>Recarrega a definição (após edição no editor de elementos) e o preview.</summary>
    public void Reload(ElementDefinition definition)
    {
        Definition = definition;
        _preview = null;
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Preview));
    }

    private BitmapSource RenderPreview()
    {
        try
        {
            using var bmp = ElementRenderer.RenderPreview(Definition, 48, 48);
            return ToBitmapSource(bmp);
        }
        catch
        {
            return null!;
        }
    }

    /// <summary>Converte um SKBitmap em BitmapSource para exibição WPF. Usa encode PNG
    /// (mesmo método da barra de classes) — evita o WriteableBitmap.WritePixels, que lançava
    /// System.ArgumentException em PresentationCore e deixava o preview em branco.</summary>
    private static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        stream.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = stream;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }
}
