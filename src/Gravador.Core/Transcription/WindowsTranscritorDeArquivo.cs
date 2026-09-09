using System.Speech.AudioFormat;
using System.Speech.Recognition;
using Gravador.Core.Audio;
using Gravador.Core.Session;
using Gravador.Core.Settings;
using NAudio.Wave;

namespace Gravador.Core.Transcription;

/// <summary>
/// O reconhecedor do Windows lendo um arquivo pronto.
///
/// É o caminho de zero download: nada a baixar, nada a pagar. Também é o de pior qualidade — o motor
/// foi feito para comando de voz, e numa apresentação corrida ele perde palavra sim, palavra não.
/// Existe para a pessoa ter ALGUMA transcrição quando não pode baixar 150 MB nem tem chave de serviço,
/// e para localizar trechos; não para virar ata.
///
/// Só funciona nos idiomas com pacote de fala instalado. Num Windows em português normalmente só o
/// en-US está — que, por acaso, serve para uma apresentação em inglês.
/// </summary>
public sealed class WindowsTranscritorDeArquivo : ITranscritorDeArquivo
{
    private const int Taxa = 16000;
    private readonly AppSettings _config;

    public WindowsTranscritorDeArquivo(AppSettings config)
    {
        _config = config;
        IdiomaPedido = config.IdiomaTranscricao;
    }

    public string Nome => "Reconhecimento de fala do Windows (arquivo)";
    public string IdiomaPedido { get; set; }
    public string? IdiomaDetectado { get; private set; }

    public bool Disponivel => Escolher(out _) != null;

    public string? Motivo
    {
        get
        {
            Escolher(out var motivo);
            return motivo;
        }
    }

    private RecognizerInfo? Escolher(out string? motivo)
    {
        motivo = null;
        try
        {
            var instalados = SpeechRecognitionEngine.InstalledRecognizers();
            if (instalados.Count == 0) { motivo = "Este Windows não tem nenhum reconhecedor de fala instalado."; return null; }

            var pedido = Transcritores.CodigoCurto(IdiomaPedido);
            if (pedido == "auto")
            {
                // sem detecção de idioma neste motor: fica com o primeiro instalado e avisa qual
                return instalados[0];
            }
            var achado = instalados.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName.Equals(pedido, StringComparison.OrdinalIgnoreCase));
            if (achado != null) return achado;

            motivo = $"Não há reconhecedor de fala em '{pedido}' neste Windows (instalados: "
                   + string.Join(", ", instalados.Select(r => r.Culture.Name).Distinct()) + ").";
            return null;
        }
        catch (Exception ex)
        {
            motivo = "O reconhecimento de fala do Windows não pôde ser consultado: " + ex.Message;
            return null;
        }
    }

    public async Task<IReadOnlyList<TrechoFalado>> TranscreverAsync(string arquivo, string fonte,
        IProgress<string>? etapa, CancellationToken ct)
    {
        var info = Escolher(out var motivo) ?? throw new InvalidOperationException(motivo);
        IdiomaDetectado = info.Culture.Name;

        var temporario = Path.Combine(Path.GetTempPath(), "Gravador", "winspeech-" + Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(temporario)!);
        try
        {
            etapa?.Report("Preparando o áudio...");
            TimeSpan duracao = TimeSpan.Zero;
            await Task.Run(() =>
            {
                using var leitor = new MediaFoundationReader(arquivo);
                var conversor = new SampleConverter(leitor.WaveFormat, Taxa, 1);
                using var wav = new WavWriter(temporario, Taxa, 1);
                var buffer = new byte[leitor.WaveFormat.AverageBytesPerSecond];
                int n;
                while ((n = leitor.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (amostras, quantidade) = conversor.Converter(buffer, n);
                    if (quantidade > 0) wav.Escrever(amostras, quantidade);
                }
                duracao = wav.Duracao;
            }, ct).ConfigureAwait(false);

            etapa?.Report($"Transcrevendo {Formato.Duracao(duracao)} com o reconhecedor do Windows ({info.Culture.Name})...");
            return await Task.Run(() => Reconhecer(info, temporario, fonte, duracao, etapa, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temporario); } catch { /* temp */ }
        }
    }

    private static List<TrechoFalado> Reconhecer(RecognizerInfo info, string wav, string fonte, TimeSpan duracao,
        IProgress<string>? etapa, CancellationToken ct)
    {
        var lista = new List<TrechoFalado>();
        using var motor = new SpeechRecognitionEngine(info);
        motor.LoadGrammar(new DictationGrammar());
        motor.EndSilenceTimeout = TimeSpan.FromMilliseconds(700);
        motor.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(900);
        motor.BabbleTimeout = TimeSpan.FromSeconds(30);
        motor.InitialSilenceTimeout = TimeSpan.Zero;
        motor.SetInputToWaveFile(wav);

        var pronto = new ManualResetEventSlim(false);
        var ultimoRelato = DateTime.MinValue;

        motor.SpeechRecognized += (_, e) =>
        {
            var texto = e.Result?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(texto) || e.Result?.Audio == null) return;
            var de = e.Result.Audio.AudioPosition;
            lista.Add(new TrechoFalado(de.TotalSeconds, (de + e.Result.Audio.Duration).TotalSeconds, fonte, texto, e.Result.Confidence));
            if ((DateTime.UtcNow - ultimoRelato).TotalSeconds > 2 && duracao.TotalSeconds > 0)
            {
                ultimoRelato = DateTime.UtcNow;
                etapa?.Report($"Transcrevendo com o Windows... {de.TotalSeconds / duracao.TotalSeconds * 100:0}%");
            }
        };
        motor.RecognizeCompleted += (_, _) => pronto.Set();

        motor.RecognizeAsync(RecognizeMode.Multiple);
        while (!pronto.Wait(250))
        {
            if (ct.IsCancellationRequested)
            {
                motor.RecognizeAsyncCancel();
                ct.ThrowIfCancellationRequested();
            }
        }
        return lista;
    }

    public void Dispose() { }
}
