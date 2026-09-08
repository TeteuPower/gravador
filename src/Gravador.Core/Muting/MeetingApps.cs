using Gravador.Core.Settings;

namespace Gravador.Core.Muting;

/// <summary>
/// Um aplicativo de reunião e o atalho com que ele liga e desliga o próprio microfone.
/// </summary>
/// <param name="Nome">Como aparece na interface.</param>
/// <param name="Processos">Executáveis que identificam o aplicativo em primeiro plano.</param>
/// <param name="Atalho">A combinação que alterna o mudo.</param>
/// <param name="Global">
/// O atalho funciona com o aplicativo em segundo plano. Quando falso, a combinação só conta se o
/// aplicativo estiver em primeiro plano — sem isso, um Ctrl+Shift+M digitado no Word marcaria a
/// reunião como muda.
/// </param>
public sealed record AppDeReuniao(string Nome, string[] Processos, string Atalho, bool Global)
{
    public Hotkey Hotkey { get; } = Hotkey.Parse(Atalho);
}

/// <summary>
/// O catálogo de atalhos de mudo que a ferramenta sabe reconhecer.
///
/// Esta lista é uma aposta calibrada, não uma verdade: são os atalhos de fábrica de cada aplicativo,
/// e quem os trocou nas preferências precisa dizer isso em Configurações › Microfone. O acerto da
/// detecção depende disso, e a interface deixa claro que este sinal é palpite — ver
/// <see cref="OrigemDoMudo"/>.
/// </summary>
public static class MeetingApps
{
    public static IReadOnlyList<AppDeReuniao> Conhecidos { get; } = new[]
    {
        // Ctrl+Shift+M é o atalho do Teams desde sempre, e vale só com a janela em foco.
        new AppDeReuniao("Microsoft Teams", ["ms-teams", "teams"], "Ctrl+Shift+M", Global: false),

        // Zoom: Alt+A funciona com a janela em segundo plano quando "atalhos globais" está ligado,
        // que é o padrão da instalação para desktop.
        new AppDeReuniao("Zoom", ["zoom", "cpthost"], "Alt+A", Global: true),

        // Google Meet e demais reuniões no navegador. Ctrl+D é do Meet; o navegador precisa estar
        // em foco, então nunca dispara por acidente com outro programa na frente.
        new AppDeReuniao("Google Meet (navegador)", ["chrome", "msedge", "firefox", "brave", "opera"], "Ctrl+D", Global: false),

        // Discord: Ctrl+Shift+M é o padrão e é global.
        new AppDeReuniao("Discord", ["discord", "discordptb", "discordcanary"], "Ctrl+Shift+M", Global: true),

        // Slack (huddles).
        new AppDeReuniao("Slack", ["slack"], "Ctrl+Shift+Espaço", Global: false),

        // Webex.
        new AppDeReuniao("Webex", ["webex", "ciscocollabhost", "webexmta"], "Ctrl+M", Global: false),

        // Google Chat / Meet PWA instalado tem o mesmo Ctrl+D do navegador.
        new AppDeReuniao("GoTo / GoToMeeting", ["goto", "gotomeeting", "g2mui"], "Ctrl+Alt+A", Global: true),
    };

    /// <summary>Todos os nomes de processo que interessam vigiar.</summary>
    public static IReadOnlySet<string> ProcessosDeReuniao { get; } =
        Conhecidos.SelectMany(a => a.Processos).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static AppDeReuniao? PeloProcesso(string? processo)
    {
        if (string.IsNullOrWhiteSpace(processo)) return null;
        var nome = Path.GetFileNameWithoutExtension(processo);
        return Conhecidos.FirstOrDefault(a => a.Processos.Contains(nome, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// De onde veio a informação de que o microfone está mudo. A interface mostra isto porque as duas
/// respostas têm confiança muito diferente, e quem vai usar a gravação precisa saber qual é qual.
/// </summary>
public enum OrigemDoMudo
{
    /// <summary>Não está mudo.</summary>
    Nenhuma,

    /// <summary>O endpoint de captura está mudo no Windows. Isto é fato: nenhum áudio seu sai daqui.</summary>
    Dispositivo,

    /// <summary>Volume do microfone em zero no Windows. Também é fato.</summary>
    VolumeZerado,

    /// <summary>O atalho de mudo de um aplicativo de reunião foi pressionado. É palpite.</summary>
    AtalhoDoApp,

    /// <summary>Você marcou como mudo na ferramenta (atalho ou botão). Vale mais que o palpite.</summary>
    Manual,
}
