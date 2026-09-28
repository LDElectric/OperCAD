using System.Xml.Linq;
using OperCAD.Core.TerminalStrips;

namespace OperCAD.Application;

/// <summary>
/// Serialização XML dos bornes (terminal strips) — fiel ao formato do padrão CAD 0.100:
/// &lt;terminal_strip_items&gt;&lt;terminal_strip_item&gt;&lt;terminal_strip uuid="..."&gt;
///   &lt;terminal_strip_data uuid="..."&gt;&lt;informations&gt;...
///   &lt;layout&gt;&lt;physical_terminal&gt;&lt;real_terminal element_uuid="..."/&gt;...
///   &lt;terminal_strip_bridge uuid="..." color="#..."&gt;&lt;real_terminals&gt;...
/// </summary>
public static class TerminalStripXml
{
    public const string ItemsTag = "terminal_strip_items";

    /// <summary>Borne lido do XML com a posição do item na folha.</summary>
    public sealed record TerminalStripXmlItem(TerminalStrip Strip, double X, double Y);

    /// <summary>Gera o elemento &lt;terminal_strip_items&gt; com todos os bornes (posição 0,0).</summary>
    public static XElement ToXml(IEnumerable<TerminalStrip> strips)
        => ToXml(strips.Select(s => (s, 0.0, 0.0)));

    /// <summary>Gera o elemento &lt;terminal_strip_items&gt; com bornes e posições na folha.</summary>
    public static XElement ToXml(IEnumerable<(TerminalStrip Strip, double X, double Y)> entries)
    {
        var root = new XElement(ItemsTag);
        foreach (var (strip, x, y) in entries)
            root.Add(ToXml(strip, x, y));
        return root;
    }

    /// <summary>Gera o item de um borne (com posição na folha).</summary>
    public static XElement ToXml(TerminalStrip strip, double x = 0, double y = 0)
    {
        var stripEl = new XElement("terminal_strip", new XAttribute("uuid", strip.Uuid));

        // Dados (nome/comentário)
        var data = new XElement("terminal_strip_data", new XAttribute("uuid", Guid.NewGuid()));
        var infos = new XElement("informations");
        void AddInfo(string name, string value) => infos.Add(new XElement("information",
            new XAttribute("name", name), value));
        AddInfo("name", strip.Name);
        AddInfo("comment", strip.Comment);
        data.Add(infos);
        stripEl.Add(data);

        // Layout: níveis (physical terminals) com terminais reais
        var layout = new XElement("layout");
        foreach (var pt in strip.PhysicalTerminals)
        {
            var xmlPt = new XElement("physical_terminal");
            foreach (var uuid in pt.ElementUuids)
                xmlPt.Add(new XElement("real_terminal", new XAttribute("element_uuid", uuid)));
            layout.Add(xmlPt);
        }
        stripEl.Add(layout);

        // Pontes
        foreach (var bridge in strip.Bridges)
        {
            var xmlBridge = new XElement("terminal_strip_bridge",
                new XAttribute("uuid", bridge.Uuid),
                new XAttribute("color", bridge.Color));
            var xmlTerminals = new XElement("real_terminals");
            foreach (var uuid in bridge.ElementUuids)
                xmlTerminals.Add(new XElement("real_terminal", new XAttribute("uuid", uuid)));
            xmlBridge.Add(xmlTerminals);
            stripEl.Add(xmlBridge);
        }

        return new XElement("terminal_strip_item", stripEl,
            new XElement("pos", new XAttribute("x", x), new XAttribute("y", y)));
    }

    /// <summary>Lê os bornes de um &lt;terminal_strip_items&gt; (com posição de cada item).</summary>
    public static List<TerminalStripXmlItem> FromXml(XElement? itemsEl)
    {
        var strips = new List<TerminalStripXmlItem>();
        if (itemsEl is null)
            return strips;

        foreach (var item in itemsEl.Elements("terminal_strip_item"))
        {
            var stripEl = item.Element("terminal_strip");
            if (stripEl is null)
                continue;
            var x = 0.0;
            var y = 0.0;
            var pos = item.Element("pos");
            if (pos is not null)
            {
                double.TryParse(pos.Attribute("x")?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x);
                double.TryParse(pos.Attribute("y")?.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out y);
            }
            var strip = new TerminalStrip
            {
                Uuid = GuidOrNew(stripEl.Attribute("uuid")?.Value)
            };

            var data = stripEl.Element("terminal_strip_data");
            var infos = data?.Element("informations");
            if (infos is not null)
            {
                foreach (var info in infos.Elements("information"))
                {
                    switch (info.Attribute("name")?.Value)
                    {
                        case "name": strip.Name = info.Value; break;
                        case "comment": strip.Comment = info.Value; break;
                    }
                }
            }

            var layout = stripEl.Element("layout");
            if (layout is not null)
            {
                foreach (var xmlPt in layout.Elements("physical_terminal"))
                {
                    var pt = new PhysicalTerminal();
                    foreach (var xmlReal in xmlPt.Elements("real_terminal"))
                    {
                        if (Guid.TryParse(xmlReal.Attribute("element_uuid")?.Value, out var g))
                            pt.ElementUuids.Add(g);
                    }
                    if (pt.ElementUuids.Count > 0)
                        strip.PhysicalTerminals.Add(pt);
                }
            }

            foreach (var xmlBridge in stripEl.Elements("terminal_strip_bridge"))
            {
                var bridge = new TerminalStripBridge
                {
                    Uuid = GuidOrNew(xmlBridge.Attribute("uuid")?.Value),
                    Color = xmlBridge.Attribute("color")?.Value ?? "#e60000"
                };
                var xmlTerminals = xmlBridge.Element("real_terminals");
                if (xmlTerminals is not null)
                {
                    foreach (var xmlReal in xmlTerminals.Elements("real_terminal"))
                    {
                        if (Guid.TryParse(xmlReal.Attribute("uuid")?.Value, out var g))
                            bridge.ElementUuids.Add(g);
                    }
                }
                if (bridge.ElementUuids.Count > 0)
                    strip.Bridges.Add(bridge);
            }

            strips.Add(new TerminalStripXmlItem(strip, x, y));
        }
        return strips;
    }

    private static Guid GuidOrNew(string? s) => Guid.TryParse(s, out var g) ? g : Guid.NewGuid();
}
