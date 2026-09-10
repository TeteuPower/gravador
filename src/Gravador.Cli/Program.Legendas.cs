using System.Diagnostics;
using Gravador.Core;
using Gravador.Core.Audio;
using Gravador.Core.Legendas;
using Gravador.Core.Settings;
using NAudio.Wave;

namespace Gravador.Cli;

internal static partial class Program
{
    /// <summary>
    /// Legenda ao vivo, na linha de comando.
    ///
    /// Com `--arquivo`, o áudio é despejado NO RITMO DO RELÓGIO, como se estivesse tocando agora.
    /// É o que permite conferir o atraso de verdade sem depender de uma reunião acontecendo: um
    /// arquivo lido o mais rápido possível provaria que o texto sai certo, mas não provaria nada
    /// sobre o que mais importa aqui, que é chegar a tempo.
    /// </summary>
    private static async Task<int> Legendas(string[] argv)
    {
        var config = AppSettings.Carregar();
        string? arquivo = null;
        double? limite = null;

        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "--arquivo" or "--video" or "--audio" when i + 1 < argv.Length:
                    arquivo = argv[++i]; break;
                case "--segundos" when i + 1 < argv.Length:
                    limite = double.Parse(argv[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--modelo" when i + 1 < argv.Length:
                    config.LegendaModelo = argv[++i]; break;
                case "--de" when i + 1 < argv.Length:
                    config.LegendaIdiomaFala = argv[++i]; break;
                case "--para" when i + 1 < argv.Length:
                    config.IdiomaDestino = argv[++i]; break;
                case "--original":
                    config.LegendaMostrarOriginal = true; break;
                case "--tradutor" when i + 1 < argv.Length:
                    config.LegendaTradutor = argv[++i].ToLowerInvariant() switch
                    {
                        "marian" or "local" => MotorTraducao.Marian,
                        "deepl" => MotorTraducao.DeepL,
                        "azure" => MotorTraducao.Azure,
                        _ => MotorTraducao.Nenhum,
                    };
                    break;
            }
        }
        config.Sanear();

        if (arquivo == null)
        {
            Console.Error.WriteLine("Falta o que legendar: gravador-cli legendas --arquivo <mp4 ou mp3> [--segundos 60]");
            Console.Error.WriteLine("Opções: --tradutor marian|deepl|azure|nenhum  --modelo tiny|base  --de en  --para pt-BR  --original");
            return 2;
        }
        if (!File.Exists(arquivo)) { Console.Error.WriteLine("Não achei " + arquivo); return 2; }

        using var servico = new ServicoDeLegenda(config);
        Console.WriteLine($"Ouvindo com: {servico.Ouvinte}");
        Console.WriteLine($"Traduzindo com: {servico.Tradutor}");
        if (!servico.Disponivel) { Console.Error.WriteLine("Não dá para legendar: " + servico.Motivo); return 1; }
        Console.WriteLine();

        var relogio = Stopwatch.StartNew();
        var ultima = "";
        servico.Atualizou += linha =>
        {
            if (linha.Traduzido == ultima) return;
            ultima = linha.Traduzido;
            var cauda = linha.Provisorio.Length > 0 ? $"   [+ {linha.Provisorio}]" : "";
            Console.WriteLine($"[{Formato.Cronometro(relogio.Elapsed)}] {linha.Traduzido}{cauda}");
            if (linha.Original.Length > 0) Console.WriteLine($"{new string(' ', 11)}({linha.Original})");
        };

        if (Environment.GetEnvironmentVariable("GRAVADOR_LEGENDA_DEBUG") == "1")
            servico.Reconheceu += t => Console.WriteLine(
                $"   .. frase fechada: de={t.DeSegundos:0.00}s ate={t.AteSegundos:0.00}s ({t.Texto.Length} car)");

        servico.Iniciar();
        await DespejarNoRitmoAsync(arquivo, limite, servico).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("Fim do áudio; esperando a última passada...");
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        servico.Encerrar();
        Console.WriteLine($"Última passada do whisper: {servico.UltimaPassadaMs} ms");
        return 0;
    }

    /// <summary>Lê o arquivo e entrega o áudio na velocidade em que ele seria ouvido.</summary>
    private static async Task DespejarNoRitmoAsync(string arquivo, double? limite, ServicoDeLegenda servico)
    {
        using var leitor = new MediaFoundationReader(arquivo);
        var formato = leitor.WaveFormat;
        var bytesPorSegundo = formato.AverageBytesPerSecond;
        var buffer = new byte[bytesPorSegundo / 10];   // pedaços de 100 ms, como o WASAPI entrega
        var conversor = new SampleConverter(formato, formato.SampleRate, formato.Channels);

        var relogio = Stopwatch.StartNew();
        var posicao = TimeSpan.Zero;
        int lidos;

        while ((lidos = leitor.Read(buffer, 0, buffer.Length)) > 0)
        {
            var (amostras, quantidade) = conversor.Converter(buffer, lidos);
            if (quantidade > 0)
                servico.Alimentar("sistema", amostras, quantidade, formato.SampleRate, formato.Channels, posicao);

            posicao += TimeSpan.FromSeconds((double)lidos / bytesPorSegundo);
            if (limite.HasValue && posicao.TotalSeconds >= limite.Value) break;

            // Segura o passo no relógio: é isto que torna o teste comparável a uma reunião.
            var adiantado = posicao - relogio.Elapsed;
            if (adiantado > TimeSpan.FromMilliseconds(5))
                await Task.Delay(adiantado).ConfigureAwait(false);
        }
    }
}
