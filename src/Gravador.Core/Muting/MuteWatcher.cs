using System.Diagnostics;
using Gravador.Core.Audio;
using Gravador.Core.Settings;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Gravador.Core.Muting;

/// <summary>O que se sabe sobre o microfone agora.</summary>
/// <param name="Mudo">Conclusão final: o que a gravação deve fazer.</param>
/// <param name="Origem">De onde veio a conclusão — e, portanto, o quanto ela é confiável.</param>
/// <param name="Certeza">
/// Verdadeiro quando a resposta veio do Windows (dispositivo mudo ou volume zerado) e não de
/// palpite. A interface pinta os dois casos de cores diferentes.
/// </param>
/// <param name="EmChamada">Algum aplicativo de reunião está com o microfone aberto.</param>
/// <param name="AppEmChamada">Qual.</param>
/// <param name="PicoDoMicrofone">Nível do endpoint, de 0 a 1, mesmo com a gravação parada.</param>
public sealed record EstadoDoMudo(
    bool Mudo,
    OrigemDoMudo Origem,
    bool Certeza,
    bool EmChamada,
    string? AppEmChamada,
    float PicoDoMicrofone)
{
    public static readonly EstadoDoMudo Desconhecido = new(false, OrigemDoMudo.Nenhuma, false, false, null, 0);

    public string Descricao => Origem switch
    {
        OrigemDoMudo.Dispositivo => "mudo no Windows",
        OrigemDoMudo.VolumeZerado => "volume do microfone em zero",
        OrigemDoMudo.AtalhoDoApp => AppEmChamada is { } a ? $"provavelmente mudo no {a}" : "provavelmente mudo no aplicativo",
        OrigemDoMudo.Manual => "marcado como mudo por você",
        _ => "aberto",
    };
}

/// <summary>
/// Responde "o microfone está mudo?" juntando quatro sinais de confiabilidade bem diferente.
///
/// A pergunta parece simples e não é. Existem dois mudos distintos no Windows:
///
/// 1. O do DISPOSITIVO — o mudo do endpoint de captura. Este é fato verificável: quando ele está
///    ligado, nenhum aplicativo recebe áudio nenhum seu, e o WASAPI entrega silêncio digital.
///
/// 2. O do APLICATIVO — o botão de mudo do Teams, do Meet, do Zoom. Este NÃO existe do lado do
///    Windows: o aplicativo continua lendo o microfone normalmente e só deixa de mandar o seu áudio
///    para os outros participantes. Nenhuma API conta isso, e é justamente o que a pessoa quer
///    saber ao gravar uma reunião — "o que eu falei aqui os outros ouviram?".
///
/// Para o segundo, a ferramenta observa o atalho de mudo do aplicativo em primeiro plano (ver
/// <see cref="KeyboardObserver"/>) e trata o resultado pelo que ele é: um palpite, marcado como tal
/// na linha do tempo e sobrescrevível por você a qualquer momento. É um palpite que erra em dois
/// casos conhecidos — mudo pelo clique no botão em vez do atalho, e atalho trocado nas preferências
/// do aplicativo — e é por isso que existe o atalho manual de correção.
/// </summary>
public sealed class MuteWatcher : IDisposable
{
    private readonly KeyboardObserver _teclado = new();
    private readonly object _trava = new();
    private Thread? _thread;
    private volatile bool _parar;

    private MMDevice? _microfone;
    private AppSettings _config;

    private bool _presumido;
    private OrigemDoMudo _origemPresumida = OrigemDoMudo.Nenhuma;

    private EstadoDoMudo _estado = EstadoDoMudo.Desconhecido;

    public MuteWatcher(AppSettings config)
    {
        _config = config;
        _teclado.Reconhecida += AoReconhecerAtalho;
    }

    /// <summary>Disparado quando a conclusão muda (não a cada leitura).</summary>
    public event Action<EstadoDoMudo>? Mudou;

    public EstadoDoMudo Estado
    {
        get { lock (_trava) return _estado; }
    }

    public bool Mudo => Estado.Mudo;

    public void Iniciar()
    {
        if (_thread != null) return;
        AplicarConfiguracao(_config);
        _parar = false;
        _thread = new Thread(Laco) { IsBackground = true, Name = "Gravador.Mudo" };
        _thread.Start();
    }

    public void AplicarConfiguracao(AppSettings config)
    {
        _config = config;
        lock (_trava)
        {
            _microfone?.Dispose();
            _microfone = DeviceCatalog.ResolverCaptura(config.DispositivoMicrofone);
        }

        if (config.DetectarMudoDeApps)
        {
            var combinacoes = MeetingApps.Conhecidos.Select(a => a.Hotkey)
                .Concat(config.AtalhosDeMudoExtras.Select(Hotkey.Parse))
                .Where(h => h.IsValid);
            _teclado.Vigiar(combinacoes);
            _teclado.Iniciar();
        }
        else
        {
            _teclado.Vigiar([]);
        }
    }

    /// <summary>Você corrigindo o palpite: o atalho "estou mudo/aberto" e o botão da interface.</summary>
    public void DefinirManual(bool mudo)
    {
        lock (_trava)
        {
            _presumido = mudo;
            _origemPresumida = mudo ? OrigemDoMudo.Manual : OrigemDoMudo.Nenhuma;
        }
        Reavaliar();
    }

    public void AlternarManual() => DefinirManual(!Estado.Mudo);

    // ------------------------------------------------------------------

    private void AoReconhecerAtalho(TeclaObservada tecla)
    {
        var app = MeetingApps.Conhecidos.FirstOrDefault(a =>
            a.Hotkey == tecla.Combinacao &&
            (a.Global || a.Processos.Contains(tecla.ProcessoEmFoco, StringComparer.OrdinalIgnoreCase)));

        // Combinação que só vale com a janela na frente e o foco é outro: não é mudo de reunião,
        // é alguém usando Ctrl+D no navegador para favoritar uma página.
        if (app == null && !_config.AtalhosDeMudoExtras.Contains(tecla.Combinacao.ToString())) return;

        lock (_trava)
        {
            _presumido = !_presumido;
            _origemPresumida = _presumido ? OrigemDoMudo.AtalhoDoApp : OrigemDoMudo.Nenhuma;
        }
        Reavaliar();
    }

    private void Laco()
    {
        var ciclo = 0;
        while (!_parar)
        {
            try
            {
                Reavaliar(escanearSessoes: ciclo % 12 == 0);
            }
            catch
            {
                // dispositivo arrancado no meio: a próxima volta resolve
            }
            ciclo++;
            Thread.Sleep(250);
        }
    }

    private bool _emChamada;
    private string? _appEmChamada;

    private void Reavaliar(bool escanearSessoes = false)
    {
        bool mudoDoDispositivo = false, volumeZero = false;
        float pico = 0;

        MMDevice? mic;
        lock (_trava) mic = _microfone;

        if (mic != null)
        {
            try
            {
                var vol = mic.AudioEndpointVolume;
                mudoDoDispositivo = vol.Mute;
                volumeZero = vol.MasterVolumeLevelScalar <= 0.0001f;
                pico = mic.AudioMeterInformation.MasterPeakValue;
            }
            catch
            {
                // endpoint sumiu; tenta reconectar na próxima varredura de sessões
                escanearSessoes = true;
            }
        }

        if (escanearSessoes)
        {
            if (mic == null)
            {
                var novo = DeviceCatalog.ResolverCaptura(_config.DispositivoMicrofone);
                lock (_trava) { _microfone = novo; }
            }
            AtualizarChamada(mic);
        }

        bool presumido;
        OrigemDoMudo origemPresumida;
        lock (_trava) { presumido = _presumido; origemPresumida = _origemPresumida; }

        var (mudo, origem, certeza) =
            mudoDoDispositivo ? (true, OrigemDoMudo.Dispositivo, true)
            : volumeZero ? (true, OrigemDoMudo.VolumeZerado, true)
            : presumido ? (true, origemPresumida, false)
            : (false, OrigemDoMudo.Nenhuma, true);

        var novoEstado = new EstadoDoMudo(mudo, origem, certeza, _emChamada, _appEmChamada, pico);

        EstadoDoMudo anterior;
        lock (_trava)
        {
            anterior = _estado;
            _estado = novoEstado;
        }

        // O pico muda toda hora e não é "mudança de estado": avisar por ele encheria a interface de
        // eventos. Só o que altera a decisão dispara o evento; o pico é lido de Estado quando a
        // barra precisa dele.
        if (anterior.Mudo != novoEstado.Mudo
            || anterior.Origem != novoEstado.Origem
            || anterior.EmChamada != novoEstado.EmChamada
            || anterior.AppEmChamada != novoEstado.AppEmChamada)
        {
            Mudou?.Invoke(novoEstado);
        }
    }

    /// <summary>
    /// Quem está com o microfone aberto agora, pelas sessões de áudio do endpoint de captura.
    ///
    /// Serve para duas coisas: mostrar "Teams está na sua frente" na interface e, principalmente,
    /// zerar o palpite de mudo quando a chamada acaba — a reunião seguinte começa do zero em vez de
    /// herdar um "mudo" que ficou pendurado da anterior.
    /// </summary>
    private void AtualizarChamada(MMDevice? mic)
    {
        if (mic == null) { _emChamada = false; _appEmChamada = null; return; }

        string? app = null;
        try
        {
            var gerente = mic.AudioSessionManager;
            gerente.RefreshSessions();
            var sessoes = gerente.Sessions;
            for (var i = 0; i < sessoes.Count && app == null; i++)
            {
                var s = sessoes[i];
                if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                var pid = (int)s.GetProcessID;
                if (pid <= 0) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (MeetingApps.PeloProcesso(p.ProcessName) is { } conhecido) app = conhecido.Nome;
                }
                catch
                {
                    // processo morreu entre listar e abrir
                }
            }
        }
        catch
        {
            // sem permissão para listar sessões: fica sem esta pista
        }

        var estavaEmChamada = _emChamada;
        _emChamada = app != null;
        _appEmChamada = app;

        if (estavaEmChamada && !_emChamada)
        {
            lock (_trava)
            {
                if (_origemPresumida == OrigemDoMudo.AtalhoDoApp)
                {
                    _presumido = false;
                    _origemPresumida = OrigemDoMudo.Nenhuma;
                }
            }
        }
    }

    public void Dispose()
    {
        _parar = true;
        _thread?.Join(1000);
        _thread = null;
        _teclado.Dispose();
        lock (_trava)
        {
            _microfone?.Dispose();
            _microfone = null;
        }
    }
}
