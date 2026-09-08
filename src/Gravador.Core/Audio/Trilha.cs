using NAudio.Wave;

namespace Gravador.Core.Audio;

/// <summary>
/// Uma fonte de áudio sendo gravada: o loopback do sistema ou o microfone.
///
/// O trabalho não óbvio desta classe é ANCORAR o áudio no relógio de parede. Os dois dispositivos
/// têm cristais independentes e nenhum deles anda exatamente na taxa que declara; ao longo de uma
/// reunião de duas horas, escrever simplesmente "o que chegou, na ordem em que chegou" afasta as
/// trilhas em segundos, e aí a captura de tela dos 42 minutos não corresponde ao que se ouve aos
/// 42 minutos. Pior: quando nada está tocando, o loopback do WASAPI não entrega buffer NENHUM — não
/// é silêncio, é ausência —, então a trilha do sistema simplesmente pularia todo trecho de silêncio
/// e encurtaria.
///
/// A correção é fazer a posição de escrita ser função do tempo decorrido, e não da contagem de
/// buffers: cada buffer sabe em que quadro deveria começar; falta vira silêncio, sobra vira
/// descarte. É o mesmo número de quadro que vai para o mixer, e é isso que mantém tudo alinhado.
/// </summary>
internal sealed class Trilha : IDisposable
{
    /// <summary>
    /// Folga antes de corrigir a posição: 100 ms.
    ///
    /// O WASAPI entrega em rajadas e o instante em que o retorno de chamada roda tem jitter de
    /// dezenas de milissegundos. Corrigir a cada buffer transformaria esse jitter em picotes
    /// audíveis. Cem milissegundos ficam acima do jitter e muito abaixo do desvio que incomoda —
    /// e a correção, quando acontece, é um trecho de silêncio inaudível numa fala.
    /// </summary>
    private const double ToleranciaSegundos = 0.100;

    private readonly IWaveIn _captura;
    private readonly SampleConverter _conversor;
    private readonly WavWriter? _wav;
    private readonly LevelMeter _medidor;
    private readonly int _canais;
    private readonly int _taxa;
    private readonly Func<TimeSpan?> _relogio;
    private readonly object _trava = new();

    private long _quadrosEscritos;
    private bool _iniciada;

    public Trilha(string nome, IWaveIn captura, string? caminhoWav, int taxa, int canais,
        double limiarDb, Func<TimeSpan?> relogio)
    {
        Nome = nome;
        _captura = captura;
        _taxa = taxa;
        _canais = canais;
        _relogio = relogio;
        _conversor = new SampleConverter(captura.WaveFormat, taxa, canais);
        _medidor = new LevelMeter(taxa, limiarDb);
        if (caminhoWav != null) _wav = new WavWriter(caminhoWav, taxa, canais);

        _captura.DataAvailable += AoChegarDados;
        _captura.RecordingStopped += (_, e) => { if (e.Exception != null) Falhou?.Invoke(Nome, e.Exception); };
    }

    public string Nome { get; }
    public string? CaminhoWav => _wav?.Caminho;
    public float Pico => _medidor.Pico;
    public bool Falando => _medidor.Falando;
    public long Quadros { get { lock (_trava) return _quadrosEscritos; } }
    public string FormatoDaOrigem => _captura.WaveFormat.ToString();

    /// <summary>Ganho aplicado antes de gravar. 1 = como veio.</summary>
    public float Ganho { get; set; } = 1f;

    /// <summary>Recebe (quadro inicial, buffer, amostras) para o arquivo misturado.</summary>
    public Action<long, float[], int>? ParaMixagem { get; set; }

    /// <summary>Verdadeiro quando o que vier agora deve virar silêncio no arquivo desta trilha.</summary>
    public Func<bool>? SilenciarNaTrilha { get; set; }

    /// <summary>Verdadeiro quando o que vier agora não deve entrar no arquivo misturado.</summary>
    public Func<bool>? SilenciarNaMixagem { get; set; }

    public event Action<string, Exception>? Falhou;

    public void Iniciar()
    {
        if (_iniciada) return;
        _iniciada = true;
        _captura.StartRecording();
    }

    public void Parar()
    {
        if (!_iniciada) return;
        _iniciada = false;
        try { _captura.StopRecording(); } catch { /* já parou sozinho */ }
    }

    /// <summary>Completa a trilha com silêncio até o instante final, para todos os arquivos terem a mesma duração.</summary>
    public void Nivelar(TimeSpan duracao)
    {
        lock (_trava)
        {
            var alvo = (long)(duracao.TotalSeconds * _taxa);
            if (alvo > _quadrosEscritos)
            {
                _wav?.EscreverSilencio(alvo - _quadrosEscritos);
                _quadrosEscritos = alvo;
            }
        }
    }

    private void AoChegarDados(object? _, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        // Pausado: o áudio deste intervalo não existe para a gravação. Descartar aqui, e não zerar
        // depois, é o que faz a pausa não deixar buraco de tempo no arquivo.
        if (_relogio() is not { } decorrido) return;

        try
        {
            var (buffer, amostras) = _conversor.Converter(e.Buffer, e.BytesRecorded);
            if (amostras <= 0) return;

            var quadros = amostras / _canais;
            if (Ganho != 1f)
                for (var i = 0; i < amostras; i++) buffer[i] *= Ganho;

            _medidor.Medir(buffer, amostras, _canais);

            long posicao;
            var descartar = 0;
            lock (_trava)
            {
                // O buffer terminou AGORA; ele começou "quadros" antes disso.
                var fimAlvo = (long)(decorrido.TotalSeconds * _taxa);
                var inicioAlvo = fimAlvo - quadros;
                var folga = (long)(ToleranciaSegundos * _taxa);
                var atraso = inicioAlvo - _quadrosEscritos;

                if (atraso > folga)
                {
                    // Ficou tempo sem chegar nada (loopback em silêncio, ou o dispositivo engasgou):
                    // o buraco vira silêncio para o arquivo continuar batendo com o relógio.
                    _wav?.EscreverSilencio(atraso);
                    _quadrosEscritos += atraso;
                }
                else if (atraso < -folga)
                {
                    // Chegou mais áudio do que coube no tempo (relógio do dispositivo adiantado):
                    // corta do começo, que é o pedaço que já foi coberto.
                    descartar = (int)Math.Min(quadros, -atraso);
                }

                posicao = _quadrosEscritos;
                var uteis = quadros - descartar;
                if (uteis > 0) _quadrosEscritos += uteis;
            }

            var restantes = (quadros - descartar) * _canais;
            if (restantes <= 0) return;
            var deslocamento = descartar * _canais;

            if (SilenciarNaTrilha?.Invoke() == true)
                _wav?.EscreverSilencio(restantes / _canais);
            else
                EscreverComDeslocamento(buffer, deslocamento, restantes);

            if (ParaMixagem is { } mix && SilenciarNaMixagem?.Invoke() != true)
            {
                if (deslocamento == 0) mix(posicao, buffer, restantes);
                else
                {
                    var recorte = new float[restantes];
                    Array.Copy(buffer, deslocamento, recorte, 0, restantes);
                    mix(posicao, recorte, restantes);
                }
            }
        }
        catch (Exception ex)
        {
            Falhou?.Invoke(Nome, ex);
        }
    }

    private void EscreverComDeslocamento(float[] buffer, int deslocamento, int quantidade)
    {
        if (_wav == null) return;
        if (deslocamento == 0) { _wav.Escrever(buffer, quantidade); return; }
        var recorte = new float[quantidade];
        Array.Copy(buffer, deslocamento, recorte, 0, quantidade);
        _wav.Escrever(recorte, quantidade);
    }

    public void ZerarMedidor() => _medidor.Zerar();

    public void Dispose()
    {
        Parar();
        try { _captura.Dispose(); } catch { /* já foi */ }
        _wav?.Dispose();
    }
}
