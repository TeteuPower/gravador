using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Core.Legendas;

/// <summary>Uma linha de legenda pronta para desenhar.</summary>
/// <param name="Traduzido">O texto já em português. É a linha principal.</param>
/// <param name="Original">O mesmo trecho como foi falado. Vai embaixo, apagado, quando pedido.</param>
/// <param name="Provisorio">O que o whisper ainda pode mudar de ideia. Nunca é traduzido.</param>
public readonly record struct LinhaDeLegenda(string Traduzido, string Original, string Provisorio);

/// <summary>
/// Junta as duas metades da legenda: quem ouve e quem traduz.
///
/// Duas regras decidem o desenho, e as duas saíram de ver a coisa funcionando errado antes.
///
/// **Só o texto firme é traduzido.** O provisório — as últimas palavras, que o whisper ainda revê —
/// fica na tela no idioma original e apagado. Traduzir fragmento dá português ruim, porque gênero e
/// concordância dependem do fim da frase; e o provisório muda a cada passada, então traduzido ele
/// piscaria entre versões diferentes da mesma frase, que é o defeito que o acordo entre passadas
/// existe para evitar.
///
/// **A tradução é por frase, não pelo acumulado.** A primeira versão traduzia todo o texto firme a
/// cada mudança: como ele só crescia, cada tradução ficava mais cara que a anterior, e em um minuto
/// de apresentação a legenda já traduzia um parágrafo inteiro por passada e não alcançava mais a
/// fala. Agora a frase fechada é traduzida UMA vez e guardada; só a frase em andamento é
/// retraduzida enquanto cresce, e uma frase tem tamanho limitado por definição.
///
/// A fila é de UM: se chega texto novo enquanto a tradução anterior roda, a intermediária é
/// descartada e só a mais recente é traduzida. Sem isso, um tradutor de 800 ms atrás de um
/// reconhecedor de 700 ms acumula fila para sempre e a legenda se afasta da fala sem nunca alcançar.
/// </summary>
public sealed class ServicoDeLegenda : IDisposable
{
    /// <summary>
    /// Quantas frases já fechadas continuam na tela, além da que está em andamento.
    ///
    /// Uma. Com o teto de 90 caracteres por frase, isso dá no máximo ~180 caracteres na tela — as
    /// duas ou três linhas de uma legenda de verdade. Guardar mais faria o texto rolar para fora do
    /// rodapé e ninguém acompanharia.
    /// </summary>
    private const int FrasesNaTela = 1;

    private readonly AppSettings _config;
    private readonly WhisperAoVivo _ouvinte;
    private readonly ITradutorAoVivo? _tradutor;
    private readonly CancellationTokenSource _parar = new();
    private readonly SemaphoreSlim _temTrabalho = new(0, 1);
    private readonly object _trava = new();

    private readonly Queue<string> _aFechar = new();
    private readonly List<string> _fechadasTraduzidas = new();
    private readonly List<string> _fechadasOriginais = new();

    private string _emAndamento = "";
    private string _emAndamentoTraduzido = "";
    private string _ultimoTraduzidoDe = "";
    private string _provisorio = "";
    private Task? _worker;

    public ServicoDeLegenda(AppSettings config)
    {
        _config = config;
        _ouvinte = new WhisperAoVivo(config, config.LegendaFonte);
        _tradutor = config.LegendaTradutor == MotorTraducao.Nenhum ? null : Tradutores.Criar(config);

        _ouvinte.Legenda += AoMudarOTexto;
        _ouvinte.Reconheceu += AoFecharFrase;
    }

    /// <summary>A linha mudou e precisa ser redesenhada.</summary>
    public event Action<LinhaDeLegenda>? Atualizou;

    /// <summary>Frase fechada, para a transcrição da sessão.</summary>
    public event Action<TrechoFalado>? Reconheceu;

    public string Ouvinte => _ouvinte.Nome;
    public string Tradutor => _tradutor?.Nome ?? "sem tradução";
    public long UltimaPassadaMs => _ouvinte.UltimaPassadaMs;

    /// <summary>Está utilizável? Quando não, <see cref="Motivo"/> diz o que falta baixar ou configurar.</summary>
    public bool Disponivel => _ouvinte.Disponivel && (_tradutor?.Disponivel ?? true);

    public string? Motivo => _ouvinte.Motivo ?? _tradutor?.Motivo;

    // ==================================================================

    public void Iniciar()
    {
        if (!_ouvinte.Disponivel) return;
        _ouvinte.Iniciar();
        _worker ??= Task.Run(TraduzirEmFilaDeUm);
    }

    /// <summary>Repassa o áudio da gravação. Assinatura igual à do transcritor ao vivo, de propósito.</summary>
    public void Alimentar(string fonte, float[] amostras, int quantidade, int taxa, int canais, TimeSpan em) =>
        _ouvinte.Alimentar(fonte, amostras, quantidade, taxa, canais, em);

    public void Encerrar()
    {
        _ouvinte.Encerrar();
        _parar.Cancel();
        Acordar();
        try { _worker?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancelado */ }
    }

    public void Dispose()
    {
        Encerrar();
        _ouvinte.Dispose();
        _tradutor?.Dispose();
        _parar.Dispose();
        _temTrabalho.Dispose();
    }

    // ==================================================================

    private void AoFecharFrase(TrechoFalado trecho)
    {
        Reconheceu?.Invoke(trecho);
        lock (_trava)
        {
            _aFechar.Enqueue(trecho.Texto);
            // A frase saiu de "em andamento" e vai virar "fechada": zerar aqui evita ela aparecer
            // duas vezes na tela durante o intervalo entre uma coisa e outra.
            _emAndamento = "";
            _emAndamentoTraduzido = "";
            _ultimoTraduzidoDe = "";
        }
        Acordar();
    }

    private void AoMudarOTexto(string frase, string provisorio)
    {
        bool sóOProvisorio;
        lock (_trava)
        {
            _provisorio = provisorio;
            sóOProvisorio = frase == _emAndamento;
            _emAndamento = frase;
            if (sóOProvisorio) Publicar();
        }
        if (!sóOProvisorio) Acordar();
    }

    /// <summary>Semáforo de teto 1: avisar duas vezes não empilha. É isso que dá a "fila de um".</summary>
    private void Acordar()
    {
        try { _temTrabalho.Release(); } catch (SemaphoreFullException) { /* já avisado */ }
    }

    private async Task TraduzirEmFilaDeUm()
    {
        var ct = _parar.Token;
        while (true)
        {
            try { await _temTrabalho.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // Frases fechadas primeiro: cada uma é traduzida UMA vez e nunca mais.
            while (true)
            {
                string fechada;
                lock (_trava)
                {
                    if (_aFechar.Count == 0) break;
                    fechada = _aFechar.Dequeue();
                }
                var pt = await TraduzirAsync(fechada, ct).ConfigureAwait(false);
                lock (_trava)
                {
                    _fechadasTraduzidas.Add(pt);
                    _fechadasOriginais.Add(fechada);
                    while (_fechadasTraduzidas.Count > FrasesNaTela)
                    {
                        _fechadasTraduzidas.RemoveAt(0);
                        _fechadasOriginais.RemoveAt(0);
                    }
                    Publicar();
                }
            }

            // Depois a frase em andamento, que é a única coisa retraduzida enquanto cresce.
            string alvo;
            lock (_trava)
            {
                alvo = _emAndamento;
                if (alvo.Length == 0 || alvo == _ultimoTraduzidoDe) continue;
            }
            var traduzido = await TraduzirAsync(alvo, ct).ConfigureAwait(false);
            lock (_trava)
            {
                // Só vale se a frase não fechou nem trocou no meio da tradução.
                if (_emAndamento != alvo) continue;
                _ultimoTraduzidoDe = alvo;
                _emAndamentoTraduzido = traduzido;
                Publicar();
            }
        }
    }

    private async Task<string> TraduzirAsync(string texto, CancellationToken ct)
    {
        if (_tradutor == null) return texto;
        try
        {
            return await _tradutor.TraduzirAsync(texto, _config.LegendaIdiomaFala, _config.IdiomaDestino, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return texto; }
    }

    /// <summary>Chamado sempre com <see cref="_trava"/> na mão.</summary>
    private void Publicar()
    {
        var traduzido = string.Join(" ", _fechadasTraduzidas.Append(_emAndamentoTraduzido))
            .Trim();
        var original = _config.LegendaMostrarOriginal
            ? string.Join(" ", _fechadasOriginais.Append(_emAndamento)).Trim()
            : "";

        Atualizou?.Invoke(new LinhaDeLegenda(traduzido, original, _provisorio));
    }
}
