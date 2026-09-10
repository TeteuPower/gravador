using System.Text;
using System.Text.Json;
using Gravador.Core.Settings;

namespace Gravador.Core.Legendas;

/// <summary>
/// DeepL. É a melhor qualidade em português do Brasil entre os três, e o único que distingue
/// "PT-BR" de "PT-PT" no próprio pedido.
///
/// A chave vem de variável de ambiente, nunca do arquivo de configuração — mesma regra do serviço
/// de transcrição remoto. A chave gratuita termina em ":fx" e vale por outro endereço; isso é
/// detectado aqui em vez de virar mais uma opção para alguém errar.
/// </summary>
public sealed class TradutorDeepL : ITradutorAoVivo
{
    private static readonly HttpClient Rede = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _chave;

    public TradutorDeepL(AppSettings config)
    {
        _chave = (Environment.GetEnvironmentVariable(config.DeepLChaveEnv) ?? "").Trim();
        if (_chave.Length == 0)
            Motivo = $"A variável de ambiente {config.DeepLChaveEnv} não está definida nesta máquina. "
                   + "Pegue uma chave gratuita em deepl.com/pro-api, defina a variável e reabra o Gravador.";
    }

    public string Nome => "DeepL";
    public bool Disponivel => Motivo == null;
    public string? Motivo { get; private set; }

    /// <summary>A chave gratuita termina em ":fx" e atende por api-free; a paga, por api.</summary>
    private string Endereco => _chave.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
        ? "https://api-free.deepl.com/v2/translate"
        : "https://api.deepl.com/v2/translate";

    public async Task<string> TraduzirAsync(string texto, string de, string para, CancellationToken ct)
    {
        if (!Disponivel || string.IsNullOrWhiteSpace(texto)) return texto;

        try
        {
            var corpo = JsonSerializer.Serialize(new
            {
                text = new[] { texto },
                source_lang = Tradutores.CodigoCurto(de).ToUpperInvariant(),
                // O DeepL quer "PT-BR" inteiro no destino; usar só "PT" devolve português europeu.
                target_lang = para.ToUpperInvariant(),
            });

            using var pedido = new HttpRequestMessage(HttpMethod.Post, Endereco)
            {
                Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
            };
            pedido.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + _chave);

            using var resposta = await Rede.SendAsync(pedido, ct).ConfigureAwait(false);
            if (!resposta.IsSuccessStatusCode) return texto;

            using var doc = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("translations", out var lista)) return texto;
            foreach (var t in lista.EnumerateArray())
                if (t.TryGetProperty("text", out var v) && v.GetString() is { Length: > 0 } s)
                    return s;
            return texto;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // sem rede, cota estourada, chave errada: devolve o original. Legenda em inglês é pior
            // do que em português, e melhor do que legenda nenhuma.
            return texto;
        }
    }

    public void Dispose() { }
}
