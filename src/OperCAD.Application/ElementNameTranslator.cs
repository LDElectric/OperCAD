using System.Text.RegularExpressions;

namespace OperCAD.Application;

/// <summary>
/// Traduz nomes de elementos e categorias para português (pt-BR) usando um glossário técnico.
/// Aplica-se em tempo de execução, sem alterar os arquivos da biblioteca.
/// </summary>
public static class ElementNameTranslator
{
    // ---- Dicionário embutido (traducoes_pt_br.txt) ---------------------------
    // Gerado por traduzir_biblioteca.py (fase de desenvolvimento). Aplicado como
    // override por nome EXATO antes do glossário genérico. Linhas que terminam em
    // " ?" sinalizam "precisa revisão" e não são aplicadas como override.
    private static readonly Lazy<Dictionary<string, string>> ExactOverrides =
        new(LoadExactOverrides);

    private static Dictionary<string, string> LoadExactOverrides()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(ElementNameTranslator).Assembly
                .GetManifestResourceStream("OperCAD.Application.Resources.traducoes_pt_br.txt");
            if (stream is null)
                return map;

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var pipe = line.IndexOf('|');
                if (pipe <= 0)
                    continue;
                var from = line[..pipe].Trim();
                var to = line[(pipe + 1)..].Trim();
                if (to.EndsWith(" ?", StringComparison.Ordinal) || to.EndsWith("?", StringComparison.Ordinal))
                    continue; // precisa revisão: não usa como override
                if (from.Length > 0 && to.Length > 0)
                    map[from] = to;
            }
        }
        catch
        {
            // dicionário indisponível: segue só com o glossário
        }
        return map;
    }

    // Traduções compostas: nomes inteiros que precisam de tradução específica.
    // Verificados ANTES do glossário por palavra, caso-insensitive.
    private static readonly (string From, string To)[] CompoundTranslations =
    {
        // Elétrico - Motores / Caixas de fase
        ("Caisson phase triangle", "Caixa de fases - ligação triângulo"),
        ("Caisson phase star", "Caixa de fases - ligação estrela"),
        ("Caisson phase", "Caixa de fases"),

        // Elétrico - Lâmpadas
        ("Cabiner balast lamp", "Lâmpada com balastro de armário"),
        ("Cabiner balast", "Balastro de armário"),
        ("Cabinet balast lamp", "Lâmpada com balastro de armário"),
        ("Cabinet balast", "Balastro de armário"),

        // Elétrico - Transformadores
        ("Three-phase transducer", "Transformador trifásico"),
        ("Single phase transducer", "Transformador monofásico"),
        ("Two winding transformer", "Transformador de dois enrolamentos"),
        ("Transformer controllable", "Transformador controlável"),
        ("Power Supply AC/DC 1-ph. DC 24V", "Fonte de alimentação CA/CC 1-fase CC 24V"),
        ("Power supply AC/DC 3 poles", "Fonte de alimentação CA/CC 3 polos"),

        // Pneumático - Válvulas e atuadores
        ("Pressure switch with contact", "Pressostato com contato"),
        ("Pressure switch adjustable", "Pressostato ajustável"),
        ("Air drain", "Dreno de ar"),
        ("Continuous vent", "Ventilação contínua"),
        ("Flow divider", "Divisor de fluxo"),
        ("Single acting + spring", "Ação simples + mola"),
        ("Motor turns clockwise", "Motor gira no sentido horário"),
        ("Telescopic double acting", "Telescópica de dupla ação"),
        ("Double-acting + end-of-travel brake on both sides", "Dupla ação + freio de fim de curso em ambos os lados"),
        ("Parallel gripper normally closed + magnet", "Garra paralela normalmente fechada + ímã"),
        ("Quick coupling with check valve", "Acoplamento rápido com válvula check"),
        ("Compressed air accumulator two connections", "Acumulador de ar comprimido duas conexões"),
        ("Air compressor variable speed", "Compressor de ar de velocidade variável"),
        ("Valve without spring", "Válvula sem mola"),
        ("Check valve with spring", "Válvula de retenção com mola"),

        // Hidráulico - Válvulas e bombas
        ("Check valve", "Válvula check"),
        ("Three-way valve-control electromagnetic", "Válvula de 3 vias - comando eletromagnético"),
        ("Three-way valve", "Válvula de 3 vias"),
        ("Motorized three-way valve", "Válvula de 3 vias motorizada"),
        ("Manual valve", "Válvula manual"),
        ("Motorized valve", "Válvula motorizada"),
        ("Valve - electromagnetic control", "Válvula - comando eletromagnético"),
        ("Pressure Independent Control Valve", "Válvula de controle independente de pressão"),
        ("Fixed Displacement hydraulic pump", "Bomba hidráulica de deslocamento fixo"),
        ("Variable displacement hydraulic pump", "Bomba hidráulica de deslocamento variável"),
        ("Hydraulic motor 1", "Motor hidráulico 1"),
        ("Hydraulic transmission", "Transmissão hidráulica"),
        ("Double acting cylinders left", "Cilindros de dupla ação - esquerda"),
        ("Double acting cylinders right", "Cilindros de dupla ação - direita"),
        ("Single acting cylinder - returned by spring", "Cilindro de ação simples - retorno por mola"),
        ("Double acting cylinders -double ended piston rod", "Cilindros de dupla ação - haste de pistão dupla"),
        ("Telescopic hydraulic cylinder", "Cilindro hidráulico telescópico"),

        // Lógica - Fluxograma
        ("Process", "Processo"),
        ("Decision", "Decisão"),
        ("Start", "Início"),
        ("End", "Fim"),
        ("Step", "Etapa"),
        ("Document", "Documento"),
        ("Jump", "Pular"),
        ("Input", "Entrada"),
        ("Set signal output", "Definir saída de sinal"),
        ("Reset signal output", "Redefinir saída de sinal"),
    };

    // Pares (origem, tradução pt-BR). A substituição é por palavra inteira,
    // insensível a maiúsculas, preservando a capitalização da tradução.
    private static readonly (string From, string To)[] Glossary =
    {
        // ---- Francês → Português ----
        ("disjoncteur", "disjuntor"), ("disjonct", "disjuntor"),
        ("contacteur", "contator"), ("contacteurs", "contatores"),
        ("moteur", "motor"), ("moteurs", "motores"),
        ("interrupteur", "chave"), ("interrupteurs", "chaves"),
        ("sectionneur", "seccionador"),
        ("poussoir", "botoeira"), ("poussoirs", "botoeiras"),
        ("bouton", "botão"), ("boutons", "botões"),
        ("voyant", "lâmpada sinalizadora"), ("voyants", "lâmpadas sinalizadoras"),
        ("relais", "relé"), ("relai", "relé"),
        ("transformateur", "transformador"),
        ("fusible", "fusível"), ("fusibles", "fusíveis"),
        ("resistance", "resistor"),
        ("condensateur", "capacitor"),
        ("inductance", "indutor"),
        ("self", "bobina"), ("bobine", "bobina"),
        ("lampe", "lâmpada"),
        ("eclairage", "iluminação"),
        ("chauffage", "aquecimento"),
        ("thermique", "térmico"),
        ("magneto", "magneto"),
        ("differentiel", "diferencial"),
        ("unipolaire", "unipolar"), ("bipolaire", "bipolar"),
        ("tripolaire", "tripolar"), ("tetrapolaire", "tetrapolar"),
        ("monopolar", "monopolar"),
        ("phase", "fase"), ("phases", "fases"),
        ("neutre", "neutro"), ("neutre", "neutro"),
        ("terre", "terra"),
        ("protection", "proteção"), ("protections", "proteções"),
        ("coupure", "corte"),
        ("inverseur", "inversor"),
        ("commande", "comando"),
        ("puissance", "potência"),
        ("vitesse", "velocidade"),
        ("frein", "freio"),
        ("vanne", "válvula"), ("vannes", "válvulas"),
        ("pompe", "bomba"),
        ("ventilateur", "ventilador"),
        ("groupe", "grupo"), ("groupes", "grupos"),
        ("source", "fonte"), ("sources", "fontes"),
        ("alimentation", "alimentação"),
        ("regulation", "regulação"),
        ("capteur", "sensor"), ("capteurs", "sensores"),
        ("detecteur", "detector"),
        ("presence", "presença"),
        ("mouvement", "movimento"),
        ("course", "curso"),
        ("contact", "contato"), ("contacts", "contatos"),
        ("fermeture", "fechamento"), ("ouverture", "abertura"),
        ("temporise", "temporizado"), ("temporisation", "temporização"),
        ("minuterie", "minuteria"),
        ("horloge", "relógio"),
        ("compteur", "contador"), ("compteurs", "contadores"),
        ("afficheur", "display"),
        ("signalisation", "sinalização"), ("signal", "sinal"), ("signaux", "sinais"),
        ("sirene", "sirene"),
        ("haut-parleur", "alto-falante"),
        ("micro", "microfone"),
        ("prise", "tomada"), ("prises", "tomadas"),
        ("chargeur", "carregador"),
        ("batterie", "bateria"), ("pile", "pilha"), ("piles", "pilhas"),
        ("accumulateur", "acumulador"),
        ("appareil", "aparelho"),
        ("appareillage", "aparelhagem"),
        ("tableau", "quadro"), ("armoire", "armário"),
        ("coffret", "caixa"),
        ("telerupteur", "telerruptor"),
        ("gradateur", "dimerizador"),
        ("variateur", "variador"),
        ("sonnerie", "campainha"),
        ("alarme", "alarme"),
        ("indicateur", "indicador"),
        ("temoins", "testemunhos"), ("temoins de", "sinalizadores de"),
        ("secteur", "setor"),
        ("sectionneur", "seccionador"),
        ("parafoudre", "pára-raios"),
        ("relais_", "relé "),
        ("normalement", "normalmente"), ("ferme", "fechado"), ("ouvert", "aberto"),
        ("fermer", "fechar"), ("ouvrir", "abrir"),
        ("arrêt", "parada"), ("arret", "parada"), ("urgence", "emergência"),
        ("marche", "marcha"), ("marche/arret", "liga/desliga"),
        ("selecteur", "seletor"), ("selecteurs", "seletores"),
        ("a-", "de "),

        // ---- Inglês → Português (PT-BR técnico) ----
        ("circuit-breaker", "disjuntor"), ("circuit breaker", "disjuntor"),
        ("contactor", "contator"), ("contactors", "contatores"),
        ("motor", "motor"), ("motors", "motores"), ("engine", "motor"),
        ("machine", "máquina"), ("machines", "máquinas"),
        ("switch", "chave"), ("switches", "chaves"),
        ("push-button", "botoeira"), ("pushbutton", "botoeira"), ("push button", "botoeira"),
        ("button", "botão"),
        ("relay", "relé"), ("relays", "relés"),
        ("transformer", "transformador"),
        ("fuse", "fusível"), ("fuses", "fusíveis"),
        ("lamp", "lâmpada"), ("lamps", "lâmpadas"),
        ("indicator", "indicador"),
        ("coil", "bobina"),
        ("contact", "contato"), ("contacts", "contatos"),
        ("thermal", "térmico"),
        ("differential", "diferencial"),
        ("protection", "proteção"),
        ("power", "potência"),
        ("phase", "fase"), ("phases", "fases"),
        ("neutral", "neutro"),
        ("ground", "terra"), ("earth", "terra"),
        ("selector", "seletor"),
        ("supply", "alimentação"),
        ("distribution", "distribuição"),
        ("starter", "partida"),
        ("limit", "fim de curso"),
        ("emergency", "emergência"), ("emergency stop", "parada de emergência"),
        ("stop", "parada"), ("start", "partida"), ("start/stop", "liga/parada"),
        ("on/off", "liga/desliga"),
        ("automatic", "automático"), ("manual", "manual"),
        ("one-phase", "monofásico"), ("single-phase", "monofásico"),
        ("three-phase", "trifásico"), ("two-phase", "bifásico"),
        ("pole", "polo"), ("poles", "polos"), ("3p", "3P"), ("1p", "1P"), ("2p", "2P"), ("4p", "4P"),
        ("n", "N"), ("neutral", "neutro"), ("no", "NA"), ("nc", "NF"),
        ("normally open", "normalmente aberto"), ("normally closed", "normalmente fechado"),
        ("buzzer", "sonorizador"), ("horn", "buzina"),
        ("sensor", "sensor"), ("sensors", "sensores"),
        ("actuator", "atuador"), ("actuators", "atuadores"),
        ("valve", "válvula"), ("valves", "válvulas"),
        ("pump", "bomba"), ("pumps", "bombas"),
        ("compressor", "compressor"),
        ("cylinder", "cilindro"), ("cylinders", "cilindros"),
        ("filter", "filtro"), ("filters", "filtros"),
        ("regulator", "regulador"), ("regulator pressure", "regulador de pressão"),
        ("pressure", "pressão"),
        ("flow", "vazão"), ("flowmeter", "vazão"),
        ("tank", "tanque"), ("reservoir", "reservatório"),
        ("pipe", "tubulação"), ("tubing", "tubo"),
        ("junction", "junção"), ("terminal block", "bornier"), ("terminal", "borne"),
        ("connector", "conector"), ("connectors", "conectores"),
        ("socket", "tomada"), ("outlet", "tomada"),
        ("plug", "enchufe"),
        ("meter", "medidor"), ("counter", "contador"),
        ("timer", "temporizador"), ("time delay", "atraso de tempo"),
        ("encoder", "codificador"),
        ("potentiometer", "potenciômetro"),
        ("resistor", "resistor"), ("resistance", "resistor"),
        ("capacitor", "capacitor"), ("condensateur", "capacitor"),
        ("inductor", "indutor"), ("inductance", "indutor"),
        ("diode", "diodo"), ("led", "LED"),
        ("transistor", "transistor"),
        ("thyristor", "tiristor"), ("triac", "triac"),
        ("rectifier", "retificador"),
        ("inversor", "inversor"), ("inverter", "inversor"),
        ("frequency", "frequência"),
        ("speed", "velocidade"),
        ("torque", "torque"),
        ("brake", "freio"),
        ("clutch", "embreagem"),
        ("bearing", "rolamento"),
        ("gearbox", "caixa de engrenagens"), ("gear", "engrenagem"),
        ("pulley", "polia"), ("belt", "correia"),
        ("chain", "corrente"),
        ("conveyor", "transportador"),
        ("elevator", "elevador"),
        ("crane", "guindaste"),
        ("hoist", "guincho"),
        ("solenoid", "eletroímã"),
        ("latch", "tranca"), ("lock", "fechadura"),
        ("key switch", "chave de fechadura"),
        ("selector switch", "seletor giratório"),
        ("limit switch", "fim de curso"), ("limit switch", "chave de fim de curso"),
        ("proximity", "proximidade"),
        ("photoelectric", "fotoelétrico"), ("photoelectric sensor", "sensor fotoelétrico"),
        ("encoder", "encoder"), ("rotary encoder", "encoder rotativo"),
        ("linear", "linear"),
        ("position", "posição"),
        ("force", "força"),
        ("temperature", "temperatura"),
        ("level", "nível"),
        ("float", "boia"),
        ("switch-disconnector", "chave seccionadora"), ("switch disconnector", "chave seccionadora"),
        ("disconnector", "seccionador"),
        ("breaker", "disjuntor"),
        ("overload", "sobrecarga"),
        ("overload relay", "relé de sobrecarga"),
        ("thermal overload", "sobrecarga térmica"),
        ("motor protector", "protetor de motor"),
        ("motor circuit protector", "disjuntor de comando"),
        ("magnetic", "magnético"),
        ("electromechanical", "eletromecânico"),
        ("pneumatic", "pneumático"),
        ("hydraulic", "hidráulico"),
        ("electric", "elétrico"), ("electrical", "elétrico"),
        ("mechanical", "mecânico"),
        ("electrical diagram", "diagrama elétrico"),
        ("wiring diagram", "diagrama de ligações"),
        ("pneumatic diagram", "diagrama pneumático"),
        ("hydraulic diagram", "diagrama hidráulico"),
        ("main circuit", "circuito principal"),
        ("control circuit", "circuito de comando"),
        ("auxiliary", "auxiliar"),
        ("normally", "normalmente"), ("open", "aberto"), ("closed", "fechado"),
        ("hold-in", "manutenção"),
        ("trip", "desligamento"), ("tripping", "desligamento"),
        ("rated", "nominal"), ("rating", "nominal"),
        ("voltage", "tensão"), ("current", "corrente"),
        ("ampere", "ampere"), ("amp", "A"),
        ("watt", "watt"), ("watt", "W"),
        ("hertz", "herz"), ("hz", "Hz"),
        ("ohm", "ohm"),
        ("pascal", "pascal"), ("bar", "bar"),
        ("lpm", "L/min"), ("l/min", "L/min"),
        ("psi", "psi"),
        ("control", "comando"), ("command", "comando"),
        ("signal", "sinal"), ("signals", "sinais"),
        ("indicator light", "lâmpada indicadora"), ("signal lamp", "lâmpada sinalizadora"),
        ("pilot light", "lâmpada piloto"),
        ("emergency", "emergência"),
        ("trip", "desarme"), ("tripping", "desarme"),

        // ---- Espanhol → Português ----
        ("seccionador", "seccionador"),
        ("interruptor", "chave"), ("interruptores", "chaves"),
        ("pulsador", "botoeira"), ("pulsadores", "botoeiras"),
        ("contacto", "contato"), ("contactos", "contatos"),
        ("relé", "relé"),
        ("transformador", "transformador"),
        ("fusible", "fusível"),
        ("mando", "comando"), ("palanca", "alavanca"),
        ("bifasico", "bifásico"), ("trifasico", "trifásico"), ("monofasico", "monofásico"),
        ("marcha", "marcha"), ("paro", "parada"),
        ("señal", "sinal"), ("señales", "sinais"), ("luz", "luz"), ("luminoso", "luminoso"),
        ("protector", "protetor"), ("térmico", "térmico"),
        ("magnético", "magnético"), ("magnetico", "magnético"),
        ("diferencial", "diferencial"),
        ("automatico", "automático"), ("manual", "manual")
    };

    /// <summary>Traduz um nome de elemento para pt-BR (override exato + traduções compostas + glossário por palavra).</summary>
    public static string Translate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name ?? string.Empty;

        // 1) Override por nome exato (dicionário embutido, gerado pelo script).
        if (ExactOverrides.Value.TryGetValue(name, out var exact))
            return exact;

        // 2) Traduções compostas: nomes inteiros que precisam de tradução específica.
        foreach (var (from, to) in CompoundTranslations)
        {
            if (string.Equals(name, from, StringComparison.OrdinalIgnoreCase))
                return to;
        }

        var result = name;
        // 3) Glossário: aplica primeiro os termos mais longos (ex.: "one-phase" antes de "phase").
        foreach (var (from, to) in Glossary.OrderByDescending(g => g.From.Length))
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(from)}\b", m => CapitalizeLike(to, m),
                RegexOptions.IgnoreCase);
        }
        // Limpa espaços duplos que podem surgir das substituições.
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return result;
    }

    /// <summary>Aplica a capitalização da ocorrência original à tradução.</summary>
    private static string CapitalizeLike(string to, Match m)
    {
        if (string.IsNullOrEmpty(to))
            return to;
        if (char.IsUpper(m.Value[0]))
            return char.ToUpperInvariant(to[0]) + to[1..];
        return to;
    }
}
