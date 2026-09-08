using System.Buffers;
using System.Text;

namespace Gravador.Core.Audio;

/// <summary>
/// Escreve WAV PCM 16 bits direto no disco, sem passar pela memória.
///
/// Escrito à mão, e não com o <c>WaveFileWriter</c> do NAudio, por causa de uma coisa só: o cabeçalho
/// é regravado a cada poucos segundos com o tamanho de até agora. Uma reunião de duas horas que
/// termina em queda de energia ou em "encerrar tarefa" deixa, assim, um arquivo que qualquer tocador
/// abre — em vez de um WAV com tamanho zero no cabeçalho, que é o que sobra quando o cabeçalho só é
/// fechado no <c>Dispose</c>. É a diferença entre perder a reunião e perder os últimos segundos.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int TamanhoCabecalho = 44;

    private readonly FileStream _fs;
    private readonly int _canais;
    private readonly int _taxa;
    private long _quadros;
    private long _quadrosNoCabecalho = -1;
    private DateTime _ultimaAtualizacao = DateTime.MinValue;
    private bool _fechado;

    public WavWriter(string caminho, int taxa, int canais)
    {
        Caminho = caminho;
        _taxa = taxa;
        _canais = canais;
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        // 64 KB: uma escrita a cada ~0,7 s de áudio mono. Buffer menor faz o disco trabalhar à toa
        // durante uma reunião inteira; maior atrasa demais o que já está gravado.
        _fs = new FileStream(caminho, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _fs.Write(new byte[TamanhoCabecalho]);
        AtualizarCabecalho(forcar: true);
    }

    public string Caminho { get; }
    public long Quadros => _quadros;
    public TimeSpan Duracao => TimeSpan.FromSeconds((double)_quadros / _taxa);
    public int Taxa => _taxa;
    public int Canais => _canais;

    /// <summary>Grava amostras float intercaladas, cortando o que passar de -1..1.</summary>
    public void Escrever(float[] amostras, int quantidade)
    {
        if (_fechado || quantidade <= 0) return;

        var bytes = ArrayPool<byte>.Shared.Rent(quantidade * 2);
        try
        {
            var span = bytes.AsSpan(0, quantidade * 2);
            for (var i = 0; i < quantidade; i++)
            {
                var v = amostras[i];
                var s = v >= 1f ? short.MaxValue : v <= -1f ? short.MinValue : (short)(v * 32767f);
                span[i * 2] = (byte)(s & 0xFF);
                span[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            _fs.Write(span);
            _quadros += quantidade / _canais;
            AtualizarCabecalho(forcar: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>Preenche <paramref name="quadros"/> quadros com silêncio.</summary>
    public void EscreverSilencio(long quadros)
    {
        if (_fechado || quadros <= 0) return;

        // meio segundo por vez: silêncio de minutos (fone mudo numa reunião longa) não pode virar
        // uma alocação de dezenas de megabytes
        var lote = ArrayPool<byte>.Shared.Rent(_taxa / 2 * _canais * 2);
        try
        {
            Array.Clear(lote);
            var faltam = quadros * _canais * 2;
            while (faltam > 0)
            {
                var n = (int)Math.Min(faltam, lote.Length);
                _fs.Write(lote, 0, n);
                faltam -= n;
            }
            _quadros += quadros;
            AtualizarCabecalho(forcar: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lote);
        }
    }

    /// <summary>Regrava o cabeçalho com o tamanho atual, no máximo a cada 3 segundos.</summary>
    private void AtualizarCabecalho(bool forcar)
    {
        var agora = DateTime.UtcNow;
        if (!forcar && (agora - _ultimaAtualizacao).TotalSeconds < 3) return;
        if (!forcar && _quadros == _quadrosNoCabecalho) return;

        _ultimaAtualizacao = agora;
        _quadrosNoCabecalho = _quadros;

        var posicao = _fs.Position;
        var dados = (uint)Math.Min(_quadros * _canais * 2L, uint.MaxValue - TamanhoCabecalho);
        var bloco = (ushort)(_canais * 2);

        Span<byte> h = stackalloc byte[TamanhoCabecalho];
        var i = 0;
        Texto(h, ref i, "RIFF"); U32(h, ref i, 36 + dados); Texto(h, ref i, "WAVE");
        Texto(h, ref i, "fmt "); U32(h, ref i, 16); U16(h, ref i, 1); U16(h, ref i, (ushort)_canais);
        U32(h, ref i, (uint)_taxa); U32(h, ref i, (uint)(_taxa * bloco)); U16(h, ref i, bloco); U16(h, ref i, 16);
        Texto(h, ref i, "data"); U32(h, ref i, dados);

        _fs.Flush();
        _fs.Position = 0;
        _fs.Write(h);
        _fs.Flush(flushToDisk: false);
        _fs.Position = posicao;
    }

    private static void Texto(Span<byte> destino, ref int i, string s)
    {
        Encoding.ASCII.GetBytes(s, destino[i..]);
        i += s.Length;
    }

    private static void U32(Span<byte> destino, ref int i, uint v)
    {
        BitConverter.TryWriteBytes(destino[i..], v);
        i += 4;
    }

    private static void U16(Span<byte> destino, ref int i, ushort v)
    {
        BitConverter.TryWriteBytes(destino[i..], v);
        i += 2;
    }

    public void Dispose()
    {
        if (_fechado) return;
        _fechado = true;
        try
        {
            AtualizarCabecalho(forcar: true);
            _fs.Flush(flushToDisk: true);
        }
        catch
        {
            // disco arrancado no meio: o que já foi gravado continua lá
        }
        _fs.Dispose();
    }
}
