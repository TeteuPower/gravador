using System.Text.Json.Serialization;

namespace Gravador.Core.Session;

public enum TipoDeMarca
{
    Inicio,
    Fim,
    Pausa,
    Retomada,

    /// <summary>Você apertou o atalho de marcar. É o "isto aqui importa".</summary>
    Marcador,

    /// <summary>Uma imagem da tela foi guardada.</summary>
    Captura,

    /// <summary>Seu microfone passou a estar mudo.</summary>
    Mudo,

    /// <summary>Seu microfone voltou a estar aberto.</summary>
    Aberto,

    /// <summary>Um trecho transcrito.</summary>
    Fala,
}

/// <summary>
/// Um acontecimento na linha do tempo da gravação, sempre em tempo RELATIVO ao início.
///
/// Relativo e não absoluto porque quem lê depois — você ou a IA — está com o arquivo de áudio na
/// mão: "aos 14:32" é uma instrução que dá para seguir, "às 15h47 de terça" não.
/// </summary>
public sealed record Marca(
    [property: JsonPropertyName("em")] double EmSegundos,
    [property: JsonPropertyName("tipo")] TipoDeMarca Tipo,
    [property: JsonPropertyName("texto")] string? Texto = null,
    [property: JsonPropertyName("arquivo")] string? Arquivo = null,
    [property: JsonPropertyName("detalhe")] string? Detalhe = null)
{
    [JsonIgnore] public TimeSpan Em => TimeSpan.FromSeconds(EmSegundos);

    /// <summary>Carimbo legível: mm:ss até uma hora, depois h:mm:ss.</summary>
    [JsonIgnore] public string Carimbo => Formato.Carimbo(Em);

    [JsonIgnore]
    public string Rotulo => Tipo switch
    {
        TipoDeMarca.Inicio => "Começou",
        TipoDeMarca.Fim => "Terminou",
        TipoDeMarca.Pausa => "Pausado",
        TipoDeMarca.Retomada => "Retomado",
        TipoDeMarca.Marcador => string.IsNullOrWhiteSpace(Texto) ? "Marcador" : Texto,
        TipoDeMarca.Captura => "Captura de tela",
        TipoDeMarca.Mudo => Detalhe is { Length: > 0 } d ? $"Microfone mudo ({d})" : "Microfone mudo",
        TipoDeMarca.Aberto => "Microfone aberto",
        TipoDeMarca.Fala => Texto ?? "",
        _ => Tipo.ToString(),
    };
}

/// <summary>Um intervalo em que o microfone esteve mudo.</summary>
public sealed record TrechoMudo(
    [property: JsonPropertyName("de")] double DeSegundos,
    [property: JsonPropertyName("ate")] double AteSegundos,
    [property: JsonPropertyName("origem")] string Origem,
    [property: JsonPropertyName("confirmado")] bool Confirmado)
{
    [JsonIgnore] public TimeSpan Duracao => TimeSpan.FromSeconds(Math.Max(0, AteSegundos - DeSegundos));
}

/// <summary>Um pedaço de fala transcrito.</summary>
public sealed record TrechoFalado(
    [property: JsonPropertyName("de")] double DeSegundos,
    [property: JsonPropertyName("ate")] double AteSegundos,
    [property: JsonPropertyName("fonte")] string Fonte,
    [property: JsonPropertyName("texto")] string Texto,
    [property: JsonPropertyName("confianca")] double Confianca);
