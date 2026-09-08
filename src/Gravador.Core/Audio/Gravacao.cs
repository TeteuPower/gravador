using System.Diagnostics;
using Gravador.Core.Muting;
using Gravador.Core.Session;
using Gravador.Core.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Timer = System.Threading.Timer;

namespace Gravador.Core.Audio;

public enum EstadoGravacao
{
    Parada,
    Gravando,
    Pausada,

    /// <summary>Parou de capturar e está fechando os arquivos (codificação do MP3).</summary>
    Finalizando,
}

/// <summary>Leitura instantânea dos medidores, para a interface desenhar as barras.</summary>
public sealed record NiveisAudio(float Sistema, float Microfone, bool AlguemFalando, bool VoceFalando);

public sealed record ResultadoDaGravacao(SessaoGravacao Sessao, TimeSpan Duracao, IReadOnlyList<string> Avisos);

/// <summary>
/// O motor: liga as capturas, mantém a linha do tempo e fecha os arquivos.
///
/// Nada aqui toca em interface. Isto é de propósito — a mesma classe é usada pela janela do
/// aplicativo e pelo modo linha de comando, que é como esta ferramenta vai ser chamada quando
/// estiver junto com as outras.
/// </summary>
public sealed class Gravacao : IDisposable
{
    private readonly object _trava = new();
    private readonly Stopwatch _relogio = new();

    private AppSettings _config = new();
    private MuteWatcher? _mudo;
    private Trilha? _sistema;
    private Trilha? _microfone;
    private Mixer? _mixador;
    private Timer? _pulso;
    private Timer? _capturaAutomatica;
    private readonly List<string> _avisos = new();
    private volatile EstadoGravacao _estado = EstadoGravacao.Parada;

    public event Action<EstadoGravacao>? EstadoMudou;
    public event Action<NiveisAudio>? NiveisAtualizados;
    public event Action<TimeSpan>? Progrediu;
    public event Action<string>? Aviso;

    /// <summary>Pedido de captura automática. Quem cuida de tela é outra camada.</summary>
    public event Action? CapturaPedida;

    public EstadoGravacao Estado => _estado;
    public SessaoGravacao? Sessao { get; private set; }
    public TimeSpan Decorrido => _relogio.Elapsed;
    public bool EmAndamento => _estado is EstadoGravacao.Gravando or EstadoGravacao.Pausada;

    /// <summary>Formato real que cada dispositivo entregou, para a tela de diagnóstico.</summary>
    public string? FormatoSistema => _sistema?.FormatoDaOrigem;
    public string? FormatoMicrofone => _microfone?.FormatoDaOrigem;

    // ==================================================================

    /// <summary>
    /// Liga a gravação. Devolve a sessão criada, ou lança quando não há nada gravável — é melhor
    /// falhar aqui, com a mensagem, do que produzir uma pasta com um WAV vazio dentro.
    /// </summary>
    public SessaoGravacao Iniciar(AppSettings config, MuteWatcher mudo, string? titulo = null)
    {
        lock (_trava)
        {
            if (EmAndamento) throw new InvalidOperationException("Já existe uma gravação em andamento.");

            _config = config;
            _mudo = mudo;
            _avisos.Clear();

            var sessao = SessaoGravacao.Criar(config, titulo);
            Sessao = sessao;

            var taxa = config.TaxaAmostragem;
            var canais = config.Canais;
            var querSeparadas = config.Trilhas is ModoTrilhas.Separadas or ModoTrilhas.Ambas;
            var querMixada = config.Trilhas is ModoTrilhas.Mixada or ModoTrilhas.Ambas;

            try
            {
                // Os dispositivos são resolvidos antes de qualquer arquivo ser aberto porque a
                // CONTAGEM deles muda o que precisa ser gravado: com uma fonte só, a trilha separada
                // já É a mistura, e abrir um mixador para copiar um arquivo em outro seria gastar
                // disco e CPU de uma máquina modesta à toa.
                var reproducao = config.GravarSistema ? DeviceCatalog.ResolverReproducao(config.DispositivoSistema) : null;
                var captura = config.GravarMicrofone ? DeviceCatalog.ResolverCaptura(config.DispositivoMicrofone) : null;

                if (config.GravarSistema && reproducao == null)
                    Avisar("Não achei um dispositivo de reprodução para gravar o áudio do computador.");
                if (config.GravarMicrofone && captura == null)
                    Avisar("Não achei um microfone. A gravação segue só com o áudio do computador.");

                var fontes = (reproducao != null ? 1 : 0) + (captura != null ? 1 : 0);
                if (fontes == 0)
                    throw new InvalidOperationException(
                        "Nenhum dispositivo de áudio disponível para gravar. Confira as configurações de som do Windows.");

                var mixarDeVerdade = querMixada && fontes > 1;
                // Com uma fonte só e "somente mixado" pedido, a trilha é o entregável: ela ganha o
                // arquivo, e ele se chama mixado.wav para a sessão continuar tendo o nome esperado.
                var trilhaEhOEntregavel = !querSeparadas && !mixarDeVerdade;
                var gravarTrilhas = querSeparadas || trilhaEhOEntregavel;

                string Caminho(string nome) => Path.Combine(sessao.Pasta, trilhaEhOEntregavel ? "mixado.wav" : nome);

                if (reproducao != null)
                    _sistema = MontarSistema(reproducao, config, gravarTrilhas ? Caminho("sistema.wav") : null);
                if (captura != null)
                    _microfone = MontarMicrofone(captura, config, gravarTrilhas ? Caminho("microfone.wav") : null);

                if (_sistema == null && _microfone == null)
                    throw new InvalidOperationException(
                        "Nenhum dispositivo de áudio pôde ser aberto. Confira as configurações de som do Windows.");

                if (mixarDeVerdade && _sistema != null && _microfone != null)
                {
                    _mixador = new Mixer(Path.Combine(sessao.Pasta, "mixado.wav"), taxa, canais);
                    _sistema.ParaMixagem = _mixador.Adicionar;
                    _microfone.ParaMixagem = _mixador.Adicionar;
                }

                LigarRegrasDeMudo();

                _relogio.Restart();
                _estado = EstadoGravacao.Gravando;

                _sistema?.Iniciar();
                _microfone?.Iniciar();

                _pulso = new Timer(Pulsar, null, 200, 200);
                if (config.CapturaAutomaticaSegundos > 0)
                {
                    var ms = config.CapturaAutomaticaSegundos * 1000;
                    _capturaAutomatica = new Timer(_ => CapturaPedida?.Invoke(), null, ms, ms);
                }

                if (mudo.Estado.Mudo)
                    sessao.RegistrarMudo(TimeSpan.Zero, true, mudo.Estado.Descricao, mudo.Estado.Certeza);
                mudo.Mudou += AoMudarMudo;
            }
            catch
            {
                DesmontarTudo();
                _estado = EstadoGravacao.Parada;
                throw;
            }

            EstadoMudou?.Invoke(_estado);
            return sessao;
        }
    }

    private Trilha? MontarSistema(MMDevice device, AppSettings config, string? arquivo)
    {
        try
        {
            var trilha = new Trilha("sistema", new WasapiLoopbackCapture(device), arquivo,
                config.TaxaAmostragem, config.Canais, config.LimiarVozDb, Relogio)
            {
                Ganho = (float)config.GanhoSistema,
            };
            trilha.Falhou += AoFalharTrilha;
            return trilha;
        }
        catch (Exception ex)
        {
            Avisar($"O áudio do computador não pôde ser capturado ({device.FriendlyName}): {ex.Message}");
            return null;
        }
    }

    private Trilha? MontarMicrofone(MMDevice device, AppSettings config, string? arquivo)
    {
        try
        {
            var trilha = new Trilha("microfone", new WasapiCapture(device), arquivo,
                config.TaxaAmostragem, config.Canais, config.LimiarVozDb, Relogio)
            {
                Ganho = (float)config.GanhoMicrofone,
            };
            trilha.Falhou += AoFalharTrilha;
            return trilha;
        }
        catch (Exception ex)
        {
            Avisar($"O microfone não pôde ser aberto ({device.FriendlyName}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Onde a decisão sobre o mudo vira ação no áudio.
    ///
    /// Só o microfone é afetado: silenciar o áudio do computador porque VOCÊ está mudo não faria
    /// sentido nenhum — a reunião continua acontecendo.
    /// </summary>
    private void LigarRegrasDeMudo()
    {
        if (_microfone == null) return;
        var acao = _config.QuandoMudo;
        if (acao == AcaoQuandoMudo.SomenteMarcar) return;

        _microfone.SilenciarNaMixagem = () => _mudo?.Mudo == true;
        if (acao == AcaoQuandoMudo.SilenciarEmTudo)
            _microfone.SilenciarNaTrilha = () => _mudo?.Mudo == true;
    }

    /// <summary>Tempo decorrido, ou null quando pausado — a trilha usa isso para descartar o buffer.</summary>
    private TimeSpan? Relogio() => _estado == EstadoGravacao.Gravando ? _relogio.Elapsed : null;

    private void AoMudarMudo(EstadoDoMudo estado)
    {
        if (Sessao is not { } s || !EmAndamento) return;
        s.RegistrarMudo(_relogio.Elapsed, estado.Mudo, estado.Descricao, estado.Certeza);
    }

    private void AoFalharTrilha(string nome, Exception ex) =>
        Avisar($"A trilha '{nome}' teve um problema: {ex.Message}");

    private void Avisar(string texto)
    {
        lock (_avisos) _avisos.Add(texto);
        Aviso?.Invoke(texto);
    }

    // ==================================================================

    private void Pulsar(object? _)
    {
        try
        {
            if (_estado == EstadoGravacao.Gravando)
            {
                // Drena o mixador com um segundo de atraso: dá tempo de a trilha mais lenta entregar
                // o buffer dela antes de a região ir para o disco.
                var ate = (long)((_relogio.Elapsed.TotalSeconds - 1.0) * _config.TaxaAmostragem);
                if (ate > 0) _mixador?.Drenar(ate);
            }

            NiveisAtualizados?.Invoke(new NiveisAudio(
                _sistema?.Pico ?? 0,
                _microfone?.Pico ?? 0,
                _sistema?.Falando ?? false,
                _microfone?.Falando ?? false));

            Progrediu?.Invoke(_relogio.Elapsed);
        }
        catch
        {
            // um pulso perdido não derruba a gravação
        }
    }

    public void Pausar()
    {
        lock (_trava)
        {
            if (_estado != EstadoGravacao.Gravando) return;
            _relogio.Stop();
            _estado = EstadoGravacao.Pausada;
            _sistema?.ZerarMedidor();
            _microfone?.ZerarMedidor();
            Sessao?.Adicionar(new Marca(_relogio.Elapsed.TotalSeconds, TipoDeMarca.Pausa));
        }
        EstadoMudou?.Invoke(_estado);
    }

    public void Retomar()
    {
        lock (_trava)
        {
            if (_estado != EstadoGravacao.Pausada) return;
            Sessao?.Adicionar(new Marca(_relogio.Elapsed.TotalSeconds, TipoDeMarca.Retomada));
            _estado = EstadoGravacao.Gravando;
            _relogio.Start();
        }
        EstadoMudou?.Invoke(_estado);
    }

    public void AlternarPausa()
    {
        if (_estado == EstadoGravacao.Gravando) Pausar();
        else if (_estado == EstadoGravacao.Pausada) Retomar();
    }

    // ==================================================================

    /// <summary>
    /// Fecha a gravação: para as capturas, nivela as trilhas, fecha o mixador e converte para MP3.
    ///
    /// A conversão é a parte demorada (dezenas de segundos numa reunião longa), então tudo isto
    /// roda fora da thread de interface e relata o andamento por <paramref name="etapa"/>.
    /// </summary>
    public async Task<ResultadoDaGravacao> PararAsync(IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        SessaoGravacao sessao;
        TimeSpan duracao;

        lock (_trava)
        {
            if (!EmAndamento || Sessao is null)
                throw new InvalidOperationException("Não há gravação em andamento.");

            sessao = Sessao;
            _estado = EstadoGravacao.Finalizando;
            _relogio.Stop();
            duracao = _relogio.Elapsed;

            if (_mudo != null) _mudo.Mudou -= AoMudarMudo;

            _pulso?.Dispose(); _pulso = null;
            _capturaAutomatica?.Dispose(); _capturaAutomatica = null;

            _sistema?.Parar();
            _microfone?.Parar();
        }

        EstadoMudou?.Invoke(_estado);
        etapa?.Report("Fechando as trilhas...");

        // O WASAPI ainda pode entregar um último buffer depois do StopRecording.
        await Task.Delay(250, ct).ConfigureAwait(false);

        _sistema?.Nivelar(duracao);
        _microfone?.Nivelar(duracao);
        _mixador?.Finalizar((long)(duracao.TotalSeconds * _config.TaxaAmostragem));

        var wavSistema = _sistema?.CaminhoWav;
        var wavMicrofone = _microfone?.CaminhoWav;
        var wavMixado = _mixador?.Caminho;

        DesmontarTudo();

        sessao.Encerrar(duracao);

        await Task.Run(() =>
        {
            Guardar(sessao, Finalizar(wavSistema, "áudio do computador", etapa, ct), padrao: 0);
            Guardar(sessao, Finalizar(wavMicrofone, "microfone", etapa, ct), padrao: 1);
            Guardar(sessao, Finalizar(wavMixado, "mistura", etapa, ct), padrao: 2);
        }, ct).ConfigureAwait(false);

        sessao.Salvar();

        _estado = EstadoGravacao.Parada;
        EstadoMudou?.Invoke(_estado);
        etapa?.Report("Pronto.");

        List<string> avisos;
        lock (_avisos) avisos = _avisos.ToList();
        return new ResultadoDaGravacao(sessao, duracao, avisos);
    }

    /// <summary>
    /// Guarda o arquivo produzido no campo certo da sessão. O NOME manda, não a origem: com uma
    /// fonte só, a trilha foi gravada direto como <c>mixado.wav</c>, e ela é o entregável.
    /// </summary>
    private static void Guardar(SessaoGravacao sessao, string? nome, int padrao)
    {
        if (nome == null) return;
        var basico = Path.GetFileNameWithoutExtension(nome);
        if (basico.Equals("mixado", StringComparison.OrdinalIgnoreCase)) sessao.Arquivos.Mixado = nome;
        else if (basico.Equals("sistema", StringComparison.OrdinalIgnoreCase)) sessao.Arquivos.Sistema = nome;
        else if (basico.Equals("microfone", StringComparison.OrdinalIgnoreCase)) sessao.Arquivos.Microfone = nome;
        else if (padrao == 0) sessao.Arquivos.Sistema = nome;
        else if (padrao == 1) sessao.Arquivos.Microfone = nome;
        else sessao.Arquivos.Mixado = nome;
    }

    /// <summary>Converte um WAV para MP3, ou o mantém como está. Devolve o caminho relativo à sessão.</summary>
    private string? Finalizar(string? wav, string rotulo, IProgress<string>? etapa, CancellationToken ct)
    {
        if (wav == null || !File.Exists(wav)) return null;

        if (_config.Formato == FormatoSaida.Wav)
            return Path.GetFileName(wav);

        etapa?.Report($"Convertendo o {rotulo} para MP3...");
        var r = AudioEncoder.ParaMp3(wav, _config.Mp3Kbps, apagarWav: !_config.ManterWav,
            progresso: p => etapa?.Report($"Convertendo o {rotulo} para MP3... {p * 100:0}%"), ct: ct);

        if (!r.Ok && r.Erro != null)
            Avisar($"O {rotulo} ficou em WAV: {r.Erro}");

        return Path.GetFileName(r.Caminho);
    }

    private void DesmontarTudo()
    {
        _sistema?.Dispose(); _sistema = null;
        _microfone?.Dispose(); _microfone = null;
        _mixador?.Dispose(); _mixador = null;
        _pulso?.Dispose(); _pulso = null;
        _capturaAutomatica?.Dispose(); _capturaAutomatica = null;
    }

    /// <summary>
    /// Encerramento de emergência: usado quando o app está sendo fechado e não dá para esperar a
    /// conversão. Os WAV ficam gravados e íntegros — o cabeçalho já era atualizado durante a
    /// gravação exatamente para este caso.
    /// </summary>
    public void AbortarSalvando()
    {
        lock (_trava)
        {
            if (!EmAndamento) return;
            var duracao = _relogio.Elapsed;
            _relogio.Stop();
            if (_mudo != null) _mudo.Mudou -= AoMudarMudo;
            _sistema?.Parar();
            _microfone?.Parar();
            _sistema?.Nivelar(duracao);
            _microfone?.Nivelar(duracao);
            _mixador?.Finalizar((long)(duracao.TotalSeconds * _config.TaxaAmostragem));

            if (Sessao is { } s)
            {
                Guardar(s, NomeSe(_sistema?.CaminhoWav), padrao: 0);
                Guardar(s, NomeSe(_microfone?.CaminhoWav), padrao: 1);
                Guardar(s, NomeSe(_mixador?.Caminho), padrao: 2);
                s.Encerrar(duracao);
            }
            DesmontarTudo();
            _estado = EstadoGravacao.Parada;
        }
        EstadoMudou?.Invoke(_estado);
    }

    private static string? NomeSe(string? caminho) =>
        caminho != null && File.Exists(caminho) ? Path.GetFileName(caminho) : null;

    public void Dispose()
    {
        if (EmAndamento) AbortarSalvando();
        DesmontarTudo();
    }
}
