using Gravador.Core.Claude;
using Gravador.Core.Settings;
using Gravador.Core.Transcription;

namespace Gravador.Core.Session;

/// <summary>
/// O que acontece com uma sessão depois que o áudio existe: transcrever, traduzir, resumir.
///
/// É uma classe só porque são DOIS caminhos que chegam aqui — parar uma gravação e importar um
/// arquivo — e a mesma cauda para os dois. Se ela morasse no fim do PararAsync, a importação
/// precisaria de uma cópia, e as duas divergiriam no primeiro ajuste, como sempre acontece.
/// </summary>
public static class PosProcessamento
{
    public sealed class Opcoes
    {
        /// <summary>Motor de arquivo a usar quando a sessão ainda não tem transcrição. Null ou Nenhum: não transcreve.</summary>
        public MotorTranscricao? Motor { get; set; }

        /// <summary>Idioma da fala. "auto" deixa o motor detectar.</summary>
        public string Idioma { get; set; } = "auto";

        public bool Traduzir { get; set; } = true;
        public bool Resumir { get; set; }
    }

    public static async Task ExecutarAsync(SessaoGravacao sessao, AppSettings config, Opcoes opcoes,
        IProgress<string>? etapa, Action<string>? aviso, CancellationToken ct)
    {
        // ---- transcrição ----
        if (opcoes.Motor is { } motor && motor != MotorTranscricao.Nenhum && sessao.Falas.Count == 0)
        {
            var alvo = sessao.Arquivos.Mixado ?? sessao.Arquivos.Sistema ?? sessao.Arquivos.Microfone;
            if (alvo != null)
            {
                using var transcritor = Transcritores.DeArquivo(config, motor);
                if (transcritor == null)
                {
                    aviso?.Invoke("Motor de transcrição desconhecido.");
                }
                else if (!transcritor.Disponivel)
                {
                    aviso?.Invoke(transcritor.Motivo ?? "A transcrição não está configurada.");
                }
                else
                {
                    transcritor.IdiomaPedido = string.IsNullOrWhiteSpace(opcoes.Idioma) ? "auto" : opcoes.Idioma;
                    var caminho = Path.Combine(sessao.Pasta, alvo);
                    var fonte = alvo.StartsWith("microfone", StringComparison.OrdinalIgnoreCase) ? "microfone"
                        : sessao.Origem == OrigemDaSessao.Importada ? "apresentação" : "reunião";
                    try
                    {
                        var trechos = await transcritor.TranscreverAsync(caminho, fonte, etapa, ct).ConfigureAwait(false);
                        sessao.SubstituirFalas(trechos);
                        var idioma = transcritor.IdiomaDetectado
                            ?? (Transcritores.CodigoCurto(opcoes.Idioma) == "auto" ? "" : opcoes.Idioma);
                        if (!string.IsNullOrWhiteSpace(idioma)) sessao.Idioma = idioma;
                        etapa?.Report($"Transcrição: {trechos.Count} trechos" + (string.IsNullOrEmpty(sessao.Idioma) ? "" : $" em {Tradutor.NomeDoIdioma(sessao.Idioma)}") + ".");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        aviso?.Invoke("A transcrição falhou: " + ex.Message);
                    }
                }
            }
        }

        if (sessao.Falas.Count > 0) Analista.GravarTranscricao(sessao);
        sessao.Salvar();

        // ---- tradução ----
        if (opcoes.Traduzir && config.TraduzirQuandoIdiomaDiferente && sessao.Falas.Count > 0 && !sessao.TemTraducao)
        {
            var origem = Transcritores.CodigoCurto(sessao.Idioma);
            var destino = Transcritores.CodigoCurto(config.IdiomaDestino);
            if (origem != "auto" && origem != destino)
            {
                var conta = ContaClaude.Estado(config);
                if (!conta.Conectado)
                {
                    aviso?.Invoke("Tradução pulada: " + conta.Descricao);
                }
                else
                {
                    var r = await Tradutor.TraduzirAsync(sessao, config, TokenDe(conta), etapa, ct).ConfigureAwait(false);
                    if (!r.Ok) aviso?.Invoke("A tradução não saiu: " + r.Erro);
                    else etapa?.Report($"Tradução pronta ({r.TokensEntrada:N0} tokens de entrada, {r.TokensSaida:N0} de saída).");
                }
            }
        }

        // ---- resumo ----
        if (opcoes.Resumir)
        {
            var r = await new Analista().ResumirAsync(sessao, config, etapa, ct).ConfigureAwait(false);
            if (!r.Ok && r.Erro != null) aviso?.Invoke("O resumo do Claude não saiu: " + r.Erro);
        }

        sessao.Salvar();
    }

    /// <summary>Token do login feito no Gravador, quando é ele que vale; null deixa o `claude` usar o login dele.</summary>
    public static string? TokenDe(EstadoDaConta conta) =>
        conta.Meio == MeioDeAcesso.LoginNoGravador ? ClaudeLogin.Carregar()?.Token : null;
}
