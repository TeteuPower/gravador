using Gravador.Core.Settings;

namespace Gravador.Core.Legendas;

/// <summary>
/// Traduz um pedaço de legenda. Precisa ser rápido: o que passa de meio segundo aqui aparece como
/// atraso na tela, somado ao que o reconhecimento já gastou.
/// </summary>
public interface ITradutorAoVivo : IDisposable
{
    string Nome { get; }

    /// <summary>Está utilizável nesta máquina? Quando falso, <see cref="Motivo"/> explica.</summary>
    bool Disponivel { get; }

    string? Motivo { get; }

    /// <summary>Traduz. Devolve o texto original quando não consegue — legenda errada é melhor que legenda vazia.</summary>
    Task<string> TraduzirAsync(string texto, string de, string para, CancellationToken ct);
}

/// <summary>
/// Guarda o que já foi traduzido.
///
/// Não é otimização de sobra: a legenda ao vivo retraduz a MESMA linha a cada passada do whisper,
/// porque a parte firme dela não muda enquanto a provisória cresce. Sem cache, uma hora de
/// apresentação mandaria a mesma frase dez vezes para o tradutor — o que, no DeepL e no Azure, é
/// cota queimada à toa, e em qualquer um deles é atraso.
/// </summary>
public sealed class TradutorComCache : ITradutorAoVivo
{
    private const int Teto = 512;

    private readonly ITradutorAoVivo _interno;
    private readonly Dictionary<string, string> _cache = new();
    private readonly Queue<string> _ordem = new();
    private readonly object _trava = new();

    public TradutorComCache(ITradutorAoVivo interno) => _interno = interno;

    public string Nome => _interno.Nome;
    public bool Disponivel => _interno.Disponivel;
    public string? Motivo => _interno.Motivo;

    public async Task<string> TraduzirAsync(string texto, string de, string para, CancellationToken ct)
    {
        var chave = de + "" + para + "" + texto;
        lock (_trava)
            if (_cache.TryGetValue(chave, out var pronto)) return pronto;

        var traduzido = await _interno.TraduzirAsync(texto, de, para, ct).ConfigureAwait(false);

        lock (_trava)
        {
            if (_cache.TryAdd(chave, traduzido))
            {
                _ordem.Enqueue(chave);
                while (_ordem.Count > Teto) _cache.Remove(_ordem.Dequeue());
            }
        }
        return traduzido;
    }

    public void Dispose() => _interno.Dispose();
}

/// <summary>Monta o tradutor escolhido na configuração.</summary>
public static class Tradutores
{
    public static ITradutorAoVivo? Criar(AppSettings config)
    {
        ITradutorAoVivo? tradutor = config.LegendaTradutor switch
        {
            MotorTraducao.Marian => new TradutorMarian(config),
            MotorTraducao.DeepL => new TradutorDeepL(config),
            MotorTraducao.Azure => new TradutorAzure(config),
            _ => null,
        };
        return tradutor == null ? null : new TradutorComCache(tradutor);
    }

    /// <summary>"pt-BR" continua "pt-BR" no DeepL; "en-US" vira "EN". Cada serviço fala um dialeto de código.</summary>
    public static string CodigoCurto(string idioma)
    {
        if (string.IsNullOrWhiteSpace(idioma)) return "en";
        var i = idioma.IndexOf('-');
        return (i > 0 ? idioma[..i] : idioma).ToLowerInvariant();
    }
}
