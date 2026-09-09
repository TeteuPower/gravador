using System.IO.Compression;
using System.Net.Http;

namespace Gravador.Core.Ferramentas;

/// <summary>Andamento de um download, para a interface mostrar.</summary>
public sealed record ProgressoDeFerramenta(string Ferramenta, string Etapa, long BytesRecebidos, long? BytesTotais)
{
    public double? Fracao => BytesTotais is { } t and > 0 ? Math.Min(1, (double)BytesRecebidos / t) : null;
}

/// <summary>
/// Um binário de terceiros que a ferramenta baixa quando (e só quando) precisa dele.
///
/// O gravador em si não depende de nada disto: gravar reunião continua sendo .NET puro. As
/// dependências entram por funcionalidades que o Windows não cobre — decodificar vídeo (ffmpeg) e
/// transcrever offline com qualidade (whisper.cpp) — e quem nunca importa um vídeo nem transcreve
/// nunca baixa um byte. É a revisão consciente da decisão "sem ffmpeg" do início: ela valia para
/// codificar áudio, onde o Windows já tem codificador; não vale para vídeo.
///
/// Onde procurar, em ordem: a pasta de ferramentas do Gravador, depois o PATH (quem já tem ffmpeg
/// instalado não precisa de outra cópia). O download vai para um arquivo temporário e só é movido
/// para o lugar no fim: uma queda de rede no meio não deixa um exe pela metade parecendo instalado.
/// </summary>
public sealed class FerramentaExterna
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <param name="Nome">Como aparece para a pessoa ("ffmpeg").</param>
    /// <param name="Subpasta">Pasta dentro de <see cref="AppInfo.PastaFerramentas"/>.</param>
    /// <param name="Arquivo">Nome do arquivo final (ex.: "ffmpeg.exe").</param>
    /// <param name="Url">De onde baixar.</param>
    /// <param name="TamanhoAproximadoMb">Para a interface avisar o custo antes de baixar.</param>
    /// <param name="EntradasDoZip">
    /// Quando a URL é um zip: sufixos das entradas a extrair (ex.: "bin/ffmpeg.exe"). Vazio significa
    /// que a URL já é o arquivo final, e não um zip.
    /// </param>
    /// <param name="ProcurarNoPath">Aceitar uma cópia já instalada na máquina.</param>
    public FerramentaExterna(string Nome, string Subpasta, string Arquivo, string Url, int TamanhoAproximadoMb,
        string[] EntradasDoZip, bool ProcurarNoPath)
    {
        this.Nome = Nome;
        this.Subpasta = Subpasta;
        this.Arquivo = Arquivo;
        this.Url = Url;
        this.TamanhoAproximadoMb = TamanhoAproximadoMb;
        this.EntradasDoZip = EntradasDoZip;
        this.ProcurarNoPath = ProcurarNoPath;
    }

    public string Nome { get; }
    public string Subpasta { get; }
    public string Arquivo { get; }
    public string Url { get; }
    public int TamanhoAproximadoMb { get; }
    public string[] EntradasDoZip { get; }
    public bool ProcurarNoPath { get; }

    public string Pasta => Path.Combine(AppInfo.PastaFerramentas, Subpasta);
    public string CaminhoLocal => Path.Combine(Pasta, Arquivo);

    /// <summary>Caminho de uma cópia utilizável, ou null se ainda não há.</summary>
    public string? Localizar()
    {
        if (File.Exists(CaminhoLocal)) return CaminhoLocal;
        if (!ProcurarNoPath) return null;

        foreach (var pasta in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(pasta)) continue;
            try
            {
                var candidato = Path.Combine(pasta.Trim(), Arquivo);
                if (File.Exists(candidato)) return candidato;
            }
            catch
            {
                // entrada de PATH inválida
            }
        }
        return null;
    }

    public bool Disponivel => Localizar() != null;

    /// <summary>Devolve o caminho, baixando antes se preciso.</summary>
    public async Task<string> GarantirAsync(IProgress<ProgressoDeFerramenta>? progresso = null, CancellationToken ct = default)
    {
        if (Localizar() is { } pronto) return pronto;

        Directory.CreateDirectory(Pasta);
        var temporario = CaminhoLocal + ".baixando";

        try
        {
            progresso?.Report(new ProgressoDeFerramenta(Nome, "baixando", 0, null));
            using (var resposta = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resposta.EnsureSuccessStatusCode();
                var total = resposta.Content.Headers.ContentLength;
                await using var origem = await resposta.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var destino = new FileStream(temporario, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

                var buffer = new byte[1 << 16];
                long recebidos = 0;
                var ultimoRelato = DateTime.UtcNow;
                int n;
                while ((n = await origem.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destino.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    recebidos += n;
                    // relata a cada 200 ms: a barra não precisa de mais, e o evento toca na interface
                    if ((DateTime.UtcNow - ultimoRelato).TotalMilliseconds > 200)
                    {
                        ultimoRelato = DateTime.UtcNow;
                        progresso?.Report(new ProgressoDeFerramenta(Nome, "baixando", recebidos, total));
                    }
                }
                progresso?.Report(new ProgressoDeFerramenta(Nome, "baixando", recebidos, total ?? recebidos));
            }

            if (EntradasDoZip.Length == 0)
            {
                File.Move(temporario, CaminhoLocal, overwrite: true);
            }
            else
            {
                progresso?.Report(new ProgressoDeFerramenta(Nome, "extraindo", 0, null));
                Extrair(temporario);
                File.Delete(temporario);
            }

            if (!File.Exists(CaminhoLocal))
                throw new InvalidOperationException($"O download de {Nome} terminou, mas {Arquivo} não apareceu no pacote.");

            progresso?.Report(new ProgressoDeFerramenta(Nome, "pronto", 0, 0));
            return CaminhoLocal;
        }
        catch
        {
            try { File.Delete(temporario); } catch { /* nada a fazer */ }
            throw;
        }
    }

    /// <summary>
    /// Extrai as entradas pedidas para a pasta, ACHATANDO o caminho: o zip do ffmpeg tem
    /// "ffmpeg-9.0.1-essentials_build/bin/ffmpeg.exe" e o do whisper tem "Release/whisper-cli.exe";
    /// o que interessa é o nome do arquivo, ao lado dos outros da mesma ferramenta.
    /// </summary>
    private void Extrair(string zip)
    {
        using var arquivo = ZipFile.OpenRead(zip);
        foreach (var entrada in arquivo.Entries)
        {
            if (string.IsNullOrEmpty(entrada.Name)) continue; // diretório
            var nome = entrada.FullName.Replace('\\', '/');
            var quer = EntradasDoZip.Any(sufixo =>
                sufixo.EndsWith("/*", StringComparison.Ordinal)
                    ? nome.StartsWith(sufixo[..^1], StringComparison.OrdinalIgnoreCase) && !nome[(sufixo.Length - 1)..].Contains('/')
                    : nome.EndsWith(sufixo, StringComparison.OrdinalIgnoreCase));
            if (!quer) continue;

            var destino = Path.Combine(Pasta, entrada.Name);
            entrada.ExtractToFile(destino, overwrite: true);
        }
    }
}

/// <summary>O catálogo do que a ferramenta sabe baixar.</summary>
public static class Ferramentas
{
    /// <summary>
    /// A build "essentials" do gyan.dev: 111 MB de zip, dos quais só o ffmpeg.exe (103 MB, estático)
    /// interessa. A build completa tem 195 MB e nada a mais que sirva aqui.
    /// </summary>
    public static readonly FerramentaExterna Ffmpeg = new(
        "ffmpeg", "ffmpeg", "ffmpeg.exe",
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        111, ["bin/ffmpeg.exe", "bin/ffprobe.exe"], ProcurarNoPath: true);

    public static readonly FerramentaExterna Ffprobe = new(
        "ffprobe", "ffmpeg", "ffprobe.exe",
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        111, ["bin/ffmpeg.exe", "bin/ffprobe.exe"], ProcurarNoPath: true);

    /// <summary>
    /// whisper.cpp para CPU genérica: 8 MB. A build com CUDA seria 10× mais rápida em quem tem placa
    /// NVIDIA, mas custa 270 a 670 MB e não roda em quem não tem — a de CPU roda em qualquer máquina,
    /// e a transcrição é pós-processamento, onde esperar alguns minutos é aceitável.
    /// </summary>
    public static readonly FerramentaExterna WhisperCli = new(
        "whisper.cpp", "whisper", "whisper-cli.exe",
        "https://github.com/ggml-org/whisper.cpp/releases/download/b4938/whisper-bin-x64.zip",
        9, ["Release/*"], ProcurarNoPath: false);

    /// <summary>Modelo ggml do whisper, pelo nome curto ("base", "small", "medium").</summary>
    public static FerramentaExterna ModeloWhisper(string nome)
    {
        var mb = nome switch
        {
            "tiny" => 75,
            "base" => 148,
            "small" => 488,
            "medium" => 1530,
            "large-v3-turbo" => 1620,
            _ => 148,
        };
        return new FerramentaExterna(
            $"modelo whisper {nome}", Path.Combine("whisper", "modelos"), $"ggml-{nome}.bin",
            $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{nome}.bin",
            mb, [], ProcurarNoPath: false);
    }
}
