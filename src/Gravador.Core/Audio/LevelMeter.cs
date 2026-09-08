namespace Gravador.Core.Audio;

/// <summary>
/// Medidor de nível de uma trilha: pico, RMS e a resposta para "tem alguém falando agora?".
///
/// O pico serve à barrinha da interface; o RMS decide a fala. Pico não decide fala porque um clique
/// de teclado passa de -20 dB e não é voz — o RMS de uma janela curta ignora o estalo e responde ao
/// que tem energia sustentada.
/// </summary>
public sealed class LevelMeter
{
    private readonly double _limiarDb;
    private float _picoSuavizado;
    private int _quadrosAcimaDoLimiar;
    private int _quadrosAbaixoDoLimiar;
    private readonly int _taxa;

    public LevelMeter(int taxa, double limiarDb)
    {
        _taxa = Math.Max(1, taxa);
        _limiarDb = limiarDb;
    }

    /// <summary>Pico do último buffer, de 0 a 1, com queda suave para a barra não piscar.</summary>
    public float Pico => _picoSuavizado;

    public double PicoDb => ParaDb(_picoSuavizado);

    /// <summary>RMS do último buffer, em dB.</summary>
    public double RmsDb { get; private set; } = -100;

    /// <summary>Passou do limiar por tempo suficiente para contar como fala.</summary>
    public bool Falando { get; private set; }

    public void Medir(float[] amostras, int quantidade, int canais)
    {
        if (quantidade <= 0) return;

        var pico = 0f;
        double soma = 0;
        for (var i = 0; i < quantidade; i++)
        {
            var v = amostras[i];
            var abs = v < 0 ? -v : v;
            if (abs > pico) pico = abs;
            soma += (double)v * v;
        }

        // queda de ~1/3 por buffer: sobe na hora, desce devagar, que é como um VU se lê
        _picoSuavizado = pico > _picoSuavizado ? pico : _picoSuavizado * 0.66f + pico * 0.34f;
        RmsDb = ParaDb(Math.Sqrt(soma / quantidade));

        AtualizarFala(quantidade / Math.Max(1, canais));
    }

    /// <summary>
    /// Histerese: 120 ms acima do limiar para abrir, 700 ms abaixo para fechar.
    ///
    /// Os dois tempos são diferentes de propósito. Abrir rápido evita cortar a primeira sílaba;
    /// fechar devagar evita picotar a frase nas pausas entre palavras, que é o que transforma uma
    /// linha do tempo de fala em confete inútil.
    /// </summary>
    private void AtualizarFala(int quadros)
    {
        var acima = RmsDb > _limiarDb;
        if (acima)
        {
            _quadrosAbaixoDoLimiar = 0;
            _quadrosAcimaDoLimiar += quadros;
            if (!Falando && _quadrosAcimaDoLimiar > _taxa * 0.12) Falando = true;
        }
        else
        {
            _quadrosAcimaDoLimiar = 0;
            _quadrosAbaixoDoLimiar += quadros;
            if (Falando && _quadrosAbaixoDoLimiar > _taxa * 0.70) Falando = false;
        }
    }

    /// <summary>Sem sinal por um tempo: derruba a barra e a fala (usado quando a trilha está pausada).</summary>
    public void Zerar()
    {
        _picoSuavizado = 0;
        RmsDb = -100;
        Falando = false;
        _quadrosAcimaDoLimiar = 0;
        _quadrosAbaixoDoLimiar = 0;
    }

    private static double ParaDb(double amplitude) =>
        amplitude <= 1e-7 ? -100 : Math.Max(-100, 20 * Math.Log10(amplitude));
}
