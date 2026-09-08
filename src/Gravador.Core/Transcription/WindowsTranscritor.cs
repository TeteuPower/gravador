using System.Globalization;
using System.Speech.Recognition;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Core.Transcription;

/// <summary>
/// Legenda ao vivo com o reconhecedor de fala que já vem no Windows.
///
/// É o motor barato: roda offline, não custa nada e usa uns poucos por cento de um núcleo. Também é
/// o menos preciso — foi feito para comando de voz, não para ditado corrido de reunião com várias
/// pessoas —, e serve para acompanhar e localizar trechos, não para virar ata.
///
/// Ele só existe se o pacote de fala do idioma estiver instalado nesta máquina, o que NÃO é o padrão
/// no Windows em português: normalmente só o en-US vem junto. Por isso <see cref="Disponivel"/> é
/// checado antes e a interface mostra o motivo em vez de simplesmente não transcrever nada.
///
/// O áudio chega da gravação já convertido; aqui ele só é reduzido a 16 kHz mono, que é a taxa em
/// que estes reconhecedores foram treinados. Alimentá-los a 48 kHz piora o resultado e gasta mais.
/// </summary>
public sealed class WindowsTranscritor : ITranscritorAoVivo
{
    private const int TaxaDoReconhecedor = 16000;

    private readonly AppSettings _config;
    private readonly object _trava = new();
    private SpeechRecognitionEngine? _motor;
    private FilaDeAudio? _fila;
    private string _fonte = "microfone";
    private double _acumulado;

    public WindowsTranscritor(AppSettings config)
    {
        _config = config;
        var idioma = EscolherIdioma(config.IdiomaTranscricao, out var motivo);
        Idioma = idioma;
        Motivo = motivo;
    }

    public string Nome => "Reconhecimento de fala do Windows";
    public RecognizerInfo? Idioma { get; }
    public bool Disponivel => Idioma != null;
    public string? Motivo { get; private set; }

    public event Action<TrechoFalado>? Reconheceu;
    public event Action<string>? Parcial;

    /// <summary>
    /// Procura um reconhecedor para o idioma pedido, aceitando o mesmo idioma em outra região
    /// (pt-PT serve para quem pediu pt-BR: o resultado piora um pouco, e é muito melhor do que nada).
    /// </summary>
    private static RecognizerInfo? EscolherIdioma(string pedido, out string? motivo)
    {
        motivo = null;
        try
        {
            var instalados = SpeechRecognitionEngine.InstalledRecognizers();
            if (instalados.Count == 0)
            {
                motivo = "Este Windows não tem nenhum reconhecedor de fala instalado.";
                return null;
            }

            var exato = instalados.FirstOrDefault(r =>
                string.Equals(r.Culture.Name, pedido, StringComparison.OrdinalIgnoreCase));
            if (exato != null) return exato;

            var raiz = pedido.Split('-')[0];
            var parecido = instalados.FirstOrDefault(r =>
                r.Culture.TwoLetterISOLanguageName.Equals(raiz, StringComparison.OrdinalIgnoreCase));
            if (parecido != null) return parecido;

            var disponiveis = string.Join(", ", instalados.Select(r => r.Culture.Name).Distinct());
            motivo = $"Não há reconhecedor de fala em {pedido} neste Windows (instalados: {disponiveis}). "
                   + "Instale o pacote de voz do idioma em Configurações › Hora e idioma, ou use a "
                   + "transcrição remota.";
            return null;
        }
        catch (Exception ex)
        {
            motivo = "O reconhecimento de fala do Windows não pôde ser consultado: " + ex.Message;
            return null;
        }
    }

    public void Iniciar()
    {
        if (!Disponivel || _motor != null) return;
        try
        {
            var motor = new SpeechRecognitionEngine(Idioma!);
            motor.LoadGrammar(new DictationGrammar());
            motor.SpeechRecognized += AoReconhecer;
            motor.SpeechHypothesized += (_, e) => Parcial?.Invoke(e.Result.Text);

            // Silêncio longo no meio de uma reunião é a regra, não a exceção; sem afrouxar estes
            // tempos o motor encerra a "frase" a cada pausa e devolve fragmentos de duas palavras.
            motor.EndSilenceTimeout = TimeSpan.FromMilliseconds(900);
            motor.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(1200);
            motor.BabbleTimeout = TimeSpan.FromSeconds(30);
            motor.InitialSilenceTimeout = TimeSpan.Zero;

            _fila = new FilaDeAudio();
            motor.SetInputToAudioStream(_fila,
                new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                    TaxaDoReconhecedor, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                    System.Speech.AudioFormat.AudioChannel.Mono));
            motor.RecognizeAsync(RecognizeMode.Multiple);
            _motor = motor;
        }
        catch (Exception ex)
        {
            Motivo = "O reconhecedor não iniciou: " + ex.Message;
            Encerrar();
        }
    }

    private void AoReconhecer(object? _, SpeechRecognizedEventArgs e)
    {
        var texto = e.Result?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(texto)) return;

        var inicio = e.Result!.Audio?.AudioPosition ?? TimeSpan.Zero;
        var duracao = e.Result.Audio?.Duration ?? TimeSpan.Zero;
        Reconheceu?.Invoke(new TrechoFalado(
            inicio.TotalSeconds,
            (inicio + duracao).TotalSeconds,
            _fonte,
            texto,
            e.Result.Confidence));
    }

    public void Alimentar(string fonte, float[] amostras, int quantidade, int taxa, int canais, TimeSpan em)
    {
        var fila = _fila;
        if (fila == null || quantidade <= 0) return;
        _fonte = fonte;

        // 48 kHz estéreo para 16 kHz mono, por decimação simples com média dos canais. É grosseiro
        // de propósito: um filtro anti-serrilhamento decente custaria mais CPU do que o reconhecedor
        // inteiro, e a fala fica abaixo de 8 kHz de qualquer jeito.
        var passo = (double)taxa / TaxaDoReconhecedor;
        if (passo < 1) passo = 1;

        var quadros = quantidade / canais;
        var saida = new byte[(int)(quadros / passo + 2) * 2];
        var escritos = 0;

        for (double p = _acumulado; p < quadros; p += passo)
        {
            var q = (int)p;
            var soma = 0f;
            for (var c = 0; c < canais; c++) soma += amostras[q * canais + c];
            var v = soma / canais;
            var s = v >= 1f ? short.MaxValue : v <= -1f ? short.MinValue : (short)(v * 32767f);
            if (escritos + 2 > saida.Length) break;
            saida[escritos++] = (byte)(s & 0xFF);
            saida[escritos++] = (byte)((s >> 8) & 0xFF);
        }
        _acumulado = 0;

        fila.Empurrar(saida, escritos);
    }

    public void Encerrar()
    {
        lock (_trava)
        {
            try { _motor?.RecognizeAsyncCancel(); } catch { /* já parou */ }
            _fila?.Encerrar();
            try { _motor?.Dispose(); } catch { /* já foi */ }
            _motor = null;
            _fila = null;
        }
    }

    public void Dispose() => Encerrar();

    // ------------------------------------------------------------------

    /// <summary>
    /// A ponte entre a gravação e o reconhecedor.
    ///
    /// O <c>SpeechRecognitionEngine</c> lê de um <see cref="Stream"/> na thread dele e espera que a
    /// leitura BLOQUEIE quando não há áudio — devolver zero bytes ele interpreta como fim do
    /// arquivo e encerra o reconhecimento. Como o áudio chega em rajadas de outra thread, este
    /// stream é uma fila com bloqueio na leitura.
    /// </summary>
    private sealed class FilaDeAudio : Stream
    {
        private readonly Queue<byte[]> _blocos = new();
        private readonly object _trava = new();
        private byte[]? _atual;
        private int _posicao;
        private bool _encerrado;

        /// <summary>Um minuto de áudio a 16 kHz. Passou disso, o reconhecedor não vai alcançar mesmo.</summary>
        private const int LimiteDeBlocos = 3000;

        public void Empurrar(byte[] dados, int quantidade)
        {
            if (quantidade <= 0) return;
            var copia = new byte[quantidade];
            Array.Copy(dados, copia, quantidade);
            lock (_trava)
            {
                if (_encerrado) return;
                if (_blocos.Count >= LimiteDeBlocos) _blocos.Dequeue();
                _blocos.Enqueue(copia);
                Monitor.Pulse(_trava);
            }
        }

        public void Encerrar()
        {
            lock (_trava)
            {
                _encerrado = true;
                Monitor.PulseAll(_trava);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_trava)
            {
                while (_atual == null || _posicao >= _atual.Length)
                {
                    if (_blocos.Count > 0)
                    {
                        _atual = _blocos.Dequeue();
                        _posicao = 0;
                        continue;
                    }
                    if (_encerrado) return 0;
                    Monitor.Wait(_trava, 250);
                    if (_blocos.Count == 0 && _encerrado) return 0;
                }

                var n = Math.Min(count, _atual.Length - _posicao);
                Array.Copy(_atual, _posicao, buffer, offset, n);
                _posicao += n;
                return n;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
