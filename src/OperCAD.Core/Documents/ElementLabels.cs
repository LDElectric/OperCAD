using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;

namespace OperCAD.Core.Documents;

/// <summary>
/// Nomenclatura automática e posição no quadro (referência cruzada estilo padrão CAD).
/// </summary>
public static class ElementLabels
{
    /// <summary>Determina o prefixo da etiqueta a partir da categoria/nome do elemento (IEC 61355).</summary>
    public static string Prefix(ElementDefinition def)
    {
        var hay = (def.Category + " " + def.Name).ToLowerInvariant();

        // K — Relé, contato, contator, bobina, comando
        if (hay.Contains("relai") || hay.Contains("rele") || hay.Contains("relé") ||
            hay.Contains("contact") || hay.Contains("contato") || hay.Contains("contator") ||
            hay.Contains("contactor") || hay.Contains("bobina") || hay.Contains("comm_"))
            return "K";

        // Q — Disjuntor, fusível, seccionador, proteção
        if (hay.Contains("disjoncteur") || hay.Contains("disjuntor") ||
            hay.Contains("fusib") || hay.Contains("fusível") || hay.Contains("fusivel") ||
            hay.Contains("sectionneur") || hay.Contains("seccionador"))
            return "Q";

        // M — Motor, máquina
        if (hay.Contains("moteur") || hay.Contains("motor") || hay.Contains("machine") ||
            hay.Contains("máquina") || hay.Contains("maquina"))
            return "M";

        // S — Interruptor, botão, chave, seletor
        if (hay.Contains("interrupteur") || hay.Contains("interruptor") ||
            hay.Contains("bouton") || hay.Contains("botão") || hay.Contains("botao") ||
            hay.Contains("chave") || hay.Contains("seletor") || hay.Contains("switch"))
            return "S";

        // H — Lâmpada, LED, sinalização, indicador
        if (hay.Contains("lampe") || hay.Contains("lâmpada") || hay.Contains("lampada") ||
            hay.Contains("voyant") || hay.Contains("signal") || hay.Contains("led") ||
            hay.Contains("sinal") || hay.Contains("indicador") || hay.Contains("luz"))
            return "H";

        // T — Transformador
        if (hay.Contains("transformateur") || hay.Contains("transformador"))
            return "T";

        // G — Alimentação, fonte, gerador
        if (hay.Contains("alimentation") || hay.Contains("alimentação") ||
            hay.Contains("alimentacao") || hay.Contains("source") || hay.Contains("fonte") ||
            hay.Contains("generator") || hay.Contains("gerador"))
            return "G";

        // R — Resistor, resistência, potenciômetro
        if (hay.Contains("resistance") || hay.Contains("resistência") ||
            hay.Contains("resistencia") || hay.Contains("resistor") ||
            hay.Contains("potenciômetro") || hay.Contains("potenciometro"))
            return "R";

        // C — Capacitor, capacitância
        if (hay.Contains("condensat") || hay.Contains("capacitor") ||
            hay.Contains("capacitância") || hay.Contains("capacitancia"))
            return "C";

        // V — Semiconductor: diodo, transistor, MOSFET, tiristor, SCR, triac
        if (hay.Contains("diode") || hay.Contains("diodo") || hay.Contains("semi") ||
            hay.Contains("transistor") || hay.Contains("mosfet") || hay.Contains("tiristor") ||
            hay.Contains("scr") || hay.Contains("triac") || hay.Contains("igbt"))
            return "V";

        // X — Terminal, borne, conector, tomada, barra
        if (hay.Contains("borne") || hay.Contains("terminal") || hay.Contains("barrette") ||
            hay.Contains("barra") || hay.Contains("conector") || hay.Contains("tomada"))
            return "X";

        return "E";
    }

    /// <summary>Letra da coluna (0→A, 25→Z, 26→AA...).</summary>
    public static string ColumnLetter(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            index--;
            s = (char)('A' + index % 26) + s;
            index /= 26;
        }
        return s;
    }

    /// <summary>Posição no quadro (ex.: "B3") a partir de um ponto do mundo.</summary>
    public static string Position(PageSettings settings, Point2D world)
    {
        var drawing = settings.DrawingBounds;
        var cols = Math.Max(settings.ColumnsCount, 1);
        var rows = Math.Max(settings.RowsCount, 1);
        // padrão CAD: LETRA = linha (do topo, Y), NÚMERO = coluna (X, 1-based)
        var col = (int)Math.Clamp(Math.Floor((world.X - drawing.MinX) / (drawing.Width / cols)), 0, cols - 1);
        var row = (int)Math.Clamp(Math.Floor((drawing.MaxY - world.Y) / (drawing.Height / rows)), 0, rows - 1);
        return ColumnLetter(row) + (col + 1);
    }
}
