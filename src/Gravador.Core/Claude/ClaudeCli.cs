using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Gravador.Core.Claude;

/// <summary>O que o `claude` devolveu.</summary>
public sealed record RespostaDoClaude(bool Ok, string Texto, string? Erro, double? CustoUsd, TimeSpan Duracao);

/// <summary>
/// Executa o `claude` em modo não interativo e traz a resposta.
///
/// O prompt vai pelo STDIN, e não como argumento: o resumo de uma reunião com transcrição passa
/// tranquilamente dos 32 mil caracteres que a linha de comando do Windows aceita, e o erro que isso
/// dá não diz o que aconteceu.
///
/// As ferramentas liberadas são só de leitura (<c>Read</c>, <c>Glob</c>), e o diretório de trabalho é
/// a pasta da sessão. Assim o Claude consegue abrir as capturas de tela e a transcrição — que estão
/// ali dentro — e não consegue mexer em mais nada da máquina.
/// </summary>
public static class ClaudeCli
{
    private static string? _cache;
    private static bool _procurou;

    /// <summary>
    /// Acha o executável do `claude`. Cobre a instalação por npm (um .cmd em %APPDATA%\npm) e a
    /// nativa (um .exe em %LOCALAPPDATA%), que são os dois jeitos oficiais no Windows.
    /// </summary>
    public static string? Localizar()
    {
        if (_procurou) return _cache;
        _procurou = true;

        var candidatos = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        candidatos.Add(Path.Combine(appData, "npm", "claude.cmd"));
        candidatos.Add(Path.Combine(localAppData, "Programs", "claude", "claude.exe"));
        candidatos.Add(Path.Combine(perfil, ".local", "bin", "claude.exe"));

        foreach (var pasta in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(pasta)) continue;
            candidatos.Add(Path.Combine(pasta.Trim(), "claude.exe"));
            candidatos.Add(Path.Combine(pasta.Trim(), "claude.cmd"));
        }

        _cache = candidatos.FirstOrDefault(c =>
        {
            try { return File.Exists(c); } catch { return false; }
        });
        return _cache;
    }

    /// <summary>Esquece o caminho achado — usado quando o usuário acabou de instalar o Claude Code.</summary>
    public static void Reprocurar() { _procurou = false; _cache = null; }

    public static async Task<RespostaDoClaude> PerguntarAsync(
        string prompt,
        string pastaDeTrabalho,
        string modelo,
        string? tokenOAuth,
        IProgress<string>? etapa = null,
        CancellationToken ct = default)
    {
        var exe = Localizar();
        if (exe == null)
            return new RespostaDoClaude(false, "", "O Claude Code não foi encontrado nesta máquina.", null, TimeSpan.Zero);

        var inicio = Stopwatch.StartNew();
        var info = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Directory.Exists(pastaDeTrabalho) ? pastaDeTrabalho : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        info.ArgumentList.Add("-p");
        info.ArgumentList.Add("--output-format");
        info.ArgumentList.Add("json");
        if (!string.IsNullOrWhiteSpace(modelo))
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(modelo);
        }
        info.ArgumentList.Add("--allowedTools");
        info.ArgumentList.Add("Read");
        info.ArgumentList.Add("Glob");
        info.ArgumentList.Add("--add-dir");
        info.ArgumentList.Add(pastaDeTrabalho);

        if (!string.IsNullOrWhiteSpace(tokenOAuth))
            info.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = tokenOAuth;

        // O terminal do VS Code exporta esta variável, e com ela o launcher do Claude Code sobe como
        // Node puro e não faz nada. Mesma armadilha que o Limpador documenta no iniciar.cmd.
        info.Environment["ELECTRON_RUN_AS_NODE"] = "";

        try
        {
            using var processo = new Process { StartInfo = info };
            if (!processo.Start())
                return new RespostaDoClaude(false, "", "O Claude Code não iniciou.", null, inicio.Elapsed);

            etapa?.Report("Perguntando ao Claude...");

            var saida = processo.StandardOutput.ReadToEndAsync(ct);
            var erro = processo.StandardError.ReadToEndAsync(ct);

            await processo.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            processo.StandardInput.Close();

            await processo.WaitForExitAsync(ct).ConfigureAwait(false);
            var texto = await saida.ConfigureAwait(false);
            var textoErro = await erro.ConfigureAwait(false);

            if (processo.ExitCode != 0)
                return new RespostaDoClaude(false, "", Resumir(textoErro, texto, processo.ExitCode), null, inicio.Elapsed);

            return Interpretar(texto, inicio.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new RespostaDoClaude(false, "", "Cancelado.", null, inicio.Elapsed);
        }
        catch (Exception ex)
        {
            return new RespostaDoClaude(false, "", ex.Message, null, inicio.Elapsed);
        }
    }

    private static RespostaDoClaude Interpretar(string saida, TimeSpan duracao)
    {
        try
        {
            using var doc = JsonDocument.Parse(saida);
            var raiz = doc.RootElement;

            var resultado = raiz.TryGetProperty("result", out var r) ? r.GetString() : null;
            var custo = raiz.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var v) ? v : (double?)null;
            var deuErro = raiz.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;

            if (deuErro || string.IsNullOrWhiteSpace(resultado))
                return new RespostaDoClaude(false, "", resultado ?? "O Claude respondeu vazio.", custo, duracao);

            return new RespostaDoClaude(true, resultado!.Trim(), null, custo, duracao);
        }
        catch (JsonException)
        {
            // saída fora do JSON esperado: devolve o texto cru, que ainda é melhor do que nada
            var limpo = saida.Trim();
            return limpo.Length > 0
                ? new RespostaDoClaude(true, limpo, null, null, duracao)
                : new RespostaDoClaude(false, "", "O Claude não respondeu nada.", null, duracao);
        }
    }

    private static string Resumir(string erro, string saida, int codigo)
    {
        var texto = string.IsNullOrWhiteSpace(erro) ? saida : erro;
        texto = texto.Trim();
        if (texto.Length == 0) return $"O Claude Code terminou com código {codigo}.";
        return texto.Length > 500 ? texto[..500] + "…" : texto;
    }
}
