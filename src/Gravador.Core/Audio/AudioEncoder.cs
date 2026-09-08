using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Gravador.Core.Audio;

/// <summary>
/// Converte os WAV da sessão em MP3 usando o codificador que já vem no Windows (Media Foundation).
///
/// Por que MP3 e não AAC, tendo os dois: a mesa de formatos do Media Foundation nesta máquina
/// oferece AAC mono só a partir de 96 kbps, enquanto o MP3 mono desce até 16 kbps. Fala não precisa
/// de 96 kbps, e o tamanho do arquivo é o que decide se dá para arrastar a gravação para dentro de
/// uma IA: uma reunião de duas horas sai com 57 MB a 64 kbps contra 690 MB do WAV.
///
/// Por que não ffmpeg: seriam ~80 MB de binário a mais para carregar, e o codificador do sistema
/// entrega a mesma coisa para voz. Esta ferramenta precisa caber em máquina modesta.
/// </summary>
public static class AudioEncoder
{
    private static bool _iniciado;
    private static readonly object Trava = new();

    private static void Iniciar()
    {
        lock (Trava)
        {
            if (_iniciado) return;
            MediaFoundationApi.Startup();
            _iniciado = true;
        }
    }

    /// <summary>O Windows desta máquina tem o codificador de MP3? (Edições "N" podem não ter.)</summary>
    public static bool Mp3Disponivel
    {
        get
        {
            try
            {
                Iniciar();
                return MediaFoundationEncoder
                    .GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_MP3)
                    .Any();
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed record Resultado(bool Ok, string Caminho, long Bytes, string? Erro);

    /// <summary>
    /// Codifica <paramref name="wav"/> em MP3 ao lado dele. Devolve o caminho do WAV de volta se o
    /// codificador não existir — perder a gravação porque a máquina não tem um MFT seria absurdo.
    /// </summary>
    public static Resultado ParaMp3(string wav, int kbps, bool apagarWav, Action<double>? progresso = null,
        CancellationToken ct = default)
    {
        var saida = Path.ChangeExtension(wav, ".mp3");
        try
        {
            Iniciar();
            using (var leitor = new WaveFileReader(wav))
            {
                if (leitor.Length == 0)
                    return new Resultado(false, wav, 0, "A trilha ficou vazia.");

                var tipo = MediaFoundationEncoder.SelectMediaType(
                    AudioSubtypes.MFAudioFormat_MP3, leitor.WaveFormat, kbps * 1000);
                if (tipo == null)
                    return new Resultado(false, wav, TamanhoDe(wav),
                        "O Windows desta máquina não oferece codificador de MP3 para este formato.");

                var fonte = new ProvedorComProgresso(leitor, progresso, ct);
                using var enc = new MediaFoundationEncoder(tipo);
                enc.Encode(saida, fonte);
            }

            var bytes = TamanhoDe(saida);
            if (bytes <= 0) return new Resultado(false, wav, TamanhoDe(wav), "O MP3 saiu vazio.");

            if (apagarWav)
            {
                try { File.Delete(wav); } catch { /* arquivo em uso: fica o WAV também, sem drama */ }
            }
            progresso?.Invoke(1);
            return new Resultado(true, saida, bytes, null);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(saida); } catch { /* nada a fazer */ }
            return new Resultado(false, wav, TamanhoDe(wav), "Conversão cancelada.");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(saida)) File.Delete(saida); } catch { /* nada a fazer */ }
            return new Resultado(false, wav, TamanhoDe(wav), ex.Message);
        }
    }

    public static long TamanhoDe(string caminho)
    {
        try { return new FileInfo(caminho).Length; } catch { return 0; }
    }

    /// <summary>
    /// Repassa o WAV para o codificador contando quanto já passou.
    ///
    /// Existe porque <c>EncodeToMp3</c> converte tudo numa chamada só e não diz nada enquanto isso;
    /// numa reunião de duas horas isso é meio minuto de interface parada sem explicação.
    /// </summary>
    private sealed class ProvedorComProgresso(WaveStream fonte, Action<double>? aoAndar, CancellationToken ct)
        : IWaveProvider
    {
        private double _ultimoRelato = -1;

        public WaveFormat WaveFormat => fonte.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            var lidos = fonte.Read(buffer, offset, count);
            if (aoAndar != null && fonte.Length > 0)
            {
                var fracao = Math.Min(0.999, (double)fonte.Position / fonte.Length);
                // relata a cada 1%: o evento é barato, mas quem escuta costuma tocar na interface
                if (fracao - _ultimoRelato >= 0.01)
                {
                    _ultimoRelato = fracao;
                    aoAndar(fracao);
                }
            }
            return lidos;
        }
    }
}
