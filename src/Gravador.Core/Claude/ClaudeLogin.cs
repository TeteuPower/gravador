using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gravador.Core.Claude;

public sealed class ClaudeCredenciais
{
    public string Token { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiraEm { get; set; }
    public string? Email { get; set; }
    public string? Organizacao { get; set; }
    public string? Plano { get; set; }
    public DateTimeOffset ConectadoEm { get; set; }

    /// <summary>De onde o token veio, para a interface poder dizer.</summary>
    public string Fonte { get; set; } = "";

    public bool Expirado => ExpiraEm != null && DateTimeOffset.UtcNow >= ExpiraEm.Value.AddMinutes(-2);
}

/// <summary>
/// Login com a conta Claude dentro da ferramenta: mesmo fluxo OAuth com PKCE que o Claude Code usa,
/// e o mesmo client_id público — o do <c>claude setup-token</c>.
///
/// Há dois caminhos, e os dois existem por um motivo. O automático sobe um ouvinte em
/// <c>127.0.0.1</c> numa porta efêmera e conclui sozinho quando o navegador volta; é o que dá menos
/// trabalho. O manual usa a página de código do console da Anthropic e pede que você cole o
/// resultado; é o que funciona quando um antivírus corporativo bloqueia o ouvinte local, que
/// acontece em máquina de empresa — exatamente onde esta ferramenta vai rodar.
///
/// O token fica só nesta máquina, protegido pelo DPAPI do usuário: sem isso, um arquivo de texto em
/// %APPDATA% daria acesso à conta a qualquer coisa que lesse a pasta.
/// </summary>
public static class ClaudeLogin
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string UrlAutorizacao = "https://claude.com/cai/oauth/authorize";
    private const string UrlToken = "https://platform.claude.com/v1/oauth/token";
    private const string RedirecionamentoManual = "https://console.anthropic.com/oauth/code/callback";

    // user:profile libera ler o perfil; user:inference é o par que o Claude Code pede. Pedir um
    // conjunto diferente do dele arrisca o servidor recusar o token na hora de usar.
    private const string Escopo = "user:inference user:profile";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly object Trava = new();

    private sealed class EmAndamento
    {
        public string Verificador = "";
        public string Estado = "";
        public string Redirecionamento = "";
        public DateTimeOffset Comecou;
        public HttpListener? Ouvinte;
    }

    private static EmAndamento? _pendente;

    private static string Arquivo => Path.Combine(AppInfo.PastaDados, "claude-login.bin");

    public static bool Conectado => File.Exists(Arquivo);

    public static bool AguardandoCodigo
    {
        get
        {
            lock (Trava)
            {
                if (_pendente == null) return false;
                if (DateTimeOffset.UtcNow - _pendente.Comecou > TimeSpan.FromMinutes(10))
                {
                    Cancelar();
                    return false;
                }
                return true;
            }
        }
    }

    // ==================================================================

    /// <summary>
    /// Monta a URL de autorização. Com <paramref name="manual"/> falso, tenta subir o ouvinte local
    /// e conclui sozinho; se a porta não abrir, cai no manual em silêncio — o que importa para quem
    /// está usando é entrar, não qual dos dois caminhos foi usado.
    /// </summary>
    public static (string Url, bool Manual) Iniciar(bool manual = false)
    {
        Cancelar();

        var verificador = Base64Url(RandomNumberGenerator.GetBytes(32));
        var estado = Base64Url(RandomNumberGenerator.GetBytes(32));
        var desafio = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));

        if (!manual)
        {
            var ouvinte = TentarOuvir(out var redirecionamento);
            if (ouvinte != null)
            {
                lock (Trava)
                {
                    _pendente = new EmAndamento
                    {
                        Verificador = verificador,
                        Estado = estado,
                        Redirecionamento = redirecionamento,
                        Comecou = DateTimeOffset.UtcNow,
                        Ouvinte = ouvinte,
                    };
                }
                _ = Task.Run(() => EsperarRetornoAsync(ouvinte));
                return (MontarUrl(redirecionamento, desafio, estado), false);
            }
        }

        lock (Trava)
        {
            _pendente = new EmAndamento
            {
                Verificador = verificador,
                Estado = estado,
                Redirecionamento = RedirecionamentoManual,
                Comecou = DateTimeOffset.UtcNow,
            };
        }
        return (MontarUrl(RedirecionamentoManual, desafio, estado), true);
    }

    private static string MontarUrl(string redirecionamento, string desafio, string estado) =>
        $"{UrlAutorizacao}?code=true&client_id={ClientId}&response_type=code"
        + $"&redirect_uri={Uri.EscapeDataString(redirecionamento)}"
        + $"&scope={Uri.EscapeDataString(Escopo)}"
        + $"&code_challenge={desafio}&code_challenge_method=S256&state={estado}";

    private static HttpListener? TentarOuvir(out string redirecionamento)
    {
        redirecionamento = "";
        try
        {
            // Porta efêmera: o socket escolhe uma livre, e só depois o HttpListener a reserva.
            var sonda = new TcpListener(IPAddress.Loopback, 0);
            sonda.Start();
            var porta = ((IPEndPoint)sonda.LocalEndpoint).Port;
            sonda.Stop();

            var ouvinte = new HttpListener();
            ouvinte.Prefixes.Add($"http://127.0.0.1:{porta}/");
            ouvinte.Start();
            redirecionamento = $"http://127.0.0.1:{porta}/callback";
            return ouvinte;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Disparado quando o login termina sozinho pelo ouvinte local.</summary>
    public static event Action<ClaudeCredenciais?, string?>? Concluiu;

    private static async Task EsperarRetornoAsync(HttpListener ouvinte)
    {
        try
        {
            var contexto = await ouvinte.GetContextAsync().ConfigureAwait(false);
            var consulta = contexto.Request.QueryString;
            string? erro = null;
            ClaudeCredenciais? credenciais = null;

            try
            {
                credenciais = await TrocarAsync(consulta["code"] ?? "", consulta["state"] ?? "")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                erro = ex.Message;
            }

            var corpo = Encoding.UTF8.GetBytes(erro == null
                ? Pagina("Conta Claude conectada ao Gravador", "Pode fechar esta aba e voltar para a ferramenta.")
                : Pagina("Não deu para conectar", erro, "#ff6b6b"));
            contexto.Response.StatusCode = erro == null ? 200 : 400;
            contexto.Response.ContentType = "text/html; charset=utf-8";
            contexto.Response.ContentLength64 = corpo.Length;
            await contexto.Response.OutputStream.WriteAsync(corpo).ConfigureAwait(false);
            contexto.Response.Close();

            Concluiu?.Invoke(credenciais, erro);
        }
        catch
        {
            // ouvinte fechado por cancelamento: caminho normal
        }
        finally
        {
            try { ouvinte.Close(); } catch { /* já fechado */ }
        }
    }

    private static string Pagina(string titulo, string sub, string cor = "#e8ebf2") =>
        "<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        + "<title>Gravador</title><body style=\"background:#0b0d12;color:#e8ebf2;font-family:Segoe UI,system-ui,sans-serif;"
        + "display:grid;place-items:center;min-height:100vh;margin:0\"><div style=\"text-align:center;max-width:460px;padding:40px 24px\">"
        + "<div style=\"width:56px;height:56px;border-radius:16px;margin:0 auto 22px;background:linear-gradient(135deg,#f0a37e,#c15f3c)\"></div>"
        + $"<div style=\"font-size:20px;font-weight:600;color:{cor}\">{WebUtility.HtmlEncode(titulo)}</div>"
        + $"<div style=\"color:#8b93a7;margin-top:10px;font-size:14px;line-height:1.55\">{WebUtility.HtmlEncode(sub)}</div></div></body>";

    /// <summary>Caminho manual: aceita "code#state", só o código, ou a URL inteira do callback.</summary>
    public static Task<ClaudeCredenciais> ConcluirManualAsync(string colado)
    {
        var (codigo, estado) = Separar(colado);
        return TrocarAsync(codigo, estado);
    }

    private static async Task<ClaudeCredenciais> TrocarAsync(string codigo, string estadoRecebido)
    {
        EmAndamento pendente;
        lock (Trava)
        {
            if (_pendente == null || DateTimeOffset.UtcNow - _pendente.Comecou > TimeSpan.FromMinutes(10))
            {
                _pendente = null;
                throw new InvalidOperationException("A autorização expirou. Clique em Entrar de novo.");
            }
            pendente = _pendente;
        }

        if (string.IsNullOrWhiteSpace(codigo))
            throw new InvalidOperationException("Não veio código de autorização.");
        if (estadoRecebido.Length > 0 && estadoRecebido != pendente.Estado)
            throw new InvalidOperationException("O código é de outra tentativa de login. Comece de novo.");

        var corpo = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = codigo,
            ["state"] = pendente.Estado,
            ["client_id"] = ClientId,
            ["redirect_uri"] = pendente.Redirecionamento,
            ["code_verifier"] = pendente.Verificador,
        });

        var credenciais = await PedirTokenAsync(corpo, null).ConfigureAwait(false);
        Cancelar();
        Salvar(credenciais);
        _ = Task.Run(() => BuscarPerfilAsync(credenciais));
        return credenciais;
    }

    private static async Task<ClaudeCredenciais> PedirTokenAsync(string corpo, ClaudeCredenciais? anterior)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, UrlToken)
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };
        pedido.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        pedido.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        using var resposta = await Http.SendAsync(pedido).ConfigureAwait(false);
        var texto = await resposta.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resposta.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"O Claude recusou o código (HTTP {(int)resposta.StatusCode}). {Cortar(texto)}");

        using var doc = JsonDocument.Parse(texto);
        var raiz = doc.RootElement;
        var acesso = Texto(raiz, "access_token", "accessToken");
        if (string.IsNullOrWhiteSpace(acesso))
            throw new InvalidOperationException("A resposta do Claude não trouxe o token de acesso.");

        return new ClaudeCredenciais
        {
            Token = acesso!.Trim(),
            RefreshToken = Texto(raiz, "refresh_token", "refreshToken") ?? anterior?.RefreshToken,
            ExpiraEm = raiz.TryGetProperty("expires_in", out var exp) && exp.TryGetDouble(out var seg)
                ? DateTimeOffset.UtcNow.AddSeconds(seg)
                : DateTimeOffset.UtcNow.AddMinutes(50),
            ConectadoEm = anterior?.ConectadoEm == default ? DateTimeOffset.UtcNow : anterior!.ConectadoEm,
            Email = anterior?.Email,
            Organizacao = anterior?.Organizacao,
            Plano = Texto(raiz, "subscriptionType", "subscription_type") ?? anterior?.Plano,
            Fonte = "Login no Gravador",
        };
    }

    /// <summary>E-mail e organização, só para a interface mostrar quem entrou. Falha em silêncio.</summary>
    private static async Task BuscarPerfilAsync(ClaudeCredenciais credenciais)
    {
        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
            pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credenciais.Token);
            pedido.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var resposta = await Http.SendAsync(pedido).ConfigureAwait(false);
            if (!resposta.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync().ConfigureAwait(false));
            var raiz = doc.RootElement;
            if (raiz.TryGetProperty("account", out var conta))
                credenciais.Email = Texto(conta, "email_address", "email") ?? credenciais.Email;
            if (raiz.TryGetProperty("organization", out var org))
                credenciais.Organizacao = Texto(org, "name") ?? credenciais.Organizacao;
            Salvar(credenciais);
            Concluiu?.Invoke(credenciais, null);
        }
        catch
        {
            // perfil é enfeite; o login vale sem ele
        }
    }

    public static async Task<ClaudeCredenciais?> RenovarAsync()
    {
        var atual = Carregar();
        if (string.IsNullOrWhiteSpace(atual?.RefreshToken)) return null;
        try
        {
            var corpo = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = atual!.RefreshToken!,
                ["client_id"] = ClientId,
            });
            var novo = await PedirTokenAsync(corpo, atual).ConfigureAwait(false);
            Salvar(novo);
            return novo;
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================

    public static ClaudeCredenciais? Carregar()
    {
        try
        {
            if (!File.Exists(Arquivo)) return null;
            var json = Cofre.Desproteger(File.ReadAllBytes(Arquivo));
            var c = JsonSerializer.Deserialize<ClaudeCredenciais>(json);
            if (c == null || string.IsNullOrWhiteSpace(c.Token)) return null;
            c.Fonte = "Login no Gravador";
            return c;
        }
        catch
        {
            return null;
        }
    }

    private static void Salvar(ClaudeCredenciais credenciais)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.PastaDados);
            File.WriteAllBytes(Arquivo, Cofre.Proteger(JsonSerializer.Serialize(credenciais)));
        }
        catch
        {
            // sem poder gravar, o login vale só nesta execução
        }
    }

    public static void Sair()
    {
        Cancelar();
        try { if (File.Exists(Arquivo)) File.Delete(Arquivo); } catch { /* arquivo travado */ }
    }

    public static void Cancelar()
    {
        lock (Trava)
        {
            try { _pendente?.Ouvinte?.Close(); } catch { /* já fechado */ }
            _pendente = null;
        }
    }

    // ------------------------------------------------------------------

    internal static (string Codigo, string Estado) Separar(string colado)
    {
        var s = (colado ?? "").Trim();
        if (s.Length == 0) return ("", "");

        if (s.Contains("://", StringComparison.Ordinal))
        {
            var codigo = Parametro(s, "code=");
            var estado = Parametro(s, "state=");
            return (Uri.UnescapeDataString(codigo), Uri.UnescapeDataString(estado));
        }

        var cerquilha = s.IndexOf('#');
        return cerquilha > 0 ? (s[..cerquilha].Trim(), s[(cerquilha + 1)..].Trim()) : (s, "");
    }

    private static string Parametro(string s, string chave)
    {
        var i = s.IndexOf(chave, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        i += chave.Length;
        var fim = i;
        while (fim < s.Length && s[fim] != '&' && s[fim] != '#' && !char.IsWhiteSpace(s[fim])) fim++;
        return s[i..fim];
    }

    private static string? Texto(JsonElement el, params string[] nomes)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            foreach (var n in nomes)
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        return null;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Cortar(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
