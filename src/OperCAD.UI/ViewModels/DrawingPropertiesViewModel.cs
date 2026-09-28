using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OperCAD.Core.Documents;

namespace OperCAD.UI.ViewModels;

/// <summary>
/// ViewModel para as propriedades de desenho (cor, espessura, estilo de traço, preenchimento).
/// Usado no painel lateral direito e para definir o estilo padrão de novas formas.
/// Suporta sincronização bidirecional: carrega de formas selecionadas e aplica de volta.
/// </summary>
public sealed partial class DrawingPropertiesViewModel : ObservableObject
{
    /// <summary>Flag para evitar loop infinito ao carregar propriedades de uma forma.</summary>
    private bool _suppressUpdate;

    /// <summary>Disparado quando as propriedades mudam e devem ser aplicadas a formas selecionadas.</summary>
    public event EventHandler? PropertiesChanged;

    /// <summary>Paleta de cores predefinidas para seleção rápida.</summary>
    public IReadOnlyList<string> PresetColors { get; } = new[]
    {
        "#000000", "#FFFFFF", "#FF0000", "#00AA00", "#0000FF",
        "#FFFF00", "#FF8800", "#808080", "#8800AA", "#00AAAA"
    };

    /// <summary>Espessuras predefinidas em mm (do mais fino ao mais grossa).</summary>
    public IReadOnlyList<double> PresetWidths { get; } = new[]
    {
        0.07, 0.15, 0.29, 0.50, 0.75, 1.0
    };

    /// <summary>Estilos de traço disponíveis.</summary>
    public IReadOnlyList<LineDashStyle> DashStyles { get; } = new[]
    {
        LineDashStyle.Solid,
        LineDashStyle.Dashed,
        LineDashStyle.Dotted,
        LineDashStyle.DashDot
    };

    [ObservableProperty]
    private string _selectedColor = "#000000";

    [ObservableProperty]
    private double _selectedPenWidth = 0.29;

    [ObservableProperty]
    private double _selectedFontSize = TextItem.DefaultFontSizePt;

    /// <summary>Família padrão dos novos textos (igual à das etiquetas do PDF).</summary>
    [ObservableProperty]
    private string _selectedFontFamily = TextItem.DefaultFontFamily;

    [ObservableProperty]
    private LineDashStyle _selectedDashStyle = LineDashStyle.Solid;

    [ObservableProperty]
    private string _filling = "none";

    [ObservableProperty]
    private string _fillingColor = "#000000";

    [ObservableProperty]
    private bool _hasFilling;

    /// <summary>
    /// Modo de preenchimento da FERRAMENTA de desenho: <c>false</c> = vazado (PADRÃO),
    /// <c>true</c> = preenchido (sólido). É independente do preenchimento da forma
    /// selecionada (<see cref="Filling"/>/<see cref="HasFilling"/>): inspecionar um símbolo
    /// preenchido importado não faz as próximas formas nascerem preenchidas.
    /// </summary>
    [ObservableProperty]
    private bool _toolFilled;

    /// <summary>Radio "Vazado (padrão)" — inverso de <see cref="ToolFilled"/>.</summary>
    public bool ToolHollow
    {
        get => !ToolFilled;
        set => ToolFilled = !value;
    }

    /// <summary>Preenchimento das NOVAS formas: "none" (vazado) ou a cor escolhida.</summary>
    public string FillingForNewShapes => Drawing.DrawingFillRules.FillingForNewShapes(ToolFilled, FillingColor);

    partial void OnToolFilledChanged(bool value) => OnPropertyChanged(nameof(ToolHollow));

    [ObservableProperty]
    private bool _isEditingShape;

    /// <summary>Texto descritivo do estilo de traço selecionado.</summary>
    public string DashStyleName => SelectedDashStyle switch
    {
        LineDashStyle.Solid => "Sólido",
        LineDashStyle.Dashed => "Tracejado",
        LineDashStyle.Dotted => "Pontilhado",
        LineDashStyle.DashDot => "Traço-ponto",
        _ => "Sólido"
    };

    /// <summary>Descrição da espessura atual (para tooltip).</summary>
    public string WidthDescription => SelectedPenWidth switch
    {
        <= 0.07 => "Muito fino (cosmético)",
        <= 0.15 => "Fino",
        <= 0.29 => "Normal (padrão)",
        <= 0.50 => "Médio",
        <= 0.75 => "Grosso",
        _ => "Muito grosso"
    };

    partial void OnSelectedDashStyleChanged(LineDashStyle value)
    {
        OnPropertyChanged(nameof(DashStyleName));
        if (!_suppressUpdate) PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedPenWidthChanged(double value)
    {
        OnPropertyChanged(nameof(WidthDescription));
        if (!_suppressUpdate) PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedColorChanged(string value)
    {
        if (!_suppressUpdate) PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFillingChanged(string value)
    {
        if (!_suppressUpdate) PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SetDashStyle(LineDashStyle style) => SelectedDashStyle = style;

    [RelayCommand]
    private void SetWidth(double width) => SelectedPenWidth = width;

    [RelayCommand]
    private void SetColor(string color) => SelectedColor = color;

    [RelayCommand]
    private void ToggleFilling()
    {
        HasFilling = !HasFilling;
        Filling = HasFilling ? FillingColor : "none";
    }

    partial void OnHasFillingChanged(bool value)
    {
        Filling = value ? FillingColor : "none";
    }

    partial void OnFillingColorChanged(string value)
    {
        OnPropertyChanged(nameof(FillingForNewShapes));
        if (HasFilling)
            Filling = value;
    }

    /// <summary>Carrega as propriedades de uma forma selecionada para o painel.</summary>
    public void LoadFromShape(ShapeItem shape)
    {
        _suppressUpdate = true;
        try
        {
            SelectedColor = shape.Color;
            SelectedPenWidth = shape.PenWidth;
            SelectedDashStyle = shape.DashStyle;
            Filling = shape.Filling;
            HasFilling = shape.Filling != "none";
            if (HasFilling) FillingColor = shape.Filling;
            IsEditingShape = true;
        }
        finally
        {
            _suppressUpdate = false;
        }
    }

    /// <summary>Limpa o estado de edição de forma (volta ao modo de criação).</summary>
    public void ClearShapeEditing()
    {
        _suppressUpdate = true;
        try
        {
            IsEditingShape = false;
        }
        finally
        {
            _suppressUpdate = false;
        }
    }

    /// <summary>Cria um StyleSnapshot com o estado atual (para aplicar a shapes).</summary>
    public DrawingStyleSnapshot ToSnapshot() => new(SelectedColor, SelectedPenWidth, SelectedDashStyle, Filling);

    /// <summary>Aplica um snapshot ao ViewModel.</summary>
    public void FromSnapshot(DrawingStyleSnapshot snapshot)
    {
        _suppressUpdate = true;
        try
        {
            SelectedColor = snapshot.Color;
            SelectedPenWidth = snapshot.PenWidth;
            SelectedDashStyle = snapshot.DashStyle;
            Filling = snapshot.Filling;
            HasFilling = snapshot.Filling != "none";
        }
        finally
        {
            _suppressUpdate = false;
        }
    }
}

/// <summary>Snapshot imutável do estilo de desenho (para cópia/undo).</summary>
public sealed record DrawingStyleSnapshot(
    string Color,
    double PenWidth,
    LineDashStyle DashStyle,
    string Filling);
