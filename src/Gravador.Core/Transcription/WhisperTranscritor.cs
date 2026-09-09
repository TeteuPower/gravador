using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gravador.Core.Audio;
using Gravador.Core.Ferramentas;
using Gravador.Core.Session;
using Gravador.Core.Settings;
using NAudio.Wave;

namespace Gravador.Core.Transcription;

/// <summary>
/// Transcrição local com o whisper.cpp, baixado sob demanda junto com o modelo.
///
/// É o motor certo para o pós-processamento: roda offline, não custa nada por minuto, é excelente em
/// inglês e bom em português. Medido nesta máquina com o modelo base e 12 threads: 48 minutos de
/// apresentação transcritos em 6m45s — 7× tempo real numa CPU, sem placa de vídeo.
///
/// Ele NÃO roda ao vivo de propósito. Em máquina modesta, transcrever em tempo real disputa a CPU
/// com a reunião que está sendo gravada — e o requisito desta ferramenta é não atrapalhar a reunião.
///
/// O áudio vai para ele em WAV de 16 kHz mono, que é o formato interno do modelo. Mandar 48 kHz
/// estéreo só faria o whisper converter por dentro, mais devagar.
/// </summary>
public sealed class WhisperTranscritor : ITranscritorDeArquivo
{
    private const int TaxaDoModelo = 16000;

    private readonly AppSettings _config;

    public WhisperTranscritor(AppSettings config)
    {
        _config = config;
        IdiomaPedido = config.IdiomaTranscricao;
    }

    public string Nome => $"whisper.cpp ({_config.WhisperModelo})";

    /// <summary>Sempre "disponível": o que falta é baixado na primeira vez, com aviso do tamanho.</summary>
    public bool Disponivel => true;

    public string? Motivo => null;
    public string IdiomaPedido { get; set; }
    public string? IdiomaDetectado { get; private set; }

    /// <summary>Quanto ainda precisa ser baixado, para a interface avisar antes.</summary>
    public int MegabytesPorBaixar =>
        (Ferramentas.Ferramentas.WhisperCli.Disponivel ? 0 : Ferramentas.Ferramentas.WhisperCli.TamanhoAproximadoMb)
        + (Ferramentas.Ferramentas.ModeloWhisper(_config.WhisperModelo).Disponivel ? 0 : Ferramentas.Ferramentas.ModeloWhisper(_config.WhisperModelo).TamanhoAproximadoMb);

    public async Task<IReadOnlyList<TrechoFalado>> TranscreverAsync(string arquivo, string fonte,
        IProgress<string>? etapa, CancellationToken ct)
    {
        if (!File.Exists(arquivo)) return [];

        var progressoDownload = new Progress<ProgressoDeFerramenta>(p =>
        {
            if (p.Etapa == "baixando")
                etapa?.Report(p.Fracao is { } f
                    ? $"Baixando {p.Ferramenta}... {f * 100:0}% de {p.BytesTotais / 1_000_000} MB"
                    : $"Baixando {p.Ferramenta}...");
        });
        var exe = await Ferramentas.Ferramentas.WhisperCli.GarantirAsync(progressoDownload, ct).ConfigureAwait(false);
        var modelo = await Ferramentas.Ferramentas.ModeloWhisper(_config.WhisperModelo).GarantirAsync(progressoDownload, ct).ConfigureAwait(false);

        var temporaria = Path.Combine(Path.GetTempPath(), "Gravador", "whisper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaria);
        try
        {
            etapa?.Report("Preparando o áudio para o whisper...");
            var wav = Path.Combine(temporaria, "audio.wav");
            TimeSpan duracao;
            await Task.Run(() => duracao = Preparar(arquivo, wav, ct), ct).ConfigureAwait(false);
            duracao = new WaveFileReader(wav).TotalTime;

            var threads = _config.WhisperThreads > 0 ? _config.WhisperThreads : Math.Max(2, Environment.ProcessorCount / 2);
            var saida = Path.Combine(temporaria, "saida");
            var idioma = Transcritores.CodigoCurto(IdiomaPedido);

            etapa?.Report($"Transcrevendo {Formato.Duracao(duracao)} com o whisper ({_config.WhisperModelo}, {threads} threads)...");
            await ExecutarAsync(exe,
            [
                "-m", modelo, "-f", wav, "-l", idioma,
                "-t", threads.ToString(CultureInfo.InvariantCulture),
                "-oj", "-of", saida, "-np", "-pp",
            ], linha =>
            {
                // "whisper_print_progress_callback: progress =  42%"
                var i = linha.IndexOf("progress =", StringComparison.Ordinal);
                if (i >= 0 && int.TryParse(linha[(i + 10)..].Trim().TrimEnd('%'), out var pct))
                    etapa?.Report($"Transcrevendo com o whisper... {pct}%");
            }, ct).ConfigureAwait(false);

            var json = saida + ".json";
            if (!File.Exists(json)) throw new InvalidOperationException("O whisper terminou sem gravar o resultado.");
            return Ler(await File.ReadAllTextAsync(json, ct).ConfigureAwait(false), fonte);
        }
        finally
        {
            try { Directory.Delete(temporaria, recursive: true); } catch { /* sobra no temp */ }
        }
    }

    /// <summary>Qualquer entrada (MP3, WAV, M4A) para WAV 16 kHz mono, pelo Media Foundation.</summary>
    private static TimeSpan Preparar(string origem, string destino, CancellationToken ct)
    {
        using var leitor = new MediaFoundationReader(origem);
        var conversor = new SampleConverter(leitor.WaveFormat, TaxaDoModelo, 1);
        using var wav = new WavWriter(destino, TaxaDoModelo, 1);
        var buffer = new byte[leitor.WaveFormat.AverageBytesPerSecond];
        int n;
        while ((n = leitor.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (amostras, quantidade) = conversor.Converter(buffer, n);
            if (quantidade > 0) wav.Escrever(amostras, quantidade);
        }
        return wav.Duracao;
    }

    private List<TrechoFalado> Ler(string json, string fonte)
    {
        var lista = new List<TrechoFalado>();
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;

        if (raiz.TryGetProperty("result", out var resultado) && resultado.TryGetProperty("language", out var lang))
            IdiomaDetectado = lang.GetString();

        if (!raiz.TryGetProperty("transcription", out var segmentos)) return lista;
        foreach (var s in segmentos.EnumerateArray())
        {
            var texto = s.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(texto)) continue;
            // o whisper marca ruído entre parênteses: "(audience member mumbles)", "[MUSIC]"
            if ((texto.StartsWith('(') && texto.EndsWith(')')) || (texto.StartsWith('[') && texto.EndsWith(']'))) continue;

            double de = 0, ate = 0;
            if (s.TryGetProperty("offsets", out var o))
            {
                de = o.TryGetProperty("from", out var f) ? f.GetDouble() / 1000.0 : 0;
                ate = o.TryGetProperty("to", out var a) ? a.GetDouble() / 1000.0 : de;
            }
            lista.Add(new TrechoFalado(de, ate, fonte, texto!, 1));
        }
        return lista;
    }

    private static async Task ExecutarAsync(string exe, IReadOnlyList<string> args, Action<string> aoLerLinha, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = new Process { StartInfo = info };
        var erro = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) aoLerLinha(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { aoLerLinha(e.Data); erro.AppendLine(e.Data); } };

        if (!p.Start()) throw new InvalidOperationException("O whisper não iniciou.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* já morreu */ }
            throw;
        }
        if (p.ExitCode != 0)
        {
            var msg = erro.ToString().Trim();
            throw new InvalidOperationException($"O whisper falhou (código {p.ExitCode}). {(msg.Length > 400 ? msg[^400..] : msg)}");
        }
    }

    public void Dispose() { }
}
