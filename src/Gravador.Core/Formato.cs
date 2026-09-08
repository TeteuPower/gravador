namespace Gravador.Core;

/// <summary>
/// Como tempo e tamanho aparecem para quem lê. Um lugar só, porque estas strings saem em três
/// destinos diferentes — a janela, a linha de comando e o texto que vai para o Claude — e três
/// versões da mesma regra sempre acabam divergindo.
/// </summary>
public static class Formato
{
    /// <summary>Carimbo de posição no áudio: mm:ss até uma hora, depois h:mm:ss.</summary>
    public static string Carimbo(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";

    public static string Carimbo(double segundos) => Carimbo(TimeSpan.FromSeconds(Math.Max(0, segundos)));

    /// <summary>Duração por extenso, do jeito que se fala: "1h20min", "8min12s", "43s".</summary>
    public static string Duracao(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}min{t.Seconds:00}s"
        : $"{t.Seconds}s";

    /// <summary>Relógio da gravação em andamento, sempre com as três casas.</summary>
    public static string Cronometro(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static string Tamanho(long bytes)
    {
        string[] unidades = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < unidades.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {unidades[i]}";
    }
}
