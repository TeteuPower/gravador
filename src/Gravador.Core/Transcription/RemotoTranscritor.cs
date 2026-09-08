using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Gravador.Core.Audio;
using Gravador.Core.Session;
using Gravador.Core.Settings;
using NAudio.Wave;

namespace Gravador.Core.Transcription;

/// <summary>
/// Transcreve a gravação pronta num serviço compatível com a API de áudio da OpenAI — a mesma forma
/// de chamada aceita por Whisper na OpenAI, pela Groq e por instalações locais de whisper.cpp com
/// servidor HTTP.
///
/// É a opção de maior qualidade em português e a mais leve para a máquina: quem faz a conta é o
/// servidor. Roda no fim, e não durante, porque o áudio inteiro dá ao modelo o contexto que a
/// transcrição ao vivo não tem — e porque uma reunião pede uma chamada, não milhares.
///
/// A chave NUNCA entra na configuração: ela é lida de uma variável de ambiente cujo NOME é o que
/// fica guardado. Assim o config.json pode ser copiado, versionado ou colado num chamado sem vazar
/// credencial.
/// </summary>
public sealed class RemotoTranscritor : ITranscritorDeArquivo
{
    /// <summary>
    /// Dez minutos por pedaço.
    ///
    /// Existe porque o limite de upload desses serviços costuma ser 25 MB, e uma reunião de duas
    /// horas passa disso. Dez minutos a 32 kbps mono dão ~2,4 MB: folgado, e curto o bastante para
    /// um pedaço que falhe ser reenviado sem refazer a reunião inteira.
    /// </summary>
    private static readonly TimeSpan Pedaco = TimeSpan.FromMinutes(10);

    /// <summary>16 kHz mono é o que todo modelo de fala usa por dentro. Mandar mais é pagar por nada.</summary>
    private const int TaxaDeEnvio = 16000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly AppSettings _config;
    private readonly string? _chave;

    public RemotoTranscritor(AppSettings config)
    {
        _config = config;
        _chave = Environment.GetEnvironmentVariable(config.RemotoChaveEnv);
        if (string.IsNullOrWhiteSpace(_chave))
            Motivo = $"A variável de ambiente {config.RemotoChaveEnv} não está definida nesta máquina. "
                   + "Defina-a com a chave do serviço e abra o Gravador de novo.";
        else if (string.IsNullOrWhiteSpace(config.RemotoUrl))
            Motivo = "Falta o endereço do serviço de transcrição nas configurações.";
    }

    public string Nome => "Transcrição remota (compatível com OpenAI)";
    public bool Disponivel => Motivo == null;
    public string? Motivo { get; }

    public async Task<IReadOnlyList<TrechoFalado>> TranscreverAsync(string arquivo, string fonte,
        IProgress<string>? etapa, CancellationToken ct)
    {
        if (!Disponivel) throw new InvalidOperationException(Motivo);
        if (!File.Exists(arquivo)) return [];

        var trechos = new List<TrechoFalado>();
        var temporaria = Path.Combine(Path.GetTempPath(), "Gravador", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaria);

        try
        {
            var pedacos = Fatiar(arquivo, temporaria, ct).ToList();
            for (var i = 0; i < pedacos.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (caminho, deslocamento) = pedacos[i];
                etapa?.Report($"Transcrevendo {fonte} — parte {i + 1} de {pedacos.Count}...");
                trechos.AddRange(await EnviarAsync(caminho, fonte, deslocamento, ct).ConfigureAwait(false));
            }
        }
        finally
        {
            try { Directory.Delete(temporaria, recursive: true); } catch { /* sobra no temp, sem drama */ }
        }

        trechos.Sort((a, b) => a.DeSegundos.CompareTo(b.DeSegundos));
        return trechos;
    }

    /// <summary>
    /// Quebra o áudio em pedaços de <see cref="Pedaco"/>, já reduzidos a 16 kHz mono e comprimidos.
    /// Devolve o caminho de cada um e em que segundo da reunião ele começa.
    /// </summary>
    private IEnumerable<(string Caminho, double Deslocamento)> Fatiar(string arquivo, string pasta, CancellationToken ct)
    {
        // MediaFoundationReader lê MP3, M4A e WAV com o mesmo código: a sessão pode ter terminado em
        // qualquer um dos três, dependendo da configuração de formato.
        using var leitor = new MediaFoundationReader(arquivo);
        var amostras = leitor.ToSampleProvider();
        var canais = amostras.WaveFormat.Channels;
        var taxa = amostras.WaveFormat.SampleRate;
        var passo = (double)taxa / TaxaDeEnvio;

        var buffer = new float[taxa * canais];
        var mono = new float[(int)(buffer.Length / canais / passo) + 2];

        var indice = 0;
        var quadrosNoPedaco = 0L;
        var quadrosPorPedaco = (long)(Pedaco.TotalSeconds * TaxaDeEnvio);
        var quadrosTotais = 0L;
        var inicioDoPedaco = 0L;

        WavWriter? wav = null;
        var caminhoWav = "";

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var lidos = amostras.Read(buffer, 0, buffer.Length);
                if (lidos <= 0) break;

                var quadros = lidos / canais;
                var escritos = 0;
                for (double p = 0; p < quadros && escritos < mono.Length; p += passo)
                {
                    var q = (int)p;
                    var soma = 0f;
                    for (var c = 0; c < canais; c++) soma += buffer[q * canais + c];
                    mono[escritos++] = soma / canais;
                }
                if (escritos == 0) continue;

                if (wav == null)
                {
                    caminhoWav = Path.Combine(pasta, $"parte{indice:000}.wav");
                    wav = new WavWriter(caminhoWav, TaxaDeEnvio, 1);
                    inicioDoPedaco = quadrosTotais;
                }

                wav.Escrever(mono, escritos);
                quadrosNoPedaco += escritos;
                quadrosTotais += escritos;

                if (quadrosNoPedaco >= quadrosPorPedaco)
                {
                    wav.Dispose();
                    wav = null;
                    quadrosNoPedaco = 0;
                    yield return (Comprimir(caminhoWav), (double)inicioDoPedaco / TaxaDeEnvio);
                    indice++;
                }
            }

            if (wav != null)
            {
                wav.Dispose();
                wav = null;
                yield return (Comprimir(caminhoWav), (double)inicioDoPedaco / TaxaDeEnvio);
            }
        }
        finally
        {
            wav?.Dispose();
        }
    }

    /// <summary>Comprime o pedaço para subir rápido. Se o MP3 falhar, sobe o WAV mesmo.</summary>
    private static string Comprimir(string wav)
    {
        var r = AudioEncoder.ParaMp3(wav, 32, apagarWav: true);
        return r.Ok ? r.Caminho : wav;
    }

    private async Task<IReadOnlyList<TrechoFalado>> EnviarAsync(string arquivo, string fonte,
        double deslocamento, CancellationToken ct)
    {
        using var conteudo = new MultipartFormDataContent();
        await using var fluxo = File.OpenRead(arquivo);
        var arquivoParte = new StreamContent(fluxo);
        arquivoParte.Headers.ContentType = new MediaTypeHeaderValue(
            arquivo.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav");
        conteudo.Add(arquivoParte, "file", Path.GetFileName(arquivo));
        conteudo.Add(new StringContent(_config.RemotoModelo), "model");
        conteudo.Add(new StringContent("verbose_json"), "response_format");

        // O código de duas letras é o que estes serviços esperam; "pt-BR" é recusado por alguns.
        var idioma = _config.IdiomaTranscricao.Split('-')[0];
        if (idioma.Length == 2) conteudo.Add(new StringContent(idioma), "language");

        using var pedido = new HttpRequestMessage(HttpMethod.Post, _config.RemotoUrl) { Content = conteudo };
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _chave);

        using var resposta = await Http.SendAsync(pedido, ct).ConfigureAwait(false);
        var corpo = await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resposta.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"O serviço de transcrição recusou o envio (HTTP {(int)resposta.StatusCode}). "
                + (corpo.Length > 300 ? corpo[..300] + "…" : corpo));

        return Ler(corpo, fonte, deslocamento);
    }

    private static List<TrechoFalado> Ler(string json, string fonte, double deslocamento)
    {
        var lista = new List<TrechoFalado>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;

            if (raiz.TryGetProperty("segments", out var segmentos) && segmentos.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segmentos.EnumerateArray())
                {
                    var texto = s.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(texto)) continue;
                    var de = s.TryGetProperty("start", out var a) && a.TryGetDouble(out var v1) ? v1 : 0;
                    var ate = s.TryGetProperty("end", out var b) && b.TryGetDouble(out var v2) ? v2 : de;
                    lista.Add(new TrechoFalado(de + deslocamento, ate + deslocamento, fonte, texto!, 1));
                }
                if (lista.Count > 0) return lista;
            }

            // Serviço que só devolve o texto corrido: melhor um trecho com tudo do que nada.
            if (raiz.TryGetProperty("text", out var inteiro) && inteiro.GetString() is { Length: > 0 } corrido)
                lista.Add(new TrechoFalado(deslocamento, deslocamento, fonte, corrido.Trim(), 1));
        }
        catch (JsonException)
        {
            // resposta fora do formato: some com este pedaço em vez de derrubar a transcrição toda
        }
        return lista;
    }

    public void Dispose() { }
}
