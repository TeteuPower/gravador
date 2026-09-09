using Gravador.Core.Audio;
using NAudio.Wave;

namespace Gravador.Core.Importacao;

/// <summary>
/// Tira o áudio de um arquivo de mídia e o deixa no formato interno da gravação.
///
/// Pelo Media Foundation primeiro: ele já vem no Windows, abre MP4/AAC, MP3, M4A e WAV, e decodifica
/// a 200× tempo real — os 48 minutos de uma apresentação saem em uns 14 segundos, sem baixar nada. O
/// ffmpeg é o plano B, para o contêiner ou codec que o Windows não conhece (MKV com Opus, por
/// exemplo), e só nesse caso ele é baixado.
/// </summary>
public static class ExtratorDeAudio
{
    /// <summary>Extrai para WAV no formato pedido. Devolve a duração do que foi escrito.</summary>
    public static async Task<TimeSpan> ExtrairAsync(string origem, string destinoWav, int taxa, int canais,
        IProgress<double>? progresso = null, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => PeloMediaFoundation(origem, destinoWav, taxa, canais, progresso, ct), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // formato que o Windows não abre: ffmpeg
            try { File.Delete(destinoWav); } catch { /* pode não existir */ }
            await Ffmpeg.ExtrairAudioAsync(origem, destinoWav, taxa, canais, ct).ConfigureAwait(false);
            using var leitor = new WaveFileReader(destinoWav);
            progresso?.Report(1);
            return leitor.TotalTime;
        }
    }

    private static TimeSpan PeloMediaFoundation(string origem, string destinoWav, int taxa, int canais,
        IProgress<double>? progresso, CancellationToken ct)
    {
        using var leitor = new MediaFoundationReader(origem);
        var conversor = new SampleConverter(leitor.WaveFormat, taxa, canais);
        using var wav = new WavWriter(destinoWav, taxa, canais);

        // um segundo de origem por volta: pouco o bastante para relatar progresso, muito o bastante
        // para o laço não dominar o custo
        var buffer = new byte[leitor.WaveFormat.AverageBytesPerSecond];
        var total = leitor.Length;
        long lidos = 0;
        var ultimoRelato = -1.0;

        int n;
        while ((n = leitor.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (amostras, quantidade) = conversor.Converter(buffer, n);
            if (quantidade > 0) wav.Escrever(amostras, quantidade);

            lidos += n;
            if (progresso != null && total > 0)
            {
                var fracao = Math.Min(0.999, (double)lidos / total);
                if (fracao - ultimoRelato >= 0.02) { ultimoRelato = fracao; progresso.Report(fracao); }
            }
        }

        progresso?.Report(1);
        return wav.Duracao;
    }
}
