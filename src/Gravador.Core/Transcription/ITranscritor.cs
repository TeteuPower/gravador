using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Core.Transcription;

/// <summary>O que um motor de transcrição precisa saber dizer sobre si mesmo.</summary>
public interface ITranscritor : IDisposable
{
    string Nome { get; }

    /// <summary>Está utilizável nesta máquina? Quando falso, <see cref="Motivo"/> explica.</summary>
    bool Disponivel { get; }

    string? Motivo { get; }
}

/// <summary>Transcreve enquanto a reunião acontece, recebendo o áudio conforme ele é capturado.</summary>
public interface ITranscritorAoVivo : ITranscritor
{
    /// <summary>
    /// Recebe um pedaço de áudio já no formato interno da gravação.
    ///
    /// O vetor é REAPROVEITADO pela trilha a cada buffer: quem guardar precisa copiar. Isso existe
    /// para a gravação não alocar um vetor por buffer — são dezenas por segundo, durante horas.
    /// </summary>
    void Alimentar(string fonte, float[] amostras, int quantidade, int taxa, int canais, TimeSpan em);

    event Action<TrechoFalado>? Reconheceu;

    /// <summary>Texto parcial da frase em formação, para a legenda ao vivo. Pode vir vazio.</summary>
    event Action<string>? Parcial;

    void Iniciar();
    void Encerrar();
}

/// <summary>Transcreve o arquivo pronto, depois que a gravação termina.</summary>
public interface ITranscritorDeArquivo : ITranscritor
{
    Task<IReadOnlyList<TrechoFalado>> TranscreverAsync(string arquivo, string fonte,
        IProgress<string>? etapa, CancellationToken ct);
}

/// <summary>
/// Escolhe e monta o motor conforme a configuração.
///
/// Por que o Claude não aparece aqui: os modelos do Claude leem texto, imagem e PDF — não áudio.
/// Fazer a transcrição com ele exigiria mandar o som, o que não é possível. O que o Claude faz nesta
/// ferramenta é o passo seguinte, que é onde ele é bom: ler a transcrição junto com as capturas de
/// tela e devolver o resumo, as decisões e as pendências (ver <see cref="Claude.Analista"/>).
///
/// Quem quiser transcrição de verdade em português tem duas saídas aqui, e elas trocam CPU por
/// qualidade: o reconhecedor do próprio Windows (offline, de graça, medíocre) ou um serviço
/// compatível com a API da OpenAI (Whisper e afins — excelente em português, roda fora da máquina,
/// custa por minuto).
/// </summary>
public static class Transcritores
{
    public static ITranscritorAoVivo? AoVivo(AppSettings config) => config.Transcricao switch
    {
        MotorTranscricao.Windows when config.TranscreverAoVivo => new WindowsTranscritor(config),
        _ => null,
    };

    public static ITranscritorDeArquivo? DeArquivo(AppSettings config) => config.Transcricao switch
    {
        MotorTranscricao.Remoto => new RemotoTranscritor(config),
        _ => null,
    };
}
