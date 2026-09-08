using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Core.Claude;

/// <summary>
/// Manda a sessão para o Claude e traz o resumo.
///
/// Este é o papel do Claude nesta ferramenta, e vale dizer por que não é outro: os modelos do Claude
/// leem texto, imagem e PDF — não leem áudio. Transcrever com ele não é possível, e prometer isso na
/// interface seria mentira. Ler a transcrição junto com as capturas de tela e devolver decisões,
/// pendências e o fio da conversa é exatamente o que ele faz melhor do que um transcritor.
///
/// Sem transcrição, o pedido muda de forma em vez de deixar de existir: o Claude analisa as imagens
/// e a linha do tempo, e o resumo diz claramente que foi feito só com isso.
/// </summary>
public sealed class Analista
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public const string PromptPadrao = """
        Você está analisando o registro de uma reunião ou apresentação gravada pelo Gravador.

        Escreva em português do Brasil, em Markdown, com estas seções (pule as que não tiverem
        conteúdo real — não invente para preencher):

        ## Resumo
        Três a seis linhas sobre o que foi tratado.

        ## Decisões
        O que ficou decidido, uma por item.

        ## Pendências
        O que ficou de ser feito, com o responsável quando ele aparecer no registro.

        ## Pontos de atenção
        Divergências, riscos e o que ficou em aberto.

        ## Linha do tempo
        Os momentos que valem voltar a ouvir, com o carimbo de tempo do áudio.

        Regras:
        - Cite o carimbo de tempo (mm:ss) sempre que apontar um momento específico.
        - Não invente nome, número ou combinação que não esteja no registro.
        - Se a transcrição estiver ruim ou faltando pedaços, diga isso em vez de preencher a lacuna.
        - Trechos marcados como microfone mudo NÃO foram ouvidos pelos outros participantes; se algo
          dito ali for relevante, aponte que ficou só na gravação.
        """;

    /// <summary>Gera o resumo e o grava em <c>resumo.md</c> dentro da pasta da sessão.</summary>
    public async Task<RespostaDoClaude> ResumirAsync(SessaoGravacao sessao, AppSettings config,
        IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        var conta = ContaClaude.Estado(config);
        if (!conta.Conectado)
            return new RespostaDoClaude(false, "", conta.Descricao, null, TimeSpan.Zero);

        etapa?.Report("Montando o pacote da reunião...");
        var prompt = MontarPrompt(sessao, config, incluirCaminhosDeImagem: conta.Meio != MeioDeAcesso.ChaveDeApi);

        var resposta = conta.Meio == MeioDeAcesso.ChaveDeApi
            ? await PelaApiAsync(prompt, sessao, config, etapa, ct).ConfigureAwait(false)
            : await ClaudeCli.PerguntarAsync(prompt, sessao.Pasta, config.ModeloClaude,
                conta.Meio == MeioDeAcesso.LoginNoGravador ? ClaudeLogin.Carregar()?.Token : null,
                etapa, ct).ConfigureAwait(false);

        if (resposta.Ok)
        {
            try
            {
                var cabecalho = $"# {sessao.Titulo}\n\n"
                    + $"*{sessao.Inicio.LocalDateTime:dd/MM/yyyy HH:mm} · {Formato.Duracao(sessao.Duracao)} · "
                    + $"resumo gerado pelo Claude ({config.ModeloClaude})*\n\n";
                File.WriteAllText(sessao.CaminhoResumo, cabecalho + resposta.Texto + "\n", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return resposta with { Erro = "O resumo veio, mas não deu para gravar o arquivo: " + ex.Message };
            }
        }
        return resposta;
    }

    // ==================================================================

    /// <summary>
    /// Monta o texto que vai para o Claude.
    ///
    /// A transcrição entra inteira quando cabe. Passando de 120 mil caracteres — algo como quatro
    /// horas de reunião — ela vira uma instrução para o Claude ABRIR o arquivo: assim ele lê em
    /// pedaços, do jeito dele, em vez de a chamada estourar de uma vez.
    /// </summary>
    public static string MontarPrompt(SessaoGravacao sessao, AppSettings config, bool incluirCaminhosDeImagem)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine(string.IsNullOrWhiteSpace(config.PromptResumo) ? PromptPadrao : config.PromptResumo);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# Sessão: {sessao.Titulo}");
        sb.AppendLine($"- Início: {sessao.Inicio.LocalDateTime:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"- Duração: {Formato.Duracao(sessao.Duracao)}");

        var mudos = sessao.TrechosMudos;
        if (mudos.Count > 0)
        {
            var total = TimeSpan.FromSeconds(mudos.Sum(m => m.Duracao.TotalSeconds));
            sb.AppendLine($"- Seu microfone esteve mudo por {Formato.Duracao(total)} no total, em {mudos.Count} trecho(s):");
            foreach (var m in mudos.OrderBy(m => m.DeSegundos))
            {
                var confianca = m.Confirmado ? "confirmado pelo Windows" : "detecção provável";
                sb.AppendLine($"  - de {Formato.Carimbo(m.DeSegundos)} a {Formato.Carimbo(m.AteSegundos)} ({m.Origem}, {confianca})");
            }
        }

        var marcadores = sessao.Marcas.Where(m => m.Tipo == TipoDeMarca.Marcador).ToList();
        if (marcadores.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Momentos marcados por quem gravou");
            foreach (var m in marcadores)
                sb.AppendLine($"- {m.Carimbo} — {(string.IsNullOrWhiteSpace(m.Texto) ? "marcado" : m.Texto)}");
        }

        var capturas = sessao.Marcas.Where(m => m.Tipo == TipoDeMarca.Captura && m.Arquivo != null).ToList();
        if (capturas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Capturas de tela ({capturas.Count})");
            foreach (var c in capturas)
            {
                var janela = string.IsNullOrWhiteSpace(c.Texto) ? "" : $" — janela: {c.Texto}";
                sb.AppendLine($"- {c.Carimbo} · `{c.Arquivo}`{janela}");
            }
            if (incluirCaminhosDeImagem)
            {
                sb.AppendLine();
                sb.AppendLine("Abra essas imagens com a ferramenta Read (os caminhos são relativos a esta pasta) "
                    + "e use o que estiver nelas — slides, planilhas, telas de sistema — junto com o texto.");
            }
        }

        var falas = sessao.Falas;
        if (falas.Count > 0)
        {
            var texto = TranscricaoEmTexto(sessao);
            sb.AppendLine();
            if (texto.Length <= 120_000)
            {
                sb.AppendLine("## Transcrição");
                sb.AppendLine();
                sb.AppendLine(texto);
            }
            else
            {
                sb.AppendLine("## Transcrição");
                sb.AppendLine();
                sb.AppendLine($"A transcrição tem {texto.Length:N0} caracteres e está em `transcricao.md`, "
                    + "nesta mesma pasta. Leia o arquivo antes de responder.");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("## Sem transcrição");
            sb.AppendLine();
            sb.AppendLine("Esta sessão não tem transcrição — o áudio está gravado, mas nenhum motor de "
                + "transcrição rodou. Você NÃO tem acesso ao áudio. Faça a análise possível com as capturas "
                + "de tela e a linha do tempo acima, e diga logo no começo do resumo que ela foi feita sem "
                + "o conteúdo falado.");
        }

        return sb.ToString();
    }

    /// <summary>Grava (e devolve) a transcrição em Markdown, com carimbo de tempo por trecho.</summary>
    public static string TranscricaoEmTexto(SessaoGravacao sessao)
    {
        var sb = new StringBuilder();
        string? fonteAnterior = null;
        foreach (var f in sessao.Falas.OrderBy(f => f.DeSegundos))
        {
            if (f.Fonte != fonteAnterior)
            {
                sb.AppendLine();
                sb.AppendLine(f.Fonte == "microfone" ? "**[você]**" : "**[reunião]**");
                fonteAnterior = f.Fonte;
            }
            sb.AppendLine($"`{Formato.Carimbo(f.DeSegundos)}` {f.Texto}");
        }
        return sb.ToString().Trim();
    }

    public static void GravarTranscricao(SessaoGravacao sessao)
    {
        try
        {
            if (sessao.Falas.Count == 0) return;
            var cabecalho = $"# Transcrição — {sessao.Titulo}\n\n"
                + $"*{sessao.Inicio.LocalDateTime:dd/MM/yyyy HH:mm} · {Formato.Duracao(sessao.Duracao)}*\n\n"
                + "`[você]` é o seu microfone; `[reunião]` é o áudio que saiu pelo computador.\n\n";
            File.WriteAllText(sessao.CaminhoTranscricao, cabecalho + TranscricaoEmTexto(sessao) + "\n", Encoding.UTF8);
        }
        catch
        {
            // a transcrição continua no sessao.json
        }
    }

    // ==================================================================

    /// <summary>
    /// Caminho da chave de API: monta a mensagem com as imagens embutidas.
    ///
    /// Existe para quem não tem o Claude Code instalado. As capturas vão em base64 e são limitadas a
    /// vinte: além disso a chamada fica cara e lenta sem ganhar nada — vinte telas já contam a
    /// história de qualquer reunião.
    /// </summary>
    private static async Task<RespostaDoClaude> PelaApiAsync(string prompt, SessaoGravacao sessao,
        AppSettings config, IProgress<string>? etapa, CancellationToken ct)
    {
        var inicio = DateTimeOffset.UtcNow;
        var chave = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(chave))
            return new RespostaDoClaude(false, "", "ANTHROPIC_API_KEY não está definida.", null, TimeSpan.Zero);

        var conteudo = new List<object>();
        if (config.EnviarCapturasNoResumo)
        {
            etapa?.Report("Anexando as capturas de tela...");
            foreach (var m in sessao.Marcas.Where(m => m.Tipo == TipoDeMarca.Captura && m.Arquivo != null).Take(20))
            {
                var caminho = Path.Combine(sessao.Pasta, m.Arquivo!);
                if (!File.Exists(caminho)) continue;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(caminho, ct).ConfigureAwait(false);
                    conteudo.Add(new
                    {
                        type = "image",
                        source = new
                        {
                            type = "base64",
                            media_type = caminho.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg",
                            data = Convert.ToBase64String(bytes),
                        },
                    });
                    conteudo.Add(new { type = "text", text = $"(imagem acima: {m.Carimbo} — {m.Arquivo})" });
                }
                catch
                {
                    // imagem ilegível: segue sem ela
                }
            }
        }
        conteudo.Add(new { type = "text", text = prompt });

        var corpo = JsonSerializer.Serialize(new
        {
            model = config.ModeloClaude,
            max_tokens = 8000,
            messages = new[] { new { role = "user", content = conteudo } },
        });

        try
        {
            etapa?.Report("Perguntando ao Claude...");
            using var pedido = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
            };
            pedido.Headers.TryAddWithoutValidation("x-api-key", chave);
            pedido.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            pedido.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resposta = await Http.SendAsync(pedido, ct).ConfigureAwait(false);
            var texto = await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var duracao = DateTimeOffset.UtcNow - inicio;

            if (!resposta.IsSuccessStatusCode)
                return new RespostaDoClaude(false, "", $"A API recusou (HTTP {(int)resposta.StatusCode}). "
                    + (texto.Length > 300 ? texto[..300] + "…" : texto), null, duracao);

            using var doc = JsonDocument.Parse(texto);
            var partes = doc.RootElement.GetProperty("content");
            var sb = new StringBuilder();
            foreach (var p in partes.EnumerateArray())
                if (p.TryGetProperty("type", out var t) && t.GetString() == "text")
                    sb.Append(p.GetProperty("text").GetString());

            var final = sb.ToString().Trim();
            return final.Length > 0
                ? new RespostaDoClaude(true, final, null, null, duracao)
                : new RespostaDoClaude(false, "", "O Claude respondeu vazio.", null, duracao);
        }
        catch (OperationCanceledException)
        {
            return new RespostaDoClaude(false, "", "Cancelado.", null, DateTimeOffset.UtcNow - inicio);
        }
        catch (Exception ex)
        {
            return new RespostaDoClaude(false, "", ex.Message, null, DateTimeOffset.UtcNow - inicio);
        }
    }

}
