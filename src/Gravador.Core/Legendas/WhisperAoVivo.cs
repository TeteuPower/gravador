using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gravador.Core.Audio;
using Gravador.Core.Ferramentas;
using Gravador.Core.Session;
using Gravador.Core.Settings;
using Gravador.Core.Transcription;

namespace Gravador.Core.Legendas;

/// <summary>
/// Transcreve enquanto a pessoa fala, para virar legenda.
///
/// O desenho saiu de três medições nesta máquina, e nenhuma delas deu o que se esperava:
///
/// 1. **O encoder do whisper custa o mesmo para 5 s e para 30 s de áudio** — 840 ms com o `base` e
///    286 ms com o `tiny`, a 12 threads; 1403 e 548 ms a 4 threads. O orçamento não é "quantos
///    segundos de áudio", é "quantas passadas por segundo". Isso derruba a janela deslizante curta
///    (que paga o encoder inteiro para reler pouca coisa) e libera a janela que CRESCE até o texto
///    firmar.
/// 2. **Chamar o `whisper-cli` a cada passada é mais rápido do que manter o `whisper-server`
///    ligado** — tiny a 4 threads: 783 ms contra 1060 ms; base: 1803 contra 2150. O servidor tem
///    ~300 ms de sobrecarga por requisição, mais do que os 150 a 330 ms de carga de modelo que ele
///    economizaria. E ainda evita ter um processo órfão para cuidar.
/// 3. **`-ml 1` faz o whisper devolver um segmento por palavra**, com carimbo. É disso que o acordo
///    entre passadas precisa para saber onde cortar o áudio já resolvido.
///
/// O algoritmo é o LocalAgreement-2: a cada passada, só vira texto firme o prefixo em que ESTA
/// passada e a ANTERIOR concordam, palavra por palavra. O resto continua provisório. Sem isso a
/// legenda treme, porque o whisper muda de ideia justamente sobre as últimas palavras — são as que
/// ele leu sem contexto à direita.
/// </summary>
public sealed class WhisperAoVivo : ITranscritorAoVivo
{
    /// <summary>O whisper só aceita 16 kHz mono.</summary>
    public const int TaxaDoModelo = 16000;

    /// <summary>
    /// O encoder do whisper enxerga 30 s. Deixar a janela chegar lá faria o começo dela ser
    /// truncado sem aviso, então antes disso o texto é firmado à força mesmo sem acordo.
    /// </summary>
    private const double JanelaMaximaSegundos = 24;

    /// <summary>Abaixo disto não vale acordar o whisper: não cabe uma palavra inteira.</summary>
    private const double JanelaMinimaSegundos = 1.0;

    /// <summary>
    /// Quando a frase corrente é fechada à força, mesmo sem ponto final.
    ///
    /// Cinco segundos e noventa caracteres não são estética: é o tamanho de uma legenda. Quem fala
    /// corrido — e numa apresentação é sempre assim, com "you know" no lugar do ponto — não entrega
    /// pontuação nenhuma para o whisper, e sem um teto a "frase" cresce até virar parágrafo. Medido
    /// com o limite em 12 s, saíram trechos de 29 s e 370 caracteres: ilegível como legenda, e caro,
    /// porque é ele inteiro que é retraduzido a cada passada.
    /// </summary>
    private const double SegundosPorFrase = 5;

    private const int CaracteresPorFrase = 90;

    /// <summary>Menos que isto não é legenda, é fragmento — e fragmento o tradutor traduz mal.</summary>
    private const int PalavrasMinimasPorFrase = 3;

    private readonly AppSettings _config;
    private readonly string _fonteEscolhida;
    private readonly object _trava = new();

    private readonly List<float> _janela = new(TaxaDoModelo * 32);
    private double _janelaBase;

    /// <summary>Fase da decimação, carregada de um buffer para o outro.</summary>
    private double _fase;

    private Thread? _worker;
    private CancellationTokenSource? _parar;
    private string? _pastaTemporaria;

    /// <summary>O que a passada anterior disse do trecho ainda não firmado. É contra isto que a nova é comparada.</summary>
    private List<Palavra> _anterior = new();

    /// <summary>Firmes e ainda não fechadas numa frase — viram um <see cref="TrechoFalado"/> no fim.</summary>
    private readonly List<Palavra> _pendentes = new();

    /// <summary>Últimas palavras firmes, passadas ao whisper como contexto da passada seguinte.</summary>
    private string _contexto = "";

    public WhisperAoVivo(AppSettings config, string fonte = "sistema")
    {
        _config = config;
        _fonteEscolhida = fonte;

        if (!Ferramentas.Ferramentas.WhisperCli.Disponivel)
            Motivo = "O whisper.cpp ainda não foi baixado nesta máquina.";
        else if (!Ferramentas.Ferramentas.ModeloWhisper(config.LegendaModelo).Disponivel)
            Motivo = $"O modelo {config.LegendaModelo} ainda não foi baixado.";
    }

    public string Nome => $"whisper.cpp ao vivo ({_config.LegendaModelo})";
    public bool Disponivel => Motivo == null;
    public string? Motivo { get; private set; }

    public event Action<TrechoFalado>? Reconheceu;
    public event Action<string>? Parcial;

    /// <summary>
    /// A linha como ela deve aparecer, separada em duas partes: o que já está firme e o que ainda
    /// pode mudar. Quem desenha pinta a segunda mais apagada — é assim que legenda de TV funciona.
    /// </summary>
    public event Action<string, string>? Legenda;

    /// <summary>Quanto tempo a última passada levou. A tela de configurações mostra para calibrar o modelo.</summary>
    public long UltimaPassadaMs { get; private set; }

    // ==================================================================

    public void Iniciar()
    {
        if (!Disponivel) return;
        lock (_trava)
        {
            if (_worker != null) return;
            _pastaTemporaria = Path.Combine(Path.GetTempPath(), "GravadorLegenda",
                Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_pastaTemporaria);
            _parar = new CancellationTokenSource();

            // Prioridade abaixo do normal: a legenda não pode disputar CPU com a gravação, que é a
            // única coisa aqui que não dá para refazer depois.
            _worker = new Thread(Rodar)
            {
                IsBackground = true,
                Name = "legenda",
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }
    }

    public void Alimentar(string fonte, float[] amostras, int quantidade, int taxa, int canais, TimeSpan em)
    {
        if (!Disponivel || quantidade <= 0) return;
        if (!fonte.Equals(_fonteEscolhida, StringComparison.OrdinalIgnoreCase)) return;

        // Decimação com média dos canais, igual ao que o transcritor do Windows faz: um filtro
        // anti-serrilhamento decente custaria mais CPU do que o reconhecedor inteiro, e a fala mora
        // abaixo de 8 kHz de qualquer jeito.
        var passo = (double)taxa / TaxaDoModelo;
        if (passo < 1) passo = 1;
        var quadros = quantidade / canais;

        lock (_trava)
        {
            // A janela vazia é reancorada no relógio da gravação. Depois disso quem manda no tempo
            // são os carimbos das palavras, que é o que o corte usa.
            if (_janela.Count == 0) _janelaBase = em.TotalSeconds;

            double p;
            for (p = _fase; p < quadros; p += passo)
            {
                var q = (int)p;
                var soma = 0f;
                for (var c = 0; c < canais; c++) soma += amostras[q * canais + c];
                _janela.Add(soma / canais);
            }

            // A fase atravessa a fronteira entre buffers. Sem isso, cada buffer recomeçaria a
            // decimação do zero e a janela acumularia mais amostras do que o tempo que ela cobre —
            // o corte por carimbo passaria a mirar no lugar errado.
            _fase = p - quadros;
        }
    }

    public void Encerrar()
    {
        Thread? worker;
        lock (_trava)
        {
            _parar?.Cancel();
            worker = _worker;
            _worker = null;
        }
        worker?.Join(TimeSpan.FromSeconds(5));

        // O que sobrou provisório vira firme: melhor uma frase com uma palavra incerta no fim do
        // que a última frase da reunião sumir da transcrição.
        lock (_trava)
        {
            _pendentes.AddRange(_anterior);
            _anterior = new List<Palavra>();
        }
        FecharFrase(forcar: true);

        try { if (_pastaTemporaria != null) Directory.Delete(_pastaTemporaria, recursive: true); }
        catch { /* sobra no temp */ }
    }

    public void Dispose() => Encerrar();

    // ==================================================================

    private void Rodar()
    {
        var ct = _parar!.Token;
        var wav = Path.Combine(_pastaTemporaria!, "janela.wav");

        while (!ct.IsCancellationRequested)
        {
            float[] amostras;
            double baseDaJanela;
            lock (_trava)
            {
                if (_janela.Count < TaxaDoModelo * JanelaMinimaSegundos)
                {
                    Monitor.Wait(_trava, 200);
                    continue;
                }
                amostras = _janela.ToArray();
                baseDaJanela = _janelaBase;
            }

            var relogio = Stopwatch.StartNew();
            try
            {
                EscreverWav(wav, amostras);
                var palavras = Transcrever(wav, baseDaJanela, ct);
                Acordar(palavras, amostras.Length / (double)TaxaDoModelo);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // uma passada que falhou não derruba a legenda; a próxima relê o mesmo áudio
            }
            relogio.Stop();
            UltimaPassadaMs = relogio.ElapsedMilliseconds;

            // A próxima passada espera pelo menos o tempo que a anterior levou. Assim a legenda
            // nunca ocupa mais que metade de um núcleo, seja qual for a máquina: numa modesta ela
            // fica mais atrasada, numa rápida chega mais perto do tempo real. Um intervalo fixo ou
            // desperdiçaria a máquina boa ou afogaria a fraca.
            var espera = Math.Max(_config.LegendaIntervaloMs, relogio.ElapsedMilliseconds);
            if (ct.WaitHandle.WaitOne((int)espera)) break;
        }
    }

    private static void EscreverWav(string caminho, float[] amostras)
    {
        using var w = new WavWriter(caminho, TaxaDoModelo, 1);
        w.Escrever(amostras, amostras.Length);
    }

    private List<Palavra> Transcrever(string wav, double baseDaJanela, CancellationToken ct)
    {
        var exe = Ferramentas.Ferramentas.WhisperCli.Localizar();
        var modelo = Ferramentas.Ferramentas.ModeloWhisper(_config.LegendaModelo).Localizar();
        if (exe == null || modelo == null) return new List<Palavra>();

        var saida = Path.Combine(_pastaTemporaria!, "passada");
        var json = saida + ".json";
        try { File.Delete(json); } catch { /* não existia */ }

        var threads = _config.LegendaThreads > 0
            ? _config.LegendaThreads
            : Math.Max(2, Environment.ProcessorCount / 4);

        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-m"); info.ArgumentList.Add(modelo);
        info.ArgumentList.Add("-f"); info.ArgumentList.Add(wav);
        info.ArgumentList.Add("-l"); info.ArgumentList.Add(Transcritores.CodigoCurto(_config.LegendaIdiomaFala));
        info.ArgumentList.Add("-t"); info.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));

        // Ganancioso: beam search custa passadas extras do decodificador, e aqui atraso é pior do
        // que uma palavra trocada — que a passada seguinte conserta sozinha, porque relê o mesmo
        // áudio já com contexto à direita.
        //
        // Mas SEM `-nf`. Desligar o recuo por temperatura foi a primeira tentativa, e ela caiu no
        // laço de repetição clássico do whisper: medido, ele cuspiu "the case is that" trinta vezes
        // seguidas num trecho de áudio ambíguo. O recuo existe justamente para detectar isso pela
        // taxa de compressão e refazer o trecho — e um trecho refeito de vez em quando custa menos
        // do que cem fichas geradas à toa.
        info.ArgumentList.Add("-bs"); info.ArgumentList.Add("1");
        info.ArgumentList.Add("-bo"); info.ArgumentList.Add("1");

        // Um segmento por palavra: é daqui que sai o carimbo que diz onde cortar o áudio resolvido.
        info.ArgumentList.Add("-ml"); info.ArgumentList.Add("1");
        info.ArgumentList.Add("-oj"); info.ArgumentList.Add("-of"); info.ArgumentList.Add(saida);
        info.ArgumentList.Add("-np");

        // Contexto do que já foi dito: segura nome próprio e jargão entre uma passada e outra.
        if (_contexto.Length > 0)
        {
            info.ArgumentList.Add("--prompt");
            info.ArgumentList.Add(_contexto);
        }

        using var processo = Process.Start(info)
            ?? throw new InvalidOperationException("O whisper não subiu.");

        // Drenar as duas saídas antes de esperar: o whisper escreve bastante em stderr, e um cano
        // cheio trava o processo que escreve nele.
        var erro = processo.StandardError.ReadToEndAsync(ct);
        processo.StandardOutput.ReadToEnd();
        erro.GetAwaiter().GetResult();
        processo.WaitForExit();
        ct.ThrowIfCancellationRequested();

        return File.Exists(json) ? Ler(json, baseDaJanela) : new List<Palavra>();
    }

    private static List<Palavra> Ler(string json, double baseDaJanela)
    {
        var palavras = new List<Palavra>();
        var dentroDeMarcacao = false;
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        if (!doc.RootElement.TryGetProperty("transcription", out var segmentos)) return palavras;

        foreach (var s in segmentos.EnumerateArray())
        {
            var texto = s.TryGetProperty("text", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(texto)) continue;
            texto = texto.Trim();

            // Ruído que o whisper anota em vez de transcrever: "(applause)", "[MUSIC]",
            // "[BLANK_AUDIO]". Com `-ml 1` cada anotação dessas chega PICADA em várias "palavras"
            // ("[", "BLANK", "_", "AUDIO", "]"), então testar o começo do segmento não basta — foi
            // assim que um "_ AUDIO]" apareceu no meio de uma legenda. Aqui a marcação é detectada
            // peça a peça e o trecho inteiro é descartado.
            if (Marcacao(texto)) { dentroDeMarcacao = true; continue; }
            if (dentroDeMarcacao)
            {
                if (texto.Contains(']') || texto.Contains(')')) dentroDeMarcacao = false;
                continue;
            }

            double de = 0, ate = 0;
            if (s.TryGetProperty("offsets", out var o))
            {
                de = o.TryGetProperty("from", out var f) ? f.GetDouble() / 1000.0 : 0;
                ate = o.TryGetProperty("to", out var a) ? a.GetDouble() / 1000.0 : de;
            }
            palavras.Add(new Palavra(baseDaJanela + de, baseDaJanela + ate, texto));
        }
        return palavras;
    }

    // ==================================================================

    /// <summary>
    /// O acordo entre duas passadas: firma o prefixo comum, corta o áudio já resolvido e devolve o
    /// resto para a tela como provisório.
    /// </summary>
    private void Acordar(List<Palavra> atual, double duracaoDaJanela)
    {
        var comum = 0;
        while (comum < atual.Count && comum < _anterior.Count
               && atual[comum].Chave.Length > 0
               && atual[comum].Chave == _anterior[comum].Chave)
            comum++;

        // A última palavra do prefixo comum ainda pode crescer — o whisper corta "infrastructure"
        // em "infra" quando o áudio termina no meio dela, e as duas passadas concordam nisso. Só
        // firma quem tem outra palavra depois confirmando onde ela acaba.
        var firmes = Math.Max(0, comum - 1);

        // Janela quase no teto de 30 s do encoder: aqui não dá para esperar acordo, ou o começo
        // seria truncado sem aviso. Firma tudo e recomeça.
        if (duracaoDaJanela >= JanelaMaximaSegundos) firmes = atual.Count;

        if (firmes > 0)
        {
            var novas = atual.GetRange(0, firmes);
            lock (_trava)
            {
                _pendentes.AddRange(novas);
                _contexto = Juntar(_pendentes.Count > 16 ? _pendentes.GetRange(_pendentes.Count - 16, 16) : _pendentes);
            }
            Cortar(novas[^1].Ate);
        }

        _anterior = firmes > 0 ? atual.GetRange(firmes, atual.Count - firmes) : atual;

        FecharFrase(forcar: false);
        Emitir();
    }

    /// <summary>Descarta o áudio anterior a <paramref name="ate"/>: ele já virou texto firme.</summary>
    private void Cortar(double ate)
    {
        lock (_trava)
        {
            var descartar = (int)Math.Round((ate - _janelaBase) * TaxaDoModelo);
            descartar = Math.Clamp(descartar, 0, _janela.Count);
            if (descartar <= 0) return;
            _janela.RemoveRange(0, descartar);
            _janelaBase = ate;
        }
    }

    /// <summary>
    /// Fecha a frase corrente num <see cref="TrechoFalado"/>, que é o que vai para a transcrição da
    /// sessão. Quem cuida da tela depois disso é o <see cref="ServicoDeLegenda"/>, que guarda a
    /// frase já traduzida em vez de mandar traduzir tudo de novo.
    /// </summary>
    private void FecharFrase(bool forcar)
    {
        // Em laço, e não uma vez só. Quando a janela bate no teto de 30 s do encoder, uma passada
        // firma tudo de uma vez — medido, meio minuto de fala chegando junto. Fechando uma frase por
        // chamada, aquele bloco viraria UMA legenda de 470 caracteres. Aqui ele vira cinco.
        while (true)
        {
            List<Palavra> frase;
            lock (_trava)
            {
                var corte = OndeCortar(_pendentes, forcar);
                if (corte <= 0) return;
                frase = _pendentes.GetRange(0, corte);
                _pendentes.RemoveRange(0, corte);
            }

            var texto = Juntar(frase);
            if (texto.Length == 0) continue;
            Reconheceu?.Invoke(new TrechoFalado(frase[0].De, frase[^1].Ate, _fonteEscolhida, texto, 1));
        }
    }

    /// <summary>
    /// Quantas palavras cabem na próxima legenda: até o primeiro ponto final, ou até estourar o
    /// tempo ou os caracteres — o que vier antes. Zero significa "ainda não fecha".
    /// </summary>
    private static int OndeCortar(List<Palavra> pendentes, bool forcar)
    {
        if (pendentes.Count == 0) return 0;

        var caracteres = 0;
        for (var i = 0; i < pendentes.Count; i++)
        {
            var t = pendentes[i].Texto;
            caracteres += t.Length + 1;

            // Nunca fechar em uma ou duas palavras. Um ponto final solto — e o whisper põe muitos,
            // no meio de fala corrida — geraria uma legenda de uma palavra, que o tradutor recebe
            // sem contexto nenhum: foi assim que um "you know" isolado virou "- Não." na tela.
            if (i + 1 < PalavrasMinimasPorFrase) continue;

            var terminou = t.EndsWith('.') || t.EndsWith('?') || t.EndsWith('!');
            if (terminou
                || caracteres >= CaracteresPorFrase
                || pendentes[i].Ate - pendentes[0].De >= SegundosPorFrase)
                return i + 1;
        }
        return forcar ? pendentes.Count : 0;
    }

    private void Emitir()
    {
        string firme;
        lock (_trava) firme = Juntar(_pendentes);
        var provisorio = Juntar(_anterior);

        Legenda?.Invoke(firme, provisorio);
        Parcial?.Invoke((firme + " " + provisorio).Trim());
    }

    /// <summary>Começo de uma anotação do whisper — "[BLANK_AUDIO]", "(applause)", "[MUSIC]".</summary>
    private static bool Marcacao(string texto) =>
        texto.StartsWith('[') || texto.StartsWith('(')
        || texto.Equals("BLANK", StringComparison.OrdinalIgnoreCase)
        || texto.Equals("MUSIC", StringComparison.OrdinalIgnoreCase);

    /// <summary>Junta as palavras num texto, sem espaço antes de pontuação.</summary>
    private static string Juntar(IReadOnlyList<Palavra> palavras)
    {
        var sb = new StringBuilder();
        foreach (var p in palavras)
        {
            var t = p.Texto;
            if (t.Length == 0) continue;
            if (sb.Length > 0 && !(t.Length > 0 && char.IsPunctuation(t[0]) && t[0] != '¿' && t[0] != '('))
                sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString().Trim();
    }
}
