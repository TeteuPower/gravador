using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gravador.Core.Ferramentas;
using Catalogo = Gravador.Core.Ferramentas.Ferramentas;

namespace Gravador.Core.Importacao;

/// <summary>O que um arquivo de mídia tem dentro, pelo ffprobe.</summary>
public sealed record InfoDeMidia(
    TimeSpan Duracao,
    bool TemVideo,
    bool TemAudio,
    int Largura,
    int Altura,
    double QuadrosPorSegundo,
    string? CodecVideo,
    string? CodecAudio,
    int TaxaAudio,
    int CanaisAudio);

/// <summary>
/// Invocações do ffmpeg/ffprobe que a importação precisa. Tudo passa por aqui para o resto do código
/// não conhecer linha de comando — e para o dia em que o binário mudar de lugar ser um dia só.
/// </summary>
public static class Ffmpeg
{
    private static readonly string[] ExtensoesDeVideo = [".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".m4v", ".ts", ".flv"];

    public static bool PareceVideo(string caminho) =>
        ExtensoesDeVideo.Contains(Path.GetExtension(caminho).ToLowerInvariant());

    /// <summary>Lê o arquivo com o ffprobe. Lança se o ffprobe não estiver disponível.</summary>
    public static async Task<InfoDeMidia> SondarAsync(string arquivo, IProgress<ProgressoDeFerramenta>? progresso = null,
        CancellationToken ct = default)
    {
        var ffprobe = await Catalogo.Ffprobe.GarantirAsync(progresso, ct).ConfigureAwait(false);
        var saida = await ExecutarAsync(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", arquivo], null, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(saida);
        var raiz = doc.RootElement;

        var duracao = TimeSpan.Zero;
        if (raiz.TryGetProperty("format", out var formato) && formato.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seg))
            duracao = TimeSpan.FromSeconds(seg);

        bool temVideo = false, temAudio = false;
        int largura = 0, altura = 0, taxa = 0, canais = 0;
        double fps = 0;
        string? codecV = null, codecA = null;

        if (raiz.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var tipo = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (tipo == "video" && !temVideo)
                {
                    // capa de MP3 vem como stream de vídeo com uma imagem só: não é vídeo
                    if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1)
                        continue;
                    temVideo = true;
                    largura = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    altura = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    codecV = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    fps = Fracao(s.TryGetProperty("avg_frame_rate", out var fr) ? fr.GetString() : null);
                    if (fps <= 0) fps = Fracao(s.TryGetProperty("r_frame_rate", out var rr) ? rr.GetString() : null);
                }
                else if (tipo == "audio" && !temAudio)
                {
                    temAudio = true;
                    codecA = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    taxa = s.TryGetProperty("sample_rate", out var sr) && int.TryParse(sr.GetString(), out var tv) ? tv : 0;
                    canais = s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
                }
            }
        }

        return new InfoDeMidia(duracao, temVideo, temAudio, largura, altura, fps, codecV, codecA, taxa, canais);
    }

    private static double Fracao(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return 0;
        var partes = texto.Split('/');
        if (partes.Length == 2
            && double.TryParse(partes[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(partes[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd > 0)
            return n / dd;
        return double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>
    /// Amostra o vídeo a N quadros por segundo, em miniaturas JPEG numeradas a partir de 1.
    /// O quadro <c>i</c> corresponde ao instante <c>(i-1)/fps</c>.
    ///
    /// Relata o andamento lendo <c>-progress pipe:1</c>: numa hora de vídeo isto leva minutos, e uma
    /// barra parada não conta se está andando ou travou.
    /// </summary>
    public static async Task<int> MiniaturasAsync(string video, string pasta, double fps, int largura,
        TimeSpan duracaoTotal, IProgress<double>? progresso = null, CancellationToken ct = default)
    {
        var ffmpeg = await Catalogo.Ffmpeg.GarantirAsync(null, ct).ConfigureAwait(false);
        Directory.CreateDirectory(pasta);

        var filtro = string.Create(CultureInfo.InvariantCulture, $"fps={fps},scale={largura}:-2");
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", video,
            "-vf", filtro,
            "-q:v", "4",
            "-progress", "pipe:1",
            Path.Combine(pasta, "%05d.jpg"),
        };

        await ExecutarAsync(ffmpeg, args, linha =>
        {
            // out_time_us=123456789 a cada ~meio segundo
            if (progresso != null && linha.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(linha[12..], out var us) && duracaoTotal.TotalSeconds > 0)
                progresso.Report(Math.Min(0.999, us / 1_000_000.0 / duracaoTotal.TotalSeconds));
        }, ct).ConfigureAwait(false);

        progresso?.Report(1);
        return Directory.GetFiles(pasta, "*.jpg").Length;
    }

    /// <summary>
    /// Extrai UM quadro em alta resolução no instante pedido, opcionalmente recortado.
    ///
    /// <c>-ss</c> antes de <c>-i</c>: o ffmpeg pula direto para o keyframe anterior e decodifica só
    /// dali — meio segundo por quadro num arquivo de 1 GB, em vez de decodificar desde o começo.
    /// </summary>
    public static async Task ExtrairQuadroAsync(string video, TimeSpan em, string destino,
        (int X, int Y, int Largura, int Altura)? recorte, int larguraMaxima, int qualidadeJpeg = 3,
        CancellationToken ct = default)
    {
        var ffmpeg = await Catalogo.Ffmpeg.GarantirAsync(null, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        var filtros = new List<string>();
        if (recorte is { } r && r.Largura > 0 && r.Altura > 0)
            filtros.Add(string.Create(CultureInfo.InvariantCulture, $"crop={Par(r.Largura)}:{Par(r.Altura)}:{Par(r.X)}:{Par(r.Y)}"));
        if (larguraMaxima > 0)
            filtros.Add(string.Create(CultureInfo.InvariantCulture, $"scale='min({larguraMaxima},iw)':-2"));

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-ss", em.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
            "-i", video,
            "-frames:v", "1",
        };
        if (filtros.Count > 0) { args.Add("-vf"); args.Add(string.Join(",", filtros)); }
        args.Add("-q:v"); args.Add(qualidadeJpeg.ToString(CultureInfo.InvariantCulture));
        args.Add(destino);

        await ExecutarAsync(ffmpeg, args, null, ct).ConfigureAwait(false);
    }

    /// <summary>Extrai o áudio como WAV PCM 16 bits — o plano B de quando o Media Foundation não abre o arquivo.</summary>
    public static async Task ExtrairAudioAsync(string origem, string destinoWav, int taxa, int canais, CancellationToken ct = default)
    {
        var ffmpeg = await Catalogo.Ffmpeg.GarantirAsync(null, ct).ConfigureAwait(false);
        await ExecutarAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", origem, "-vn",
            "-ac", canais.ToString(CultureInfo.InvariantCulture),
            "-ar", taxa.ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le", destinoWav,
        ], null, ct).ConfigureAwait(false);
    }

    /// <summary>Codecs de vídeo exigem dimensões pares; o recorte vindo da análise pode ser ímpar.</summary>
    private static int Par(int v) => v % 2 == 0 ? v : v - 1;

    // ------------------------------------------------------------------

    private static async Task<string> ExecutarAsync(string exe, IReadOnlyList<string> args, Action<string>? aoLerLinha, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = new Process { StartInfo = info };
        var saida = new StringBuilder();
        var erro = new StringBuilder();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (aoLerLinha != null) aoLerLinha(e.Data);
            else saida.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) erro.AppendLine(e.Data); };

        if (!p.Start()) throw new InvalidOperationException($"{Path.GetFileName(exe)} não iniciou.");
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
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} falhou (código {p.ExitCode}). {(msg.Length > 400 ? msg[..400] + "…" : msg)}");
        }
        return saida.ToString();
    }
}
