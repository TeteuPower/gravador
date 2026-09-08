using System.Text.Json;
using Gravador.Core.Settings;

namespace Gravador.Core.Claude;

/// <summary>Como o Claude será chamado, e por quem.</summary>
public enum MeioDeAcesso
{
    Nenhum,

    /// <summary>Pelo `claude` desta máquina, com a assinatura já conectada nele.</summary>
    ClaudeCode,

    /// <summary>Pelo `claude`, com o token do login feito dentro do Gravador.</summary>
    LoginNoGravador,

    /// <summary>Direto na API, com uma chave de API (cobrança por uso, não pela assinatura).</summary>
    ChaveDeApi,
}

/// <summary>O que a interface precisa mostrar sobre a conta.</summary>
public sealed record EstadoDaConta(
    bool Conectado,
    MeioDeAcesso Meio,
    string? Email,
    string? Organizacao,
    string? Plano,
    bool CliInstalado,
    string Descricao);

/// <summary>
/// Descobre por onde falar com o Claude.
///
/// A ordem importa e não é arbitrária: primeiro o que usa a ASSINATURA (o `claude` desta máquina,
/// com o login dele ou com o do Gravador), depois a chave de API, que cobra por token. Quem tem as
/// duas coisas normalmente prefere não ser cobrado duas vezes pela mesma pergunta.
///
/// A chamada é feita pelo `claude` em modo não interativo, e não pela API direta com o token da
/// assinatura, porque token de assinatura não é credencial de API: usar um no lugar do outro é o
/// que dá 401 depois de o login ter dado certo. É a mesma escolha do Limpador, que fala com o
/// Claude pelo SDK do agente em vez de montar a requisição na mão.
/// </summary>
public static class ContaClaude
{
    /// <summary>Onde o Claude Code guarda o login dele.</summary>
    public static string ArquivoDoClaudeCode => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static bool ClaudeCodeConectado
    {
        get
        {
            try
            {
                if (!File.Exists(ArquivoDoClaudeCode)) return false;
                using var fs = new FileStream(ArquivoDoClaudeCode, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return false;
                return oauth.TryGetProperty("accessToken", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(t.GetString());
            }
            catch
            {
                // arquivo sendo reescrito no exato instante da renovação do token
                return false;
            }
        }
    }

    public static EstadoDaConta Estado(AppSettings config)
    {
        var cli = ClaudeCli.Localizar() != null;
        var login = ClaudeLogin.Carregar();

        if (config.FonteClaude != FonteCredencialClaude.Manual)
        {
            if (config.FonteClaude != FonteCredencialClaude.LoginNoApp && ClaudeCodeConectado && cli)
                return new EstadoDaConta(true, MeioDeAcesso.ClaudeCode, null, null, null, cli,
                    "Usando o login do Claude Code desta máquina.");

            if (login != null && cli)
                return new EstadoDaConta(true, MeioDeAcesso.LoginNoGravador, login.Email, login.Organizacao,
                    login.Plano, cli, $"Conectado{(login.Email is { Length: > 0 } e ? $" como {e}" : "")}.");
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return new EstadoDaConta(true, MeioDeAcesso.ChaveDeApi, null, null, null, cli,
                "Usando a chave em ANTHROPIC_API_KEY (cobrada por uso).");

        if (login != null && !cli)
            return new EstadoDaConta(false, MeioDeAcesso.Nenhum, login.Email, login.Organizacao, login.Plano, false,
                "Você entrou com a conta Claude, mas o Claude Code não está instalado nesta máquina — "
                + "é ele que executa o pedido. Instale com: npm install -g @anthropic-ai/claude-code");

        return new EstadoDaConta(false, MeioDeAcesso.Nenhum, null, null, null, cli,
            cli ? "Entre com a sua conta Claude para gerar o resumo."
                : "Para o resumo automático, entre com a conta Claude e instale o Claude Code "
                  + "(npm install -g @anthropic-ai/claude-code), ou defina ANTHROPIC_API_KEY.");
    }
}
