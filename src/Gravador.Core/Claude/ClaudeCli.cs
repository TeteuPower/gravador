using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Gravador.Core.Claude;

/// <summary>O que o `claude` devolveu.</summary>
public sealed record RespostaDoClaude(
    bool Ok,
    string Texto,
    string? Erro,
    double? CustoUsd,
    TimeSpan Duracao,
    string? SessionId = null,
    int? TokensEntrada = null,
    int? TokensSaida = null,
    int? TokensCacheLidos = null,
    int? Turnos = null);

/// <summary>
/// Como chamar o `claude` para uma tarefa.
///
/// O padrão é o MÍNIMO: nenhuma ferramenta nativa, nenhuma configuração do usuário, system prompt
/// só o que a tarefa pede. Cada coisa que se liga aqui custa tokens em toda mensagem — a lista de
/// ferramentas nativas do Claude Code sozinha são milhares de tokens de descrição — e o objetivo
/// desta ferramenta é mandar pouco. Quem precisa de ferramenta liga o servidor MCP do Gravador
/// (<see cref="McpConfigJson"/>), que descreve exatamente as ferramentas necessárias, em português,
/// escopadas na pasta da sessão.
/// </summary>
public sealed class OpcoesDoClaude
{
    public string Modelo { get; set; } = "";

    /// <summary>Substitui o system prompt padrão do Claude Code. Null mantém o padrão (caro).</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Ligar as ferramentas embutidas do Claude Code (Read, Bash...). Padrão: desligadas.</summary>
    public bool FerramentasNativas { get; set; }

    /// <summary>JSON de <c>mcpServers</c> a carregar em modo estrito — só ele, nada do usuário.</summary>
    public string? McpConfigJson { get; set; }

    /// <summary>Ferramentas pré-aprovadas (ex.: "mcp__gravador__*"). Sem isto, o `-p` nega tudo.</summary>
    public List<string> FerramentasPermitidas { get; } = new();

    /// <summary>Continua uma conversa existente.</summary>
    public string? RetomarSessao { get; set; }

    /// <summary>Id fixo para a conversa nova, para poder retomá-la depois.</summary>
    public string? SessionId { get; set; }

    /// <summary>Guardar a sessão em disco. Tradução e resumo são descartáveis; a conversa não.</summary>
    public bool PersistirSessao { get; set; }

    public string? PastaDeTrabalho { get; set; }
    public string? TokenOAuth { get; set; }

    /// <summary>Recebe o texto conforme ele sai, para a interface mostrar a resposta se formando.</summary>
    public Action<string>? AoReceberTexto { get; set; }

    /// <summary>Recebe o nome de cada ferramenta que o Claude chama, para a interface mostrar o que ele está fazendo.</summary>
    public Action<string>? AoChamarFerramenta { get; set; }
}

/// <summary>
/// Executa o `claude` em modo não interativo e traz a resposta.
///
/// O prompt vai pelo STDIN, e não como argumento: o resumo de uma reunião com transcrição passa
/// tranquilamente dos 32 mil caracteres que a linha de comando do Windows aceita, e o erro que isso
/// dá não diz o que aconteceu.
///
/// A saída é sempre <c>stream-json</c>: é o único formato que entrega o texto conforme ele sai
/// (para a conversa parecer viva) E o registro final com custo, tokens e id de sessão. Com
/// <c>--output-format json</c> a interface ficaria trinta segundos olhando para nada.
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

    /// <summary>
    /// Onde está o <c>gravador-cli.exe</c> — é ele que o `claude` executa como servidor MCP.
    /// Ao lado do executável atual (instalado), o próprio processo (quando somos o CLI), ou a saída
    /// de build do projeto irmão (desenvolvimento).
    /// </summary>
    public static string? LocalizarGravadorCli()
    {
        var candidatos = new List<string> { Path.Combine(AppContext.BaseDirectory, "gravador-cli.exe") };
        if (Environment.ProcessPath is { } proprio && proprio.EndsWith("gravador-cli.exe", StringComparison.OrdinalIgnoreCase))
            candidatos.Insert(0, proprio);

        // desenvolvimento: sobe até achar a pasta src e desce no projeto do CLI
        var pasta = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && pasta != null; i++, pasta = pasta.Parent)
        {
            var src = Path.Combine(pasta.FullName, "src", "Gravador.Cli", "bin");
            if (!Directory.Exists(src)) continue;
            foreach (var cfg in new[] { "Release", "Debug" })
                candidatos.Add(Path.Combine(src, cfg, "net10.0-windows", "gravador-cli.exe"));
        }
        return candidatos.FirstOrDefault(File.Exists);
    }

    /// <summary>O JSON de <c>--mcp-config</c> que aponta o `claude` para o servidor MCP desta sessão.</summary>
    public static string? McpConfigJson(string pastaDaSessao)
    {
        var cli = LocalizarGravadorCli();
        if (cli == null) return null;
        var config = new
        {
            mcpServers = new
            {
                gravador = new { command = cli, args = new[] { "mcp", "--sessao", Path.GetFullPath(pastaDaSessao) } },
            },
        };
        return JsonSerializer.Serialize(config);
    }

    /// <summary>Nomes das ferramentas do servidor "gravador" no formato que o `--allowedTools` espera.</summary>
    public static IEnumerable<string> FerramentasMcpPermitidas(IEnumerable<string> nomes) =>
        nomes.Select(n => $"mcp__gravador__{n}");

    public static async Task<RespostaDoClaude> PerguntarAsync(string prompt, OpcoesDoClaude opcoes,
        IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        var exe = Localizar();
        if (exe == null)
            return new RespostaDoClaude(false, "", "O Claude Code não foi encontrado nesta máquina.", null, TimeSpan.Zero);

        var inicio = Stopwatch.StartNew();
        var info = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = opcoes.PastaDeTrabalho is { } p && Directory.Exists(p) ? p : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var a = info.ArgumentList;
        a.Add("-p");
        a.Add("--output-format"); a.Add("stream-json");
        a.Add("--verbose");
        a.Add("--include-partial-messages");

        if (!string.IsNullOrWhiteSpace(opcoes.Modelo)) { a.Add("--model"); a.Add(opcoes.Modelo); }

        // Nenhuma configuração do usuário: CLAUDE.md, hooks, servidores MCP dele. O que entra aqui é
        // só o que esta chamada declarou.
        a.Add("--setting-sources"); a.Add("");

        if (!opcoes.FerramentasNativas) { a.Add("--tools"); a.Add(""); }
        if (opcoes.SystemPrompt is { } sp) { a.Add("--system-prompt"); a.Add(sp); }

        if (opcoes.McpConfigJson is { } mcp)
        {
            a.Add("--strict-mcp-config");
            a.Add("--mcp-config"); a.Add(mcp);
        }
        if (opcoes.FerramentasPermitidas.Count > 0)
        {
            a.Add("--allowedTools");
            foreach (var f in opcoes.FerramentasPermitidas) a.Add(f);
        }

        if (opcoes.RetomarSessao is { } r) { a.Add("--resume"); a.Add(r); }
        else if (opcoes.SessionId is { } sid) { a.Add("--session-id"); a.Add(sid); }
        if (!opcoes.PersistirSessao && opcoes.RetomarSessao == null) a.Add("--no-session-persistence");

        if (!string.IsNullOrWhiteSpace(opcoes.TokenOAuth))
            info.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = opcoes.TokenOAuth;

        // O terminal do VS Code exporta esta variável, e com ela o launcher do Claude Code sobe como
        // Node puro e não faz nada. Mesma armadilha que o Limpador documenta no iniciar.cmd.
        info.Environment["ELECTRON_RUN_AS_NODE"] = "";
        // Sem verificação de atualização nem telemetria numa chamada de serviço.
        info.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        try
        {
            using var processo = new Process { StartInfo = info };
            if (!processo.Start())
                return new RespostaDoClaude(false, "", "O Claude Code não iniciou.", null, inicio.Elapsed);

            etapa?.Report("Perguntando ao Claude...");

            var erro = processo.StandardError.ReadToEndAsync(ct);

            await processo.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            processo.StandardInput.Close();

            var acumulado = new StringBuilder();
            RespostaDoClaude? final = null;
            string? linha;
            while ((linha = await processo.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (linha.Length == 0 || linha[0] != '{') continue;
                try
                {
                    final = Interpretar(linha, opcoes, acumulado, inicio.Elapsed) ?? final;
                }
                catch (JsonException)
                {
                    // linha que não é JSON inteiro (aviso do launcher): ignora
                }
            }

            await processo.WaitForExitAsync(ct).ConfigureAwait(false);
            var textoErro = await erro.ConfigureAwait(false);

            if (final != null) return final with { Duracao = inicio.Elapsed };

            if (processo.ExitCode != 0)
                return new RespostaDoClaude(false, "", Resumir(textoErro, acumulado.ToString(), processo.ExitCode), null, inicio.Elapsed);

            var texto = acumulado.ToString().Trim();
            return texto.Length > 0
                ? new RespostaDoClaude(true, texto, null, null, inicio.Elapsed)
                : new RespostaDoClaude(false, "", "O Claude não respondeu nada.", null, inicio.Elapsed);
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

    /// <summary>
    /// Uma linha do stream-json. Três tipos interessam: <c>stream_event</c> com delta de texto (a
    /// resposta saindo), <c>assistant</c> com <c>tool_use</c> (o que ele está fazendo) e <c>result</c>
    /// (o fecho, com custo e sessão).
    /// </summary>
    private static RespostaDoClaude? Interpretar(string linha, OpcoesDoClaude opcoes, StringBuilder acumulado, TimeSpan decorrido)
    {
        using var doc = JsonDocument.Parse(linha);
        var raiz = doc.RootElement;
        var tipo = raiz.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (tipo)
        {
            case "stream_event":
                if (raiz.TryGetProperty("event", out var ev)
                    && ev.TryGetProperty("type", out var et) && et.GetString() == "content_block_delta"
                    && ev.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                    && delta.TryGetProperty("text", out var tx) && tx.GetString() is { } pedaco)
                {
                    acumulado.Append(pedaco);
                    opcoes.AoReceberTexto?.Invoke(pedaco);
                }
                return null;

            case "assistant":
                if (opcoes.AoChamarFerramenta != null && raiz.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var conteudo) && conteudo.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bloco in conteudo.EnumerateArray())
                        if (bloco.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use"
                            && bloco.TryGetProperty("name", out var nome))
                            opcoes.AoChamarFerramenta(nome.GetString() ?? "");
                }
                return null;

            case "result":
            {
                var resultado = raiz.TryGetProperty("result", out var r) ? r.GetString() : null;
                var deuErro = raiz.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                var custo = raiz.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var v) ? v : (double?)null;
                var sessao = raiz.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                var turnos = raiz.TryGetProperty("num_turns", out var nt) && nt.TryGetInt32(out var ntv) ? ntv : (int?)null;
                int? entrada = null, saida = null, cache = null;
                if (raiz.TryGetProperty("usage", out var uso))
                {
                    entrada = Int(uso, "input_tokens");
                    saida = Int(uso, "output_tokens");
                    cache = Int(uso, "cache_read_input_tokens");
                }

                var texto = string.IsNullOrWhiteSpace(resultado) ? acumulado.ToString().Trim() : resultado!.Trim();
                if (deuErro)
                    return new RespostaDoClaude(false, "", texto.Length > 0 ? texto : "O Claude devolveu erro.", custo, decorrido, sessao, entrada, saida, cache, turnos);
                if (texto.Length == 0)
                    return new RespostaDoClaude(false, "", "O Claude respondeu vazio.", custo, decorrido, sessao, entrada, saida, cache, turnos);
                return new RespostaDoClaude(true, texto, null, custo, decorrido, sessao, entrada, saida, cache, turnos);
            }

            default:
                return null;
        }
    }

    private static int? Int(JsonElement el, string nome) =>
        el.TryGetProperty(nome, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static string Resumir(string erro, string saida, int codigo)
    {
        var texto = string.IsNullOrWhiteSpace(erro) ? saida : erro;
        texto = texto.Trim();
        if (texto.Length == 0) return $"O Claude Code terminou com código {codigo}.";
        return texto.Length > 500 ? texto[..500] + "…" : texto;
    }
}
