namespace Gravador.Core.Audio;

/// <summary>
/// Junta as trilhas num arquivo só, somando por POSIÇÃO ABSOLUTA e não por ordem de chegada.
///
/// É a peça que faz o arquivo misturado bater com os separados. Sistema e microfone são dois
/// dispositivos com dois relógios: os buffers chegam intercalados, em rajadas, e um deles pode
/// simplesmente não chegar (o loopback de um alto-falante em silêncio não produz nada). Somar na
/// ordem em que chegam desalinharia as trilhas em segundos ao longo de uma reunião. Aqui cada
/// buffer diz em que quadro ele começa — o mesmo número que a trilha separada usou para escrever —
/// e cai exatamente ali dentro do acumulador.
///
/// O acumulador cobre alguns segundos e é drenado por trás, com atraso: o que já passou do prazo
/// vai para o disco, e um buffer que chegue atrasado demais é descartado em vez de sair fora do
/// lugar.
/// </summary>
public sealed class Mixer : IDisposable
{
    private readonly WavWriter _wav;
    private readonly float[] _acumulador;
    private readonly int _canais;
    private readonly int _quadrosNoBuffer;
    private readonly object _trava = new();
    private long _inicioAbsoluto;

    /// <summary>Acima disto o pico entra num joelho macio em vez de bater no teto e distorcer.</summary>
    private const float Joelho = 0.8f;

    public Mixer(string caminho, int taxa, int canais, double segundosDeFolga = 3.0)
    {
        _canais = canais;
        _quadrosNoBuffer = (int)(taxa * segundosDeFolga);
        _acumulador = new float[_quadrosNoBuffer * canais];
        _wav = new WavWriter(caminho, taxa, canais);
    }

    public string Caminho => _wav.Caminho;
    public TimeSpan Duracao => _wav.Duracao;

    /// <summary>Soma um buffer que começa no quadro absoluto <paramref name="posicao"/>.</summary>
    public void Adicionar(long posicao, float[] amostras, int quantidade)
    {
        if (quantidade <= 0) return;
        var quadros = quantidade / _canais;
        if (quadros <= 0) return;

        lock (_trava)
        {
            // Não cabe: força a drenagem do que for preciso para abrir espaço. Sem isto, uma trilha
            // que se adiante muito (rajada grande do WASAPI) perderia áudio em silêncio.
            var excedente = posicao + quadros - (_inicioAbsoluto + _quadrosNoBuffer);
            if (excedente > 0) DrenarInterno(_inicioAbsoluto + excedente);

            var deslocamento = posicao - _inicioAbsoluto;
            var primeiroQuadro = 0;
            if (deslocamento < 0)
            {
                // chegou depois de a região já ter ido para o disco: aproveita o que ainda dá
                primeiroQuadro = (int)Math.Min(-deslocamento, quadros);
                deslocamento = 0;
            }

            for (var q = primeiroQuadro; q < quadros; q++)
            {
                var destino = (int)(deslocamento + (q - primeiroQuadro)) * _canais;
                if (destino + _canais > _acumulador.Length) break;
                for (var c = 0; c < _canais; c++)
                    _acumulador[destino + c] += amostras[q * _canais + c];
            }
        }
    }

    /// <summary>Manda para o disco tudo o que for anterior a <paramref name="ateQuadro"/>.</summary>
    public void Drenar(long ateQuadro)
    {
        lock (_trava) DrenarInterno(ateQuadro);
    }

    private void DrenarInterno(long ateQuadro)
    {
        var quadros = (int)Math.Min(ateQuadro - _inicioAbsoluto, _quadrosNoBuffer);
        if (quadros <= 0) return;

        var amostras = quadros * _canais;
        for (var i = 0; i < amostras; i++)
        {
            var v = _acumulador[i];
            var abs = v < 0 ? -v : v;
            if (abs > Joelho)
            {
                var excesso = MathF.Tanh((abs - Joelho) / (1f - Joelho)) * (1f - Joelho);
                _acumulador[i] = v < 0 ? -(Joelho + excesso) : Joelho + excesso;
            }
        }

        _wav.Escrever(_acumulador, amostras);

        var restante = (_quadrosNoBuffer - quadros) * _canais;
        if (restante > 0) Array.Copy(_acumulador, amostras, _acumulador, 0, restante);
        Array.Clear(_acumulador, restante, amostras);
        _inicioAbsoluto += quadros;
    }

    /// <summary>
    /// Drena até o instante final e fecha o arquivo.
    ///
    /// O laço existe porque uma drenagem só cobre, no máximo, o tamanho do acumulador: parar uma
    /// gravação com vários segundos ainda represados precisa de mais de uma volta. E o limite é
    /// <paramref name="ateQuadro"/>, nunca o fim do acumulador — despejar o resto dele acrescentaria
    /// ao arquivo misturado os segundos de zeros que ainda não tinham sido usados, e ele terminaria
    /// mais longo que as trilhas separadas.
    /// </summary>
    public void Finalizar(long ateQuadro)
    {
        lock (_trava)
        {
            while (_inicioAbsoluto < ateQuadro) DrenarInterno(ateQuadro);
        }
        _wav.Dispose();
    }

    public void Dispose() => _wav.Dispose();
}
