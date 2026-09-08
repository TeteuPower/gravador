namespace Gravador.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// Uma combinação de teclas, guardada como texto legível ("Ctrl+Alt+P").
///
/// Texto e não números porque o arquivo de configuração é para ser lido por gente — mesma decisão
/// do claude-indicator, e é o formato que a tela de configurações mostra sem traduzir nada.
///
/// A conversão para o que o Windows espera (RegisterHotKey e o gancho de teclado) acontece aqui,
/// num lugar só, para os dois consumidores concordarem sobre o que "Ctrl+Shift+M" quer dizer.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public bool IsValid => VirtualKey != 0 && Modifiers != HotkeyModifiers.None;

    /// <summary>Modificadores no formato do RegisterHotKey, com MOD_NOREPEAT.</summary>
    public uint NativeModifiers
    {
        get
        {
            uint m = 0;
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) m |= 0x0001;
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) m |= 0x0002;
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) m |= 0x0004;
            if (Modifiers.HasFlag(HotkeyModifiers.Windows)) m |= 0x0008;
            // sem repetição com a tecla presa: um atalho de alternar não pode piscar
            m |= 0x4000;
            return m;
        }
    }

    public override string ToString()
    {
        if (VirtualKey == 0) return "";
        var partes = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) partes.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) partes.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) partes.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) partes.Add("Win");
        partes.Add(NomeDaTecla(VirtualKey));
        return string.Join("+", partes);
    }

    public static Hotkey Parse(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return default;

        var mods = HotkeyModifiers.None;
        uint tecla = 0;

        foreach (var bruto in texto.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var parte = bruto.Trim();
            switch (parte.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= HotkeyModifiers.Control; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win":
                case "windows": mods |= HotkeyModifiers.Windows; break;
                default:
                    var vk = TeclaPeloNome(parte);
                    if (vk != 0) tecla = vk;
                    break;
            }
        }
        return new Hotkey(mods, tecla);
    }

    // ------------------------------------------------------------------

    private static readonly Dictionary<string, uint> PorNome = MontarTabela();
    private static readonly Dictionary<uint, string> PorCodigo =
        PorNome.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);

    public static uint TeclaPeloNome(string nome) =>
        PorNome.TryGetValue(nome.Trim().ToLowerInvariant(), out var vk) ? vk : 0;

    public static string NomeDaTecla(uint vk) =>
        PorCodigo.TryGetValue(vk, out var n) ? n : $"0x{vk:X2}";

    /// <summary>Nomes que a interface oferece, na ordem em que fazem sentido numa lista.</summary>
    public static IReadOnlyList<string> TeclasDisponiveis { get; } = MontarTabela().Keys
        .Select(k => PorCodigo[PorNome[k]])
        .Distinct()
        .ToList();

    private static Dictionary<string, uint> MontarTabela()
    {
        var t = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++) t[c.ToString()] = c;
        for (var d = '0'; d <= '9'; d++) t[d.ToString()] = d;
        for (uint f = 1; f <= 24; f++) t[$"F{f}"] = 0x6F + f;      // VK_F1 = 0x70
        t["Espaço"] = 0x20; t["Space"] = 0x20;
        t["Insert"] = 0x2D; t["Delete"] = 0x2E; t["Home"] = 0x24; t["End"] = 0x23;
        t["PageUp"] = 0x21; t["PageDown"] = 0x22;
        t["Esquerda"] = 0x25; t["Cima"] = 0x26; t["Direita"] = 0x27; t["Baixo"] = 0x28;
        t["PrintScreen"] = 0x2C; t["Pause"] = 0x13; t["ScrollLock"] = 0x91;
        t["Numpad0"] = 0x60; t["Numpad1"] = 0x61; t["Numpad2"] = 0x62; t["Numpad3"] = 0x63;
        t["Numpad4"] = 0x64; t["Numpad5"] = 0x65; t["Numpad6"] = 0x66; t["Numpad7"] = 0x67;
        t["Numpad8"] = 0x68; t["Numpad9"] = 0x69;
        t["Ponto"] = 0xBE; t["Vírgula"] = 0xBC; t["Barra"] = 0xBF;
        t["Menos"] = 0xBD; t["Igual"] = 0xBB;
        return t;
    }
}
