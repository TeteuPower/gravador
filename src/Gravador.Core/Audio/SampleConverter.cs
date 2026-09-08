using NAudio.Wave;

namespace Gravador.Core.Audio;

/// <summary>
/// Traduz o que o WASAPI entrega para o formato interno da gravação.
///
/// O endpoint manda no formato de mistura dele — na prática quase sempre float de 32 bits a
/// 48 kHz, mas um microfone de array (o "Grupo de microfones" do Intel Smart Sound) chega com
/// 4 canais, e placas antigas ainda aparecem a 44,1 kHz. Aqui isso vira sempre a mesma coisa:
/// float intercalado, na taxa e no número de canais que a configuração pediu.
///
/// A reamostragem é interpolação linear, e não um filtro polifásico. Para voz, a 48 kHz de destino,
/// a diferença é inaudível, e o custo de CPU é o que importa: esta ferramenta roda ao lado de uma
/// chamada de vídeo em máquina modesta. Quando origem e destino batem — o caso normal — nem esse
/// laço roda: os quadros passam direto.
/// </summary>
public sealed class SampleConverter
{
    private readonly WaveFormat _origem;
    private readonly int _canaisDestino;
    private readonly int _taxaDestino;
    private readonly bool _mesmaTaxa;

    private float[] _decodificado = new float[8192];
    private float[] _saida = new float[8192];

    // estado da reamostragem, que precisa atravessar a fronteira entre buffers
    private double _posicao;
    private float[] _ultimoQuadro;

    public SampleConverter(WaveFormat origem, int taxaDestino, int canaisDestino)
    {
        _origem = origem;
        _taxaDestino = taxaDestino;
        _canaisDestino = canaisDestino;
        _mesmaTaxa = origem.SampleRate == taxaDestino;
        _ultimoQuadro = new float[canaisDestino];
    }

    public int CanaisDestino => _canaisDestino;

    /// <summary>
    /// Converte um buffer do WASAPI. Devolve o vetor interno (não guarde a referência) e a
    /// quantidade de amostras válidas nele.
    /// </summary>
    public (float[] Buffer, int Amostras) Converter(byte[] bytes, int contagem)
    {
        var quadrosOrigem = Decodificar(bytes, contagem, out var canaisOrigem);
        if (quadrosOrigem == 0) return (_saida, 0);

        var quadrosMisturados = Misturar(quadrosOrigem, canaisOrigem);
        return _mesmaTaxa
            ? (_decodificado, quadrosMisturados * _canaisDestino)
            : (Reamostrar(quadrosMisturados), _ultimaQuantidade);
    }

    // ------------------------------------------------------------------

    /// <summary>Bytes do endpoint para float, em <see cref="_decodificado"/>. Devolve os quadros lidos.</summary>
    private int Decodificar(byte[] bytes, int contagem, out int canais)
    {
        canais = _origem.Channels;
        var bytesPorAmostra = _origem.BitsPerSample / 8;
        if (bytesPorAmostra == 0 || canais == 0) return 0;

        var amostras = contagem / bytesPorAmostra;
        var quadros = amostras / canais;
        amostras = quadros * canais;
        Garantir(ref _decodificado, amostras);

        var ehFloat = _origem.Encoding is WaveFormatEncoding.IeeeFloat
            || (_origem is WaveFormatExtensible ext && ext.Encoding == WaveFormatEncoding.Extensible && _origem.BitsPerSample == 32);

        switch (bytesPorAmostra)
        {
            case 4 when ehFloat:
                for (var i = 0; i < amostras; i++) _decodificado[i] = BitConverter.ToSingle(bytes, i * 4);
                break;
            case 4:
                for (var i = 0; i < amostras; i++) _decodificado[i] = BitConverter.ToInt32(bytes, i * 4) / 2147483648f;
                break;
            case 3:
                for (var i = 0; i < amostras; i++)
                {
                    var o = i * 3;
                    var v = bytes[o] | (bytes[o + 1] << 8) | (sbyte)bytes[o + 2] << 16;
                    _decodificado[i] = v / 8388608f;
                }
                break;
            case 2:
                for (var i = 0; i < amostras; i++) _decodificado[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                break;
            case 1:
                for (var i = 0; i < amostras; i++) _decodificado[i] = (bytes[i] - 128) / 128f;
                break;
            default:
                return 0;
        }
        return quadros;
    }

    /// <summary>
    /// Ajusta a contagem de canais no lugar, dentro de <see cref="_decodificado"/>.
    ///
    /// Vários canais viram um pela MÉDIA, não pela soma nem pelo primeiro canal. Média porque a
    /// soma satura e o primeiro canal joga fora metade da voz num microfone estéreo; num array de
    /// quatro cápsulas, a média ainda ajuda a cancelar ruído descorrelacionado.
    /// </summary>
    private int Misturar(int quadros, int canaisOrigem)
    {
        if (canaisOrigem == _canaisDestino) return quadros;

        if (_canaisDestino == 1)
        {
            for (var q = 0; q < quadros; q++)
            {
                var soma = 0f;
                for (var c = 0; c < canaisOrigem; c++) soma += _decodificado[q * canaisOrigem + c];
                _decodificado[q] = soma / canaisOrigem;
            }
            return quadros;
        }

        // destino estéreo: de mono duplica, de mais canais fica com a frente esquerda/direita
        if (canaisOrigem == 1)
        {
            Garantir(ref _decodificado, quadros * 2, preservar: quadros);
            for (var q = quadros - 1; q >= 0; q--)
            {
                var v = _decodificado[q];
                _decodificado[q * 2] = v;
                _decodificado[q * 2 + 1] = v;
            }
            return quadros;
        }

        for (var q = 0; q < quadros; q++)
        {
            _decodificado[q * 2] = _decodificado[q * canaisOrigem];
            _decodificado[q * 2 + 1] = _decodificado[q * canaisOrigem + 1];
        }
        return quadros;
    }

    private int _ultimaQuantidade;

    private float[] Reamostrar(int quadros)
    {
        var razao = (double)_origem.SampleRate / _taxaDestino;
        var canais = _canaisDestino;

        // +2 de folga: a posição fracionária sobra de um buffer para o outro
        var maximo = (int)(quadros / razao) + 2;
        Garantir(ref _saida, maximo * canais);

        // O último quadro do buffer fica retido: interpolar até ele exigiria o quadro seguinte, que
        // só chega no próximo buffer. Ele volta como índice -1 na chamada seguinte, vindo de
        // _ultimoQuadro — é o que faz a emenda entre buffers não ter degrau.
        var limite = quadros - 1;
        var escritos = 0;
        var pos = _posicao;
        while (true)
        {
            var indice = (int)Math.Floor(pos);
            if (indice >= limite) break;

            var fracao = (float)(pos - indice);
            for (var c = 0; c < canais; c++)
            {
                var a = indice < 0 ? _ultimoQuadro[c] : _decodificado[indice * canais + c];
                var b = _decodificado[(indice + 1) * canais + c];
                _saida[escritos * canais + c] = a + (b - a) * fracao;
            }
            escritos++;
            pos += razao;
        }

        if (quadros > 0)
            for (var c = 0; c < canais; c++)
                _ultimoQuadro[c] = _decodificado[limite * canais + c];
        _posicao = pos - limite;

        _ultimaQuantidade = escritos * canais;
        return _saida;
    }

    private static void Garantir(ref float[] vetor, int tamanho, int preservar = 0)
    {
        if (vetor.Length >= tamanho) return;
        var novo = new float[Math.Max(tamanho, vetor.Length * 2)];
        if (preservar > 0) Array.Copy(vetor, novo, Math.Min(preservar, vetor.Length));
        vetor = novo;
    }
}
