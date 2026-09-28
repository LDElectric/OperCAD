using System.Windows;
using OperCAD.Core.TerminalStrips;

namespace OperCAD.UI.Views;

/// <summary>Terminal livre de um elemento (borne de ligação) oferecido ao terminal strip.</summary>
public sealed record FreeTerminal(Guid ElementId, string Label, string TerminalName)
{
    public override string ToString() => Label;
}

/// <summary>
/// Editor de bornes (terminal strip) — port simplificado do TerminalStripEditorWindow
/// do padrão CAD: nome/comentário, níveis numerados com terminais reais, pontes e ordenação.
/// </summary>
public partial class TerminalStripWindow : Window
{
    private readonly TerminalStrip _strip;
    private readonly IReadOnlyList<FreeTerminal> _freeTerminals;

    // Snapshot para restaurar em caso de cancelamento.
    private readonly string _name;
    private readonly string _comment;
    private readonly List<List<Guid>> _levels;
    private readonly List<(Guid Uuid, string Color, List<Guid> Uuids)> _bridges;

    public TerminalStripWindow(TerminalStrip strip, IReadOnlyList<FreeTerminal> freeTerminals)
    {
        InitializeComponent();
        _strip = strip;
        _freeTerminals = freeTerminals;

        _name = strip.Name;
        _comment = strip.Comment;
        _levels = strip.PhysicalTerminals.Select(p => p.ElementUuids.ToList()).ToList();
        _bridges = strip.Bridges.Select(b => (b.Uuid, b.Color, b.ElementUuids.ToList())).ToList();

        TxtName.Text = strip.Name;
        TxtComment.Text = strip.Comment;
        CmbBridgeColor.ItemsSource = new[]
        {
            new { Label = "Ponte vermelha", Value = "#e60000" },
            new { Label = "Ponte azul", Value = "#0057b8" },
            new { Label = "Ponte verde", Value = "#2e7d32" },
            new { Label = "Ponte preta", Value = "#000000" }
        };
        CmbBridgeColor.DisplayMemberPath = "Label";
        CmbBridgeColor.SelectedIndex = 0;
        foreach (var ft in freeTerminals)
            CmbFreeTerminals.Items.Add(ft);
        if (freeTerminals.Count > 0)
            CmbFreeTerminals.SelectedIndex = 0;
        RefreshLevels();
    }

    private void RefreshLevels()
    {
        LstLevels.Items.Clear();
        var n = 0;
        foreach (var level in _strip.PhysicalTerminals)
        {
            n++;
            var parts = level.ElementUuids.Select(LabelOf).Where(s => s.Length > 0);
            var bridge = _strip.Bridges.FirstOrDefault(b => b.ElementUuids.Any(level.ElementUuids.Contains));
            var marker = bridge is null ? string.Empty : "  ⏚ (ponte)";
            LstLevels.Items.Add($"{n} — {string.Join(" + ", parts)}{marker}");
        }
    }

    private string LabelOf(Guid elementId)
    {
        var ft = _freeTerminals.FirstOrDefault(f => f.ElementId == elementId);
        return ft is null ? elementId.ToString("N")[..8] : $"{ft.Label} ({ft.TerminalName})";
    }

    private void OnAddLevel(object sender, RoutedEventArgs e)
    {
        if (CmbFreeTerminals.SelectedItem is not FreeTerminal ft)
            return;
        var pt = new PhysicalTerminal();
        pt.ElementUuids.Add(ft.ElementId);
        _strip.PhysicalTerminals.Add(pt);
        CmbFreeTerminals.Items.Remove(ft);
        RefreshLevels();
    }

    private void OnAddToLevel(object sender, RoutedEventArgs e)
    {
        if (CmbFreeTerminals.SelectedItem is not FreeTerminal ft)
            return;
        var pt = SelectedPhysicalTerminal();
        if (pt is null)
        {
            MessageBox.Show(this, "Selecione um nível do borne para adicionar o terminal.", "Borne",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        pt.ElementUuids.Add(ft.ElementId);
        CmbFreeTerminals.Items.Remove(ft);
        RefreshLevels();
    }

    private PhysicalTerminal? SelectedPhysicalTerminal()
        => LstLevels.SelectedIndex >= 0 && LstLevels.SelectedIndex < _strip.PhysicalTerminals.Count
            ? _strip.PhysicalTerminals[LstLevels.SelectedIndex]
            : null;

    private void OnRemoveLevel(object sender, RoutedEventArgs e)
    {
        foreach (var idx in LstLevels.SelectedItems.Cast<int>().OrderByDescending(i => i))
        {
            if (idx < 0 || idx >= _strip.PhysicalTerminals.Count)
                continue;
            _strip.PhysicalTerminals.RemoveAt(idx);
        }
        RefreshLevels();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveLevel(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveLevel(+1);

    private void MoveLevel(int delta)
    {
        var idx = LstLevels.SelectedIndex;
        if (idx < 0)
            return;
        var target = idx + delta;
        if (target < 0 || target >= _strip.PhysicalTerminals.Count)
            return;
        var pt = _strip.PhysicalTerminals[idx];
        _strip.PhysicalTerminals.RemoveAt(idx);
        _strip.PhysicalTerminals.Insert(target, pt);
        LstLevels.SelectedIndex = target;
        RefreshLevels();
    }

    private void OnBridge(object sender, RoutedEventArgs e)
    {
        var uuids = LstLevels.SelectedItems.Cast<int>()
            .Where(i => i >= 0 && i < _strip.PhysicalTerminals.Count)
            .SelectMany(i => _strip.PhysicalTerminals[i].ElementUuids)
            .Distinct()
            .ToList();
        if (uuids.Count < 2)
        {
            MessageBox.Show(this, "Selecione 2 ou mais níveis para criar uma ponte.", "Borne",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var bridge = new TerminalStripBridge
        {
            Color = CmbBridgeColor.SelectedItem is { } selected
                ? (string)selected.GetType().GetProperty("Value")!.GetValue(selected)!
                : "#e60000"
        };
        foreach (var u in uuids)
            bridge.ElementUuids.Add(u);
        _strip.Bridges.Add(bridge);
        RefreshLevels();
    }

    private void OnRemoveBridge(object sender, RoutedEventArgs e)
    {
        var uuids = LstLevels.SelectedItems.Cast<int>()
            .Where(i => i >= 0 && i < _strip.PhysicalTerminals.Count)
            .SelectMany(i => _strip.PhysicalTerminals[i].ElementUuids)
            .ToHashSet();
        foreach (var bridge in _strip.Bridges.Where(b => b.ElementUuids.Any(uuids.Contains)).ToList())
            _strip.Bridges.Remove(bridge);
        RefreshLevels();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            MessageBox.Show(this, "Informe o nome do borne.", "Borne",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _strip.Name = TxtName.Text.Trim();
        _strip.Comment = TxtComment.Text ?? string.Empty;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DialogResult != true)
        {
            // Cancela: restaura o estado original.
            _strip.Name = _name;
            _strip.Comment = _comment;
            _strip.PhysicalTerminals.Clear();
            foreach (var level in _levels)
            {
                var pt = new PhysicalTerminal();
                foreach (var u in level)
                    pt.ElementUuids.Add(u);
                _strip.PhysicalTerminals.Add(pt);
            }
            _strip.Bridges.Clear();
            foreach (var (uuid, color, uuids) in _bridges)
            {
                var b = new TerminalStripBridge { Uuid = uuid, Color = color };
                foreach (var u in uuids)
                    b.ElementUuids.Add(u);
                _strip.Bridges.Add(b);
            }
        }
    }
}
