using System.Text;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Core.Claude;

/// <summary>
/// Traduz a transcrição com o Claude, preservando os carimbos de tempo.
///
/// Por que o Claude e não o serviço de transcrição: o endpoint de tradução do Whisper só traduz
/// PARA o inglês. Quem quer a apresentação em português precisa de um tradutor de verdade, e o
/// Claude é excelente nisso — e já está conectado.
///
/// O original nunca é sobrescrito. A tradução é um artefato derivado, em arquivo próprio: você tem
/// que poder conferir o que foi dito de fato.
///
/// Vai em pedaços de ~6 mil caracteres, com as linhas carimbadas. Pedir "a mesma quantidade de
/// linhas, com os mesmos carimbos" é o que mantém a tradução alinhada ao áudio; sem isso o modelo
/// junta frases e o carimbo se perde. Cada pedaço é uma chamada SEM ferramenta e SEM system prompt
/// do Claude Code — só a instrução de traduzir. É a chamada mais barata que a ferramenta faz.
/// </summary>
public static class Tradutor
{
    private const int TamanhoDoPedaco = 6000;

    private const string Instrucao = """
        Você é um tradutor profissional. Traduza as linhas abaixo para {DESTINO}.

        Regras absolutas:
        - Cada linha começa com um carimbo entre crases, como `12:34`. Devolva EXATAMENTE as mesmas
          linhas, na mesma ordem, cada uma com o MESMO carimbo, seguido da tradução.
        - Não junte linhas, não divida linhas, não pule linhas, não acrescente comentários.
        - Mantenha nomes próprios, siglas, nomes de produtos e termos técnicos consagrados no original.
        - Traduza de forma natural, como alguém falaria — é fala transcrita, não texto escrito.
        - Responda só com as linhas traduzidas, nada antes nem depois.
        """;

    public static async Task<RespostaDoClaude> TraduzirAsync(SessaoGravacao sessao, AppSettings config,
        string? tokenOAuth, IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        var falas = sessao.Falas.OrderBy(f => f.DeSegundos).ToList();
        if (falas.Count == 0)
            return new RespostaDoClaude(false, "", "Não há transcrição para traduzir.", null, TimeSpan.Zero);

        var destino = NomeDoIdioma(config.IdiomaDestino);
        var pedacos = Fatiar(falas);
        var traduzidas = new List<string>(falas.Count);
        var inicio = DateTime.UtcNow;
        double custo = 0;
        int entrada = 0, saida = 0;

        for (var i = 0; i < pedacos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            etapa?.Report($"Traduzindo para {destino}... parte {i + 1} de {pedacos.Count}");

            var corpo = string.Join("\n", pedacos[i].Select(f => $"`{Formato.Carimbo(f.DeSegundos)}` {f.Texto}"));
            var opcoes = new OpcoesDoClaude
            {
                Modelo = config.ModeloClaude,
                SystemPrompt = Instrucao.Replace("{DESTINO}", destino),
                TokenOAuth = tokenOAuth,
                PersistirSessao = false,
            };
            var resposta = await ClaudeCli.PerguntarAsync(corpo, opcoes, null, ct).ConfigureAwait(false);
            if (!resposta.Ok)
                return resposta with { Erro = $"A tradução parou na parte {i + 1} de {pedacos.Count}: {resposta.Erro}" };

            custo += resposta.CustoUsd ?? 0;
            entrada += resposta.TokensEntrada ?? 0;
            saida += resposta.TokensSaida ?? 0;
            traduzidas.AddRange(Alinhar(pedacos[i], resposta.Texto));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Tradução ({destino}) — {sessao.Titulo}");
        sb.AppendLine();
        sb.AppendLine($"*{sessao.Inicio.LocalDateTime:dd/MM/yyyy HH:mm} · {Formato.Duracao(sessao.Duracao)} · "
            + $"traduzido do {NomeDoIdioma(sessao.Idioma)} pelo Claude ({config.ModeloClaude}). O original está em transcricao.md.*");
        sb.AppendLine();
        foreach (var linha in traduzidas) sb.AppendLine(linha);

        await File.WriteAllTextAsync(sessao.CaminhoTraducao, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
        sessao.IdiomaDaTraducao = config.IdiomaDestino;
        sessao.Salvar();

        return new RespostaDoClaude(true, sb.ToString(), null, custo, DateTime.UtcNow - inicio, null, entrada, saida);
    }

    private static List<List<TrechoFalado>> Fatiar(List<TrechoFalado> falas)
    {
        var pedacos = new List<List<TrechoFalado>>();
        var atual = new List<TrechoFalado>();
        var tamanho = 0;
        foreach (var f in falas)
        {
            if (tamanho + f.Texto.Length > TamanhoDoPedaco && atual.Count > 0)
            {
                pedacos.Add(atual);
                atual = new List<TrechoFalado>();
                tamanho = 0;
            }
            atual.Add(f);
            tamanho += f.Texto.Length + 12;
        }
        if (atual.Count > 0) pedacos.Add(atual);
        return pedacos;
    }

    /// <summary>
    /// Casa as linhas devolvidas com as originais pelo carimbo. Quando o modelo desobedece e devolve
    /// uma contagem diferente, o que veio ainda é aproveitado: as linhas sem par recebem o texto
    /// original, para a tradução não ter buracos silenciosos.
    /// </summary>
    private static IEnumerable<string> Alinhar(List<TrechoFalado> originais, string resposta)
    {
        var porCarimbo = new Dictionary<string, string>();
        foreach (var linhaBruta in resposta.Split('\n'))
        {
            var linha = linhaBruta.Trim();
            if (linha.Length < 3 || linha[0] != '`') continue;
            var fim = linha.IndexOf('`', 1);
            if (fim < 0) continue;
            var carimbo = linha[1..fim];
            var texto = linha[(fim + 1)..].Trim();
            if (texto.Length > 0 && !porCarimbo.ContainsKey(carimbo)) porCarimbo[carimbo] = texto;
        }

        foreach (var f in originais)
        {
            var carimbo = Formato.Carimbo(f.DeSegundos);
            yield return porCarimbo.TryGetValue(carimbo, out var traduzido)
                ? $"`{carimbo}` {traduzido}"
                : $"`{carimbo}` {f.Texto}";
        }
    }

    public static string NomeDoIdioma(string codigo)
    {
        var curto = Transcription.Transcritores.CodigoCurto(codigo);
        return curto switch
        {
            "pt" => codigo.Contains("PT", StringComparison.OrdinalIgnoreCase) ? "português de Portugal" : "português do Brasil",
            "en" => "inglês",
            "es" => "espanhol",
            "fr" => "francês",
            "de" => "alemão",
            "it" => "italiano",
            "ja" => "japonês",
            "zh" => "chinês",
            "auto" or "" => "idioma original",
            _ => codigo,
        };
    }
}
