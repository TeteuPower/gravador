using System.Text;
using System.Text.Json;
using Gravador.Core.Claude.Mcp;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Core.Claude;

/// <summary>Uma mensagem da conversa, para a interface mostrar o histórico.</summary>
public sealed record MensagemDaConversa(string Papel, string Texto, DateTimeOffset Quando);

/// <summary>
/// A conversa com o Claude sobre UMA sessão, que sobrevive ao fechar da janela.
///
/// Cada gravação tem a sua: o id de sessão do `claude` fica no <c>sessao.json</c> e a chamada
/// seguinte usa <c>--resume</c>, então você reabre a apresentação semana que vem e continua de onde
/// parou — com o Claude lembrando o que já leu.
///
/// O que vai em cada mensagem é só a pergunta. O contexto ele PUXA pelas ferramentas do servidor
/// MCP: a linha do tempo, o trecho da transcrição que interessa, o slide pelo id. Numa apresentação
/// de 48 minutos, é a diferença entre caber e não caber — e é o que faz a pergunta custar o que a
/// pergunta vale, não o que a reunião inteira vale.
///
/// O registro da conversa vai para <c>conversa.md</c>, na pasta da sessão: faz parte do pacote, como
/// a transcrição e o resumo.
/// </summary>
public sealed class Conversa
{
    private const string SystemPrompt = """
        Você é o assistente de uma pessoa que gravou (ou importou) uma reunião ou apresentação com a
        ferramenta Gravador. Você conversa sobre ESSA sessão.

        Você não tem o áudio nem a tela: tem ferramentas. Use-as em vez de supor:
        - linha_do_tempo: comece por aqui na primeira pergunta — mostra o que existe.
        - transcricao(de, ate, versao): a fala entre dois instantes. Peça por intervalo, não inteira.
        - buscar(texto): acha onde um assunto foi falado, com carimbo de tempo.
        - quadros / ver_quadro(id): os slides e capturas. Veja um slide quando a pergunta for sobre ele.
        - definir_capitulo(em, titulo) e marcar_trecho(de, ate, tipo): só quando a pessoa pedir.

        Responda em português do Brasil, direto ao ponto, citando carimbos de tempo (mm:ss) quando
        apontar um momento. Se a transcrição estiver em outro idioma, traduza o que citar. Não invente
        o que não está no registro; se não achou, diga que não achou.
        """;

    private readonly SessaoGravacao _sessao;
    private readonly AppSettings _config;
    private readonly List<MensagemDaConversa> _historico;

    public Conversa(SessaoGravacao sessao, AppSettings config)
    {
        _sessao = sessao;
        _config = config;
        _historico = CarregarHistorico();
    }

    public IReadOnlyList<MensagemDaConversa> Historico => _historico;
    public bool Ocupada { get; private set; }

    private string CaminhoJson => Path.Combine(_sessao.Pasta, "conversa.json");

    public async Task<RespostaDoClaude> PerguntarAsync(string pergunta, Action<string>? aoReceberTexto = null,
        Action<string>? aoChamarFerramenta = null, IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        if (Ocupada) return new RespostaDoClaude(false, "", "Ainda estou respondendo a anterior.", null, TimeSpan.Zero);
        pergunta = pergunta.Trim();
        if (pergunta.Length == 0) return new RespostaDoClaude(false, "", "Pergunta vazia.", null, TimeSpan.Zero);

        var conta = ContaClaude.Estado(_config);
        if (!conta.Conectado) return new RespostaDoClaude(false, "", conta.Descricao, null, TimeSpan.Zero);
        if (conta.Meio == MeioDeAcesso.ChaveDeApi)
            return new RespostaDoClaude(false, "", "A conversa precisa do Claude Code instalado (ela usa as ferramentas do Gravador por MCP).", null, TimeSpan.Zero);

        var mcp = ClaudeCli.McpConfigJson(_sessao.Pasta);
        if (mcp == null) return new RespostaDoClaude(false, "", "Não achei o gravador-cli.exe ao lado do aplicativo.", null, TimeSpan.Zero);

        Ocupada = true;
        try
        {
            var opcoes = new OpcoesDoClaude
            {
                Modelo = _config.ModeloClaude,
                SystemPrompt = SystemPrompt,
                McpConfigJson = mcp,
                PastaDeTrabalho = _sessao.Pasta,
                TokenOAuth = PosProcessamento.TokenDe(conta),
                PersistirSessao = true,
                AoReceberTexto = aoReceberTexto,
                AoChamarFerramenta = aoChamarFerramenta,
            };
            foreach (var f in ClaudeCli.FerramentasMcpPermitidas(FerramentasDaSessao.Para(_sessao).Select(t => t.Nome)))
                opcoes.FerramentasPermitidas.Add(f);

            string prompt;
            if (_sessao.ConversaId is { } existente)
            {
                opcoes.RetomarSessao = existente;
                prompt = pergunta;
            }
            else
            {
                opcoes.SessionId = Guid.NewGuid().ToString();
                // Na primeira mensagem, o mínimo de contexto para ele saber do que se trata; o resto
                // ele pega em linha_do_tempo.
                prompt = $"Sessão: \"{_sessao.Titulo}\" ({Formato.Duracao(_sessao.Duracao)}"
                       + (string.IsNullOrEmpty(_sessao.Idioma) ? "" : $", fala em {Tradutor.NomeDoIdioma(_sessao.Idioma)}")
                       + $"). Pergunta: {pergunta}";
            }

            Registrar("você", pergunta);
            var resposta = await ClaudeCli.PerguntarAsync(prompt, opcoes, etapa, ct).ConfigureAwait(false);

            if (resposta.Ok)
            {
                var id = resposta.SessionId ?? opcoes.SessionId ?? opcoes.RetomarSessao;
                if (id != null && id != _sessao.ConversaId)
                {
                    _sessao.ConversaId = id;
                    _sessao.Salvar();
                }
                Registrar("claude", resposta.Texto);
            }
            else if (_sessao.ConversaId != null && (resposta.Erro ?? "").Contains("session", StringComparison.OrdinalIgnoreCase))
            {
                // A sessão do `claude` sumiu (limpeza, outra máquina): recomeça na próxima pergunta em
                // vez de ficar preso num id morto.
                _sessao.ConversaId = null;
                _sessao.Salvar();
            }
            return resposta;
        }
        finally
        {
            Ocupada = false;
        }
    }

    /// <summary>Esquece a conversa: próxima pergunta começa do zero. O registro em Markdown fica.</summary>
    public void Reiniciar()
    {
        _sessao.ConversaId = null;
        _sessao.Salvar();
        _historico.Clear();
        try { File.Delete(CaminhoJson); } catch { /* nada */ }
    }

    // ------------------------------------------------------------------

    private void Registrar(string papel, string texto)
    {
        var m = new MensagemDaConversa(papel, texto, DateTimeOffset.Now);
        _historico.Add(m);
        try
        {
            File.WriteAllText(CaminhoJson, JsonSerializer.Serialize(_historico, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

            var sb = new StringBuilder();
            sb.AppendLine($"# Conversa — {_sessao.Titulo}");
            sb.AppendLine();
            foreach (var h in _historico)
            {
                sb.AppendLine($"## {(h.Papel == "claude" ? "Claude" : "Você")} · {h.Quando.LocalDateTime:dd/MM HH:mm}");
                sb.AppendLine();
                sb.AppendLine(h.Texto.Trim());
                sb.AppendLine();
            }
            File.WriteAllText(_sessao.CaminhoConversa, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // registro é conveniência; a resposta já está na tela
        }
    }

    private List<MensagemDaConversa> CarregarHistorico()
    {
        try
        {
            if (!File.Exists(CaminhoJson)) return new List<MensagemDaConversa>();
            return JsonSerializer.Deserialize<List<MensagemDaConversa>>(File.ReadAllText(CaminhoJson)) ?? new List<MensagemDaConversa>();
        }
        catch
        {
            return new List<MensagemDaConversa>();
        }
    }
}
