using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gravador.Core.Settings;

namespace Gravador.Core.Atualizacao;

/// <summary>Uma versão publicada no GitHub, quando ela é mais nova do que a instalada.</summary>
public sealed class VersaoDisponivel
{
    public string Versao = "";
    public string Tag = "";
    public string Notas = "";
    public string UrlDoInstalador = "";
    public string UrlDaPagina = "";

    /// <summary>Tamanho do instalador anexado. Vale mostrar: são dezenas de megabytes.</summary>
    public long Bytes;
}

/// <summary>
/// Procura versão nova nas releases do repositório e, quando existe, baixa o instalador e o roda
/// em modo silencioso.
///
/// O instalador já sabe se atualizar no lugar — fecha o que estiver rodando, reaproveita a pasta e
/// as opções da instalação anterior e abre o Gravador de volta na bandeja. Então atualizar pelo app
/// é o mesmo caminho de sempre, só que sem as telas do assistente: não existe um segundo mecanismo
/// de troca de arquivos para dar errado de um jeito diferente.
///
/// Nada aqui decide sozinho instalar. Quem grava reunião não pode ter o programa fechado no meio
/// dela por conta de uma atualização; a instalação é sempre um clique de quem está usando, e a
/// interface recusa enquanto houver gravação em andamento.
/// </summary>
public sealed class Atualizador
{
    private static readonly HttpClient Rede = CriarCliente();

    private static HttpClient CriarCliente()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // a API do GitHub recusa quem não se identifica
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Gravador/" + AppInfo.Versao);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Quando o GitHub foi consultado pela última vez.</summary>
    public DateTimeOffset UltimaConsulta { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>O que a última consulta achou, ou <c>null</c> se já estamos na versão mais nova.</summary>
    public VersaoDisponivel? Disponivel { get; private set; }

    /// <summary>
    /// Consulta as releases. Sem <paramref name="forcar"/>, respeita o intervalo mínimo entre
    /// consultas: a API pública do GitHub dá 60 chamadas por hora por IP, e uma ferramenta que
    /// fica aberta o dia inteiro não tem por que gastá-las.
    /// </summary>
    public async Task<VersaoDisponivel?> ProcurarAsync(AppSettings config, bool forcar = false,
        CancellationToken ct = default)
    {
        if (!forcar)
        {
            if (!config.VerificarAtualizacoes) return null;
            if (DateTimeOffset.Now - UltimaConsulta < TimeSpan.FromHours(6)) return Disponivel;
        }

        var repo = (config.RepositorioDeAtualizacao ?? "").Trim();
        if (repo.Length == 0) return null;

        try
        {
            // /releases/latest ignora pré-releases — e a "latest" da esteira, gerada a cada push na
            // main, é exatamente uma pré-release. Por isso listamos e escolhemos a maior versão aqui.
            var url = $"https://api.github.com/repos/{repo}/releases?per_page=15";
            using var resposta = await Rede.GetAsync(url, ct).ConfigureAwait(false);
            UltimaConsulta = DateTimeOffset.Now;
            if (!resposta.IsSuccessStatusCode) return null;

            var corpo = await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            VersaoDisponivel? melhor = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var rascunho) && rascunho.ValueKind == JsonValueKind.True)
                    continue;

                var previa = release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
                if (previa && !config.IncluirPreReleases) continue;

                var info = Ler(release, repo);
                if (info == null) continue;
                if (melhor == null || MaisNova(info.Versao, melhor.Versao)) melhor = info;
            }

            Disponivel = melhor != null && MaisNova(melhor.Versao, AppInfo.Versao) ? melhor : null;
            return Disponivel;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // sem rede, repositório privado, resposta em formato inesperado: silencioso de propósito.
            // Procurar atualização é acessório; falhar aqui não pode atrapalhar quem está gravando.
            return null;
        }
    }

    /// <summary>Baixa o instalador para a pasta temporária, informando o progresso (0 a 1).</summary>
    public static async Task<string?> BaixarAsync(VersaoDisponivel info, IProgress<double>? progresso = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.UrlDoInstalador)) return null;

        var pasta = Path.Combine(Path.GetTempPath(), "GravadorAtualizacao");
        Directory.CreateDirectory(pasta);
        var destino = Path.Combine(pasta, $"Gravador-Setup-{info.Versao}.exe");

        try
        {
            using var resposta = await Rede
                .GetAsync(info.UrlDoInstalador, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resposta.IsSuccessStatusCode) return null;

            var total = resposta.Content.Headers.ContentLength ?? info.Bytes;
            using var origem = await resposta.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var arquivo = new FileStream(destino, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long lidos = 0;
            int n;
            while ((n = await origem.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await arquivo.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                lidos += n;
                if (total > 0) progresso?.Report(Math.Clamp((double)lidos / total, 0, 1));
            }

            return destino;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Roda o instalador em modo silencioso. Ele fecha o Gravador, troca os arquivos e o abre de
    /// volta na bandeja — por isso este processo não precisa (nem consegue) fazer nada depois.
    /// </summary>
    public static bool Instalar(string caminhoDoInstalador)
    {
        try
        {
            var inicio = new ProcessStartInfo
            {
                FileName = caminhoDoInstalador,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            };

            // Instalado em Arquivos de Programas, trocar os arquivos exige administrador, e em modo
            // silencioso o instalador não tem como pedir elevação sozinho: quem pede é o app.
            if (PrecisaDeAdministrador()) inicio.Verb = "runas";

            Process.Start(inicio);
            return true;
        }
        catch
        {
            return false; // inclui a pessoa recusar o pedido de elevação
        }
    }

    /// <summary>A pasta do app é gravável por quem está usando? Se não for, a troca precisa de admin.</summary>
    private static bool PrecisaDeAdministrador()
    {
        try
        {
            var sonda = Path.Combine(AppContext.BaseDirectory, ".atualizacao-sonda.tmp");
            using (File.Create(sonda, 1, FileOptions.DeleteOnClose)) { }
            return false;
        }
        catch
        {
            return true;
        }
    }

    public static void AbrirPagina(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // sem navegador padrão: nada a fazer
        }
    }

    // ==================================================================

    /// <summary>
    /// Lê uma release. A versão vem da tag quando ela é numérica ("v0.2.0"); quando é um canal fixo
    /// ("latest"), vem do nome do instalador anexado, que é Gravador-Setup-X.Y.Z.exe.
    /// </summary>
    private static VersaoDisponivel? Ler(JsonElement release, string repo)
    {
        var tag = Texto(release, "tag_name") ?? "";
        var info = new VersaoDisponivel
        {
            Tag = tag,
            Notas = Texto(release, "body") ?? "",
            UrlDaPagina = Texto(release, "html_url") ?? $"https://github.com/{repo}/releases",
        };

        // Uma release pode ter mais de um instalador anexado — a "latest" acumula um por build
        // quando a limpeza da esteira falha. Vale o de maior versão, nunca o primeiro da lista: a
        // ordem em que o GitHub devolve os anexos não significa nada.
        string? versaoDoArquivo = null;
        if (release.TryGetProperty("assets", out var anexos) && anexos.ValueKind == JsonValueKind.Array)
        {
            foreach (var anexo in anexos.EnumerateArray())
            {
                var nome = Texto(anexo, "name") ?? "";
                if (!nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                var v = VersaoNoNome(nome);
                var melhor = info.UrlDoInstalador.Length == 0
                             || (v != null && (versaoDoArquivo == null || MaisNova(v, versaoDoArquivo)));
                if (!melhor) continue;

                versaoDoArquivo = v ?? versaoDoArquivo;
                info.UrlDoInstalador = Texto(anexo, "browser_download_url") ?? "";
                info.Bytes = anexo.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            }
        }

        // Prioridade: tag numérica > nome do instalador > nome da release. O instalador vem antes do
        // nome da release porque é o arquivo que será de fato instalado; o nome é texto editável e
        // fica defasado se a chamada da esteira que o atualiza falhar (e ela às vezes falha).
        var daTag = SemOV(tag);
        info.Versao = Version.TryParse(Completar(daTag), out _)
            ? daTag
            : versaoDoArquivo ?? VersaoNoNome(Texto(release, "name") ?? "") ?? "";

        return string.IsNullOrEmpty(info.Versao) ? null : info;
    }

    /// <summary>Tira "0.2.0" de textos como "Gravador-Setup-0.2.0.exe" ou "Build 0.2.0 (main)".</summary>
    private static string? VersaoNoNome(string texto)
    {
        var m = Regex.Match(texto, @"(\d+\.\d+(\.\d+)?(\.\d+)?)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Texto(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>"v0.2.0" e "0.2.0" viram "0.2.0".</summary>
    public static string SemOV(string tag)
    {
        var t = tag.Trim();
        return t.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? t[1..] : t;
    }

    /// <summary>Compara campo a campo. Texto que não é versão nunca conta como "mais nova".</summary>
    public static bool MaisNova(string candidata, string atual)
    {
        if (!Version.TryParse(Completar(candidata), out var a)) return false;
        if (!Version.TryParse(Completar(atual), out var b)) return false;
        return a > b;
    }

    /// <summary><see cref="Version"/> exige pelo menos dois campos; "1" e "1.2" viram "1.0.0" e "1.2.0".</summary>
    internal static string Completar(string v)
    {
        var nucleo = v.Split('-', '+')[0];
        return nucleo.Split('.').Length switch
        {
            1 => nucleo + ".0.0",
            2 => nucleo + ".0",
            _ => nucleo,
        };
    }
}
