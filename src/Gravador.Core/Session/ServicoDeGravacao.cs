using Gravador.Core.Audio;
using Gravador.Core.Claude;
using Gravador.Core.Muting;
using Gravador.Core.Screens;
using Gravador.Core.Settings;
using Gravador.Core.Transcription;

namespace Gravador.Core.Session;

/// <summary>
/// A ferramenta inteira em uma classe, sem nada de interface.
///
/// Existe para a janela e o modo linha de comando fazerem a MESMA coisa. Quando o Gravador entrar no
/// conjunto com as outras ferramentas, quem vai chamá-lo é a linha de comando; se a lógica morasse
/// no code-behind da janela, seria preciso reescrevê-la — e as duas versões divergiriam no primeiro
/// ajuste, como sempre acontece.
/// </summary>
public sealed class ServicoDeGravacao : IDisposable
{
    private readonly Gravacao _gravacao = new();
    private ITranscritorAoVivo? _aoVivo;
    private int _proximaCaptura = 1;

    public ServicoDeGravacao(AppSettings config)
    {
        Config = config;
        Mudo = new MuteWatcher(config);
        Mudo.Iniciar();

        _gravacao.EstadoMudou += e => EstadoMudou?.Invoke(e);
        _gravacao.NiveisAtualizados += n => Niveis?.Invoke(n);
        _gravacao.Progrediu += t => Progrediu?.Invoke(t);
        _gravacao.Aviso += a => Aviso?.Invoke(a);
        _gravacao.CapturaPedida += () => Capturar();
    }

    public AppSettings Config { get; private set; }
    public MuteWatcher Mudo { get; }
    public Gravacao Motor => _gravacao;
    public SessaoGravacao? Sessao => _gravacao.Sessao;
    public EstadoGravacao Estado => _gravacao.Estado;
    public TimeSpan Decorrido => _gravacao.Decorrido;
    public bool EmAndamento => _gravacao.EmAndamento;

    public event Action<EstadoGravacao>? EstadoMudou;
    public event Action<NiveisAudio>? Niveis;
    public event Action<TimeSpan>? Progrediu;
    public event Action<string>? Aviso;
    public event Action<Marca>? Marcou;

    /// <summary>Texto parcial da legenda ao vivo, quando há transcrição em tempo real.</summary>
    public event Action<string>? LegendaParcial;

    public void AplicarConfiguracao(AppSettings config)
    {
        Config = config;
        Mudo.AplicarConfiguracao(config);
    }

    // ==================================================================

    public SessaoGravacao Iniciar(string? titulo = null)
    {
        _proximaCaptura = 1;
        LigarTranscricaoAoVivo();
        var sessao = _gravacao.Iniciar(Config, Mudo, titulo);
        sessao.MarcaAdicionada += m => Marcou?.Invoke(m);
        _aoVivo?.Iniciar();
        return sessao;
    }

    private void LigarTranscricaoAoVivo()
    {
        _aoVivo?.Dispose();
        _aoVivo = Transcritores.AoVivo(Config);
        if (_aoVivo == null)
        {
            _gravacao.EscutaMicrofone = null;
            _gravacao.EscutaSistema = null;
            return;
        }

        if (!_aoVivo.Disponivel)
        {
            Aviso?.Invoke(_aoVivo.Motivo ?? "A transcrição ao vivo não está disponível nesta máquina.");
            _aoVivo.Dispose();
            _aoVivo = null;
            _gravacao.EscutaMicrofone = null;
            _gravacao.EscutaSistema = null;
            return;
        }

        _aoVivo.Reconheceu += t => Sessao?.AdicionarFala(t);
        _aoVivo.Parcial += t => LegendaParcial?.Invoke(t);

        // Só o microfone alimenta a transcrição ao vivo do Windows: ele reconhece UMA voz por vez, e
        // o áudio da reunião tem várias pessoas sobrepostas — jogar as duas fontes no mesmo motor
        // piora as duas. Para transcrever a reunião inteira existe o caminho remoto, que roda no fim.
        var taxa = Config.TaxaAmostragem;
        var canais = Config.Canais;
        _gravacao.EscutaMicrofone = (buffer, n, em) => _aoVivo?.Alimentar("microfone", buffer, n, taxa, canais, em);
        _gravacao.EscutaSistema = null;
    }

    public void Pausar() => _gravacao.Pausar();
    public void Retomar() => _gravacao.Retomar();
    public void AlternarPausa() => _gravacao.AlternarPausa();

    /// <summary>Alterna gravar/parar — o que o atalho principal faz.</summary>
    public bool PodeIniciar => _gravacao.Estado == EstadoGravacao.Parada;

    // ------------------------------------------------------------------

    /// <summary>
    /// Tira uma captura de tela e a prende na linha do tempo.
    ///
    /// Funciona com a gravação parada também: a imagem vai para a última sessão aberta, ou para uma
    /// pasta avulsa do dia. Alguém que aperta o atalho antes de dar play não deveria perder o slide.
    /// </summary>
    public ImagemCapturada? Capturar()
    {
        var sessao = Sessao;
        var momento = _gravacao.Decorrido;

        var pasta = sessao?.PastaCapturas
            ?? Path.Combine(Config.PastaSaidaEfetiva, "capturas-avulsas", DateTime.Now.ToString("yyyy-MM-dd"));

        var resultado = ScreenCapture.Capturar(Config, pasta, momento, _proximaCaptura);
        if (resultado.Imagem is not { } imagem)
        {
            Aviso?.Invoke(resultado.Erro ?? "Não deu para capturar a tela agora.");
            return null;
        }

        _proximaCaptura++;
        sessao?.AdicionarCaptura(momento, imagem.Caminho, imagem.Janela);
        return imagem;
    }

    public Marca? Marcar(string? texto = null)
    {
        var sessao = Sessao;
        if (sessao == null || !EmAndamento) return null;
        return sessao.AdicionarMarcador(_gravacao.Decorrido, texto);
    }

    public void AlternarMudoManual() => Mudo.AlternarManual();

    // ==================================================================

    /// <summary>
    /// Encerra a gravação e faz o pós-processamento: converte o áudio, transcreve se for o caso,
    /// grava a transcrição em Markdown e pede o resumo ao Claude quando isso estiver ligado.
    /// </summary>
    public async Task<ResultadoDaGravacao> PararAsync(IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        _aoVivo?.Encerrar();

        var resultado = await _gravacao.PararAsync(etapa, ct).ConfigureAwait(false);
        var sessao = resultado.Sessao;

        _aoVivo?.Dispose();
        _aoVivo = null;

        await TranscreverArquivosAsync(sessao, etapa, ct).ConfigureAwait(false);

        Analista.GravarTranscricao(sessao);
        sessao.Salvar();

        if (Config.ResumirAoFinal)
        {
            var resposta = await new Analista().ResumirAsync(sessao, Config, etapa, ct).ConfigureAwait(false);
            if (!resposta.Ok && resposta.Erro != null)
                Aviso?.Invoke("O resumo do Claude não saiu: " + resposta.Erro);
        }

        return resultado;
    }

    /// <summary>
    /// Transcrição de arquivo (o caminho remoto). Roda sobre a trilha que responde à pergunta certa:
    /// a mixada, quando existe, porque é onde estão as duas pontas da conversa.
    /// </summary>
    private async Task TranscreverArquivosAsync(SessaoGravacao sessao, IProgress<string>? etapa, CancellationToken ct)
    {
        using var motor = Transcritores.DeArquivo(Config);
        if (motor == null) return;

        if (!motor.Disponivel)
        {
            Aviso?.Invoke(motor.Motivo ?? "A transcrição não está configurada.");
            return;
        }

        var alvo = sessao.Arquivos.Mixado ?? sessao.Arquivos.Sistema ?? sessao.Arquivos.Microfone;
        if (alvo == null) return;

        var caminho = Path.Combine(sessao.Pasta, alvo);
        var fonte = alvo.StartsWith("microfone", StringComparison.OrdinalIgnoreCase) ? "microfone" : "reunião";

        try
        {
            var trechos = await motor.TranscreverAsync(caminho, fonte, etapa, ct).ConfigureAwait(false);
            foreach (var t in trechos) sessao.AdicionarFala(t);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Aviso?.Invoke("A transcrição falhou: " + ex.Message);
        }
    }

    /// <summary>Gera (ou refaz) o resumo de uma sessão já gravada.</summary>
    public Task<RespostaDoClaude> ResumirAsync(SessaoGravacao sessao, IProgress<string>? etapa = null,
        CancellationToken ct = default) => new Analista().ResumirAsync(sessao, Config, etapa, ct);

    public void Dispose()
    {
        _aoVivo?.Dispose();
        _gravacao.Dispose();
        Mudo.Dispose();
    }
}
