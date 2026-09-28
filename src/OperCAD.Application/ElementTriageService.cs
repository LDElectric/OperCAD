using System.Text.Json;
using System.Text.Json.Serialization;
using OperCAD.Core.Documents;
using OperCAD.Core.Recognition;

namespace OperCAD.Application;

/// <summary>
/// Serviço de triagem por amostras (reconhecimento assistido), compartilhado na sessão.
/// O usuário marca aglomerados selecionados de primitivas como um elemento (ou "não é elemento");
/// clusters ainda não marcados podem ser classificados por similaridade (invariante a escala/rotação).
/// As amostras são persistidas em disco (%AppData%/OperCAD/triage_samples.json).
/// </summary>
public sealed class ElementTriageService
{
    private static readonly Lazy<ElementTriageService> DefaultInstance = new(() => new ElementTriageService());

    /// <summary>Instância compartilhada da sessão.</summary>
    public static ElementTriageService Default => DefaultInstance.Value;

    private readonly ElementTriage _triage = new();
    private readonly List<ElementSampleEntry> _entries = new();

    public IReadOnlyList<ElementSampleEntry> Entries => _entries;
    public int PositiveCount => _entries.Count(e => e.IsElement);
    public int NegativeCount => _entries.Count(e => !e.IsElement);

    public void MarkAsElement(string label, IReadOnlyList<ShapeItem> shapes)
    {
        if (string.IsNullOrWhiteSpace(label) || shapes.Count == 0) return;
        _triage.AddPositive(label, shapes);
        _entries.Add(new ElementSampleEntry(label, true));
    }

    public void MarkAsNotElement(IReadOnlyList<ShapeItem> shapes)
    {
        if (shapes.Count == 0) return;
        _triage.AddNegative(shapes);
        _entries.Add(new ElementSampleEntry(string.Empty, false));
    }

    /// <summary>Sugere um rótulo (elemento) para um aglomerado não marcado, ou null.</summary>
    public (string? Label, double Score)? Suggest(IReadOnlyList<ShapeItem> shapes)
        => _triage.Classify(shapes);

    public void Clear()
    {
        _triage.Clear();
        _entries.Clear();
    }

    // --- Persistência em disco ---

    private static string GetSamplesFilePath()
    {
        Directory.CreateDirectory(PortablePath.DataDirectory);
        return Path.Combine(PortablePath.DataDirectory, "triage_samples.json");
    }

    /// <summary>Salva as amostras atuais em disco.</summary>
    public void Save()
    {
        var samples = _triage.Samples.Select(s => new PersistedSample
        {
            Label = s.Label,
            Grid = s.Grid,
            Descriptor = s.Descriptor
        }).ToList();

        var data = new PersistedData { Samples = samples };
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(GetSamplesFilePath(), json);
    }

    /// <summary>Carrega amostras salvas de disco (se existirem).</summary>
    public void Load()
    {
        var path = GetSamplesFilePath();
        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PersistedData>(json, JsonOptions);
            if (data?.Samples is null) return;

            _triage.Clear();
            _entries.Clear();

            foreach (var s in data.Samples)
            {
                if (s.Descriptor is { Length: > 0 })
                {
                    if (!string.IsNullOrEmpty(s.Label))
                        _triage.AddPositive(s.Label, s.Descriptor, s.Grid);
                    else
                        _triage.AddNegative(s.Descriptor, s.Grid);

                    _entries.Add(new ElementSampleEntry(s.Label, !string.IsNullOrEmpty(s.Label)));
                }
            }
        }
        catch
        {
            // Arquivo corrompido — ignora silenciosamente
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class PersistedData
    {
        public List<PersistedSample> Samples { get; set; } = new();
    }

    private sealed class PersistedSample
    {
        public string Label { get; set; } = string.Empty;
        public int Grid { get; set; } = 16;
        public double[] Descriptor { get; set; } = Array.Empty<double>();
    }
}

/// <summary>Entrada exibível de uma amostra marcada na sessão.</summary>
public sealed record ElementSampleEntry(string Label, bool IsElement);
