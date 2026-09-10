using System.Text;
using System.Text.Json;
using Gravador.Core.Settings;

namespace Gravador.Core.Legendas;

/// <summary>
/// Azure Translator. A cota gratuita é a mais generosa dos três — 2 milhões de caracteres por mês,
/// o que dá umas 30 horas de fala.
///
/// Diferente do DeepL, ele exige a REGIÃO do recurso junto da chave: um recurso criado em
/// "brazilsouth" recusa o pedido que não diga isso, com um 401 que não explica o motivo. Por isso a
/// região é campo próprio e a falta dela é dita antes de tentar.
/// </summary>
public sealed class TradutorAzure : ITradutorAoVivo
{
    private const string Endereco = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0";

    private static readonly HttpClient Rede = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _chave;
    private readonly string _regiao;

    public TradutorAzure(AppSettings config)
    {
        _chave = (Environment.GetEnvironmentVariable(config.AzureChaveEnv) ?? "").Trim();
        _regiao = (config.AzureRegiao ?? "").Trim();

        if (_chave.Length == 0)
            Motivo = $"A variável de ambiente {config.AzureChaveEnv} não está definida nesta máquina. "
                   + "Crie um recurso Translator (camada gratuita) no portal do Azure, defina a variável "
                   + "com a chave e reabra o Gravador.";
        else if (_regiao.Length == 0)
            Motivo = "Falta a região do recurso do Azure (por exemplo \"brazilsouth\"). Sem ela o "
                   + "serviço recusa o pedido com um erro que não diz o porquê.";
    }

    public string Nome => "Azure Translator";
    public bool Disponivel => Motivo == null;
    public string? Motivo { get; private set; }

    public async Task<string> TraduzirAsync(string texto, string de, string para, CancellationToken ct)
    {
        if (!Disponivel || string.IsNullOrWhiteSpace(texto)) return texto;

        try
        {
            var url = $"{Endereco}&from={Tradutores.CodigoCurto(de)}&to={para.ToLowerInvariant()}";
            var corpo = JsonSerializer.Serialize(new[] { new { Text = texto } });

            using var pedido = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
            };
            pedido.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _chave);
            pedido.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", _regiao);

            using var resposta = await Rede.SendAsync(pedido, ct).ConfigureAwait(false);
            if (!resposta.IsSuccessStatusCode) return texto;

            using var doc = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return texto;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("translations", out var lista)) continue;
                foreach (var t in lista.EnumerateArray())
                    if (t.TryGetProperty("text", out var v) && v.GetString() is { Length: > 0 } s)
                        return s;
            }
            return texto;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return texto;
        }
    }

    public void Dispose() { }
}
