using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gravador.Core.Claude.Mcp;

/// <summary>O que uma ferramenta devolve: texto, imagem, ou os dois.</summary>
public sealed class ConteudoMcp
{
    private readonly JsonArray _blocos = new();
    public bool Erro { get; private set; }

    public static ConteudoMcp Texto(string texto) => new ConteudoMcp().ComTexto(texto);

    public static ConteudoMcp Falha(string mensagem) => new ConteudoMcp { Erro = true }.ComTexto(mensagem);

    public ConteudoMcp ComTexto(string texto)
    {
        _blocos.Add(new JsonObject { ["type"] = "text", ["text"] = texto });
        return this;
    }

    public ConteudoMcp ComImagem(byte[] bytes, string mimeType)
    {
        _blocos.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = mimeType });
        return this;
    }

    internal JsonObject ParaJson() => new() { ["content"] = _blocos, ["isError"] = Erro };
}

/// <summary>Uma ferramenta exposta pelo servidor: nome, descrição, esquema de entrada e o que ela faz.</summary>
public sealed record FerramentaMcp(
    string Nome,
    string Descricao,
    JsonObject Esquema,
    Func<JsonElement, CancellationToken, Task<ConteudoMcp>> Executar);

/// <summary>
/// Um servidor MCP mínimo, por stdio, em JSON-RPC 2.0 — o suficiente para o `claude` chamar as
/// ferramentas do Gravador.
///
/// Por que existe: quando o Claude é chamado com as ferramentas nativas dele desligadas
/// (<c>--tools ""</c>), o system prompt encolhe milhares de tokens — mas ele fica sem conseguir ler
/// nada. Este servidor devolve a ele exatamente o que a tarefa precisa: a transcrição por trecho,
/// os slides um a um, a linha do tempo, e a possibilidade de anotar capítulos. Nem Bash, nem
/// escrita de arquivo, nem acesso fora da pasta da sessão. As ferramentas são NOSSAS, descritas em
/// português e escopadas; o Claude Code entra só como executor.
///
/// O protocolo cabe em quatro mensagens: <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c> e
/// <c>ping</c>. O stdout é o canal do protocolo — qualquer log vai para o stderr, senão uma linha de
/// aviso no meio do JSON derruba a conexão.
/// </summary>
public sealed class ServidorMcp
{
    private readonly IReadOnlyList<FerramentaMcp> _ferramentas;
    private readonly string _nome;
    private readonly string _instrucoes;
    private readonly string? _marcadorDePronto;
    private readonly object _travaDeEscrita = new();
    private StreamWriter _saida = null!;

    /// <param name="marcadorDePronto">
    /// Arquivo a criar quando o cliente já pediu a lista de ferramentas — o sinal de "pode perguntar".
    ///
    /// Existe porque o `claude -p` sobe os servidores de <c>--mcp-config</c> sem esperar por eles
    /// ("running fully async (nonblocking)", diz o log dele): se a pergunta entrar antes da conexão,
    /// o modelo responde sem ferramenta nenhuma. Quem nos chama espera este arquivo aparecer antes de
    /// mandar a pergunta. O servidor é o único que sabe quando o aperto de mão terminou.
    /// </param>
    public ServidorMcp(string nome, string instrucoes, IReadOnlyList<FerramentaMcp> ferramentas, string? marcadorDePronto = null)
    {
        _nome = nome;
        _instrucoes = instrucoes;
        _ferramentas = ferramentas;
        _marcadorDePronto = marcadorDePronto;
    }

    public async Task<int> RodarAsync(CancellationToken ct = default)
    {
        _saida = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var entrada = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        string? linha;
        while (!ct.IsCancellationRequested && (linha = await entrada.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;
            JsonNode? mensagem;
            try
            {
                mensagem = JsonNode.Parse(linha);
            }
            catch (JsonException ex)
            {
                Responder(null, erro: (-32700, "JSON inválido: " + ex.Message));
                continue;
            }
            if (mensagem is not JsonObject obj) continue;

            var id = obj["id"];
            var metodo = obj["method"]?.GetValue<string>() ?? "";
            var parametros = obj["params"];

            // notificações não têm id e não se respondem
            if (id == null)
            {
                Log($"notificação: {metodo}");
                continue;
            }

            try
            {
                var resultado = await Tratar(metodo, parametros, ct).ConfigureAwait(false);
                if (resultado != null) Responder(id, resultado);
                else Responder(id, erro: (-32601, $"Método desconhecido: {metodo}"));
            }
            catch (Exception ex)
            {
                Responder(id, erro: (-32603, ex.Message));
            }
        }
        return 0;
    }

    private async Task<JsonObject?> Tratar(string metodo, JsonNode? parametros, CancellationToken ct)
    {
        switch (metodo)
        {
            case "initialize":
            {
                // devolve a versão que o cliente pediu: o subconjunto usado aqui é igual em todas
                var versao = parametros?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05";
                return new JsonObject
                {
                    ["protocolVersion"] = versao,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = _nome, ["version"] = AppInfo.Versao },
                    ["instructions"] = _instrucoes,
                };
            }

            case "ping":
                return new JsonObject();

            case "tools/list":
            {
                var lista = new JsonArray();
                foreach (var f in _ferramentas)
                    lista.Add(new JsonObject
                    {
                        ["name"] = f.Nome,
                        ["description"] = f.Descricao,
                        ["inputSchema"] = f.Esquema.DeepClone(),
                    });
                SinalizarPronto();
                return new JsonObject { ["tools"] = lista };
            }

            case "tools/call":
            {
                var nome = parametros?["name"]?.GetValue<string>() ?? "";
                var ferramenta = _ferramentas.FirstOrDefault(f => f.Nome == nome);
                if (ferramenta == null)
                    return ConteudoMcp.Falha($"Ferramenta desconhecida: {nome}").ParaJson();

                var argumentos = parametros?["arguments"] is JsonNode n
                    ? JsonDocument.Parse(n.ToJsonString()).RootElement
                    : JsonDocument.Parse("{}").RootElement;

                Log($"chamada: {nome} {argumentos.GetRawText()}");
                try
                {
                    var conteudo = await ferramenta.Executar(argumentos, ct).ConfigureAwait(false);
                    return conteudo.ParaJson();
                }
                catch (Exception ex)
                {
                    return ConteudoMcp.Falha($"{nome} falhou: {ex.Message}").ParaJson();
                }
            }

            // aparecem em alguns clientes; responder vazio é o correto para quem não os oferece
            case "resources/list":
                return new JsonObject { ["resources"] = new JsonArray() };
            case "prompts/list":
                return new JsonObject { ["prompts"] = new JsonArray() };

            default:
                return null;
        }
    }

    private void Responder(JsonNode? id, JsonObject? resultado = null, (int Codigo, string Mensagem)? erro = null)
    {
        var resposta = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() };
        if (erro is { } e)
            resposta["error"] = new JsonObject { ["code"] = e.Codigo, ["message"] = e.Mensagem };
        else
            resposta["result"] = resultado;

        var json = resposta.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        lock (_travaDeEscrita) _saida.WriteLine(json);
    }

    private void SinalizarPronto()
    {
        if (_marcadorDePronto == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_marcadorDePronto)!);
            File.WriteAllText(_marcadorDePronto, DateTimeOffset.UtcNow.ToString("o"));
        }
        catch
        {
            // sem marcador, quem chama cai no tempo máximo de espera
        }
    }

    private static void Log(string texto)
    {
        if (Environment.GetEnvironmentVariable("GRAVADOR_MCP_LOG") == "1")
            Console.Error.WriteLine($"[mcp] {texto}");
    }

    // ------------------------------------------------------------------

    /// <summary>Atalhos para montar os esquemas de entrada sem escrever JSON à mão.</summary>
    public static JsonObject Esquema(params (string Nome, string Tipo, string Descricao, bool Obrigatorio)[] campos)
    {
        var props = new JsonObject();
        var obrigatorios = new JsonArray();
        foreach (var (nome, tipo, descricao, obrigatorio) in campos)
        {
            props[nome] = new JsonObject { ["type"] = tipo, ["description"] = descricao };
            if (obrigatorio) obrigatorios.Add(nome);
        }
        var esquema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (obrigatorios.Count > 0) esquema["required"] = obrigatorios;
        return esquema;
    }

    public static string? Str(JsonElement args, string nome) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? Num(JsonElement args, string nome)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(nome, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String) return LerTempo(v.GetString());
        return null;
    }

    /// <summary>Aceita segundos ("754"), "mm:ss" e "h:mm:ss" — o Claude escreve como leu na linha do tempo.</summary>
    public static double? LerTempo(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        texto = texto.Trim();
        if (double.TryParse(texto, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        var partes = texto.Split(':');
        if (partes.Length is 2 or 3 && partes.All(p => int.TryParse(p, out _)))
        {
            var v = partes.Select(int.Parse).ToArray();
            return partes.Length == 2 ? v[0] * 60 + v[1] : v[0] * 3600 + v[1] * 60 + v[2];
        }
        return null;
    }
}
