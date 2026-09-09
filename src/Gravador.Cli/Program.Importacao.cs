using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gravador.Core;
using Gravador.Core.Claude;
using Gravador.Core.Ferramentas;
using Gravador.Core.Importacao;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Cli;

/// <summary>Comandos de importação, transcrição e tradução — o fluxo "já tenho o arquivo".</summary>
internal static partial class Program
{
    private static async Task<int> Importar(string[] argv)
    {
        if (argv.Length == 0 || argv[0].StartsWith("--"))
        {
            Console.Error.WriteLine("Informe o arquivo: gravador-cli importar \"C:\\Videos\\apresentacao.mp4\" [opções]");
            return 2;
        }

        var config = AppSettings.Carregar();
        var opcoes = new OpcoesDeImportacao();
        for (var i = 1; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "--titulo" when i + 1 < argv.Length: opcoes.Titulo = argv[++i]; break;
                case "--idioma" when i + 1 < argv.Length: opcoes.Idioma = argv[++i]; break;
                case "--motor" when i + 1 < argv.Length: opcoes.Motor = LerMotor(argv[++i]); break;
                case "--modelo" when i + 1 < argv.Length: config.WhisperModelo = argv[++i]; break;
                case "--saida" when i + 1 < argv.Length: config.PastaSaida = argv[++i]; break;
                case "--sem-quadros": opcoes.ExtrairQuadros = false; break;
                case "--sem-transcricao": opcoes.Transcrever = false; break;
                case "--sem-traducao": opcoes.Traduzir = false; break;
                case "--resumir": opcoes.Resumir = true; break;
                case "--manter-miniaturas": config.ImportacaoManterMiniaturas = true; break;
            }
        }
        config.Sanear();

        var importador = new ImportadorDeMidia(config);
        importador.Aviso += a => Console.Error.WriteLine($"  aviso: {a}");

        Console.WriteLine();
        Console.WriteLine($"Importando {argv[0]}");
        var inicio = DateTime.UtcNow;
        var sessao = await importador.ImportarAsync(argv[0], opcoes, Progresso()).ConfigureAwait(false);
        LimparLinha();

        Console.WriteLine();
        Console.WriteLine($"Pronto em {Formato.Duracao(DateTime.UtcNow - inicio)}: {sessao.Pasta}");
        Console.WriteLine($"  duração:     {Formato.Duracao(sessao.Duracao)}");
        Console.WriteLine($"  áudio:       {sessao.Arquivos.Mixado}");
        Console.WriteLine($"  imagens:     {sessao.QuantidadeDeCapturas}");
        var naoPert = sessao.TrechosDeVideo.Where(t => t.Tipo == TipoDeTrecho.NaoPertinente).Sum(t => t.Duracao.TotalSeconds);
        if (sessao.TrechosDeVideo.Count > 0)
            Console.WriteLine($"  descartado:  {Formato.Duracao(TimeSpan.FromSeconds(naoPert))} de vídeo não pertinente");
        Console.WriteLine($"  transcrição: {(sessao.Falas.Count > 0 ? $"{sessao.Falas.Count} trechos em {Tradutor.NomeDoIdioma(sessao.Idioma)}" : "não")}");
        Console.WriteLine($"  tradução:    {(sessao.TemTraducao ? "traducao.md" : "não")}");
        Console.WriteLine($"  resumo:      {(sessao.TemResumo ? "resumo.md" : "não")}");
        return 0;
    }

    /// <summary>
    /// Só o funil de quadros, para calibrar sem refazer a importação inteira. Aceita miniaturas já
    /// extraídas (<c>--miniaturas</c>) e grava o resultado da análise e as imagens na saída.
    /// </summary>
    private static async Task<int> Quadros(string[] argv)
    {
        string? video = null, miniaturas = null, saida = null;
        double fps = 1;
        var config = AppSettings.Carregar();
        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "--video" when i + 1 < argv.Length: video = argv[++i]; break;
                case "--miniaturas" when i + 1 < argv.Length: miniaturas = argv[++i]; break;
                case "--saida" when i + 1 < argv.Length: saida = argv[++i]; break;
                case "--fps" when i + 1 < argv.Length: fps = double.Parse(argv[++i], CultureInfo.InvariantCulture); break;
                case "--sem-recorte": config.ImportacaoRecortarNoConteudo = false; break;
            }
        }
        if (video == null || saida == null)
        {
            Console.Error.WriteLine("Uso: gravador-cli quadros --video <mp4> --saida <pasta> [--miniaturas <pasta>] [--fps 1]");
            return 2;
        }

        Directory.CreateDirectory(saida);
        var info = await Ffmpeg.SondarAsync(video).ConfigureAwait(false);
        Console.WriteLine($"vídeo: {info.Largura}x{info.Altura} @ {info.QuadrosPorSegundo:0.##} fps, {Formato.Duracao(info.Duracao)}");

        if (miniaturas == null)
        {
            miniaturas = Path.Combine(saida, "miniaturas");
            var t0 = DateTime.UtcNow;
            var n = await Ffmpeg.MiniaturasAsync(video, miniaturas, fps, config.ImportacaoLarguraMiniatura, info.Duracao,
                new Progress<double>(f => Console.Write($"\r  amostrando... {f * 100:0}%   ")), CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"\r  {n} miniaturas em {Formato.Duracao(DateTime.UtcNow - t0)}");
        }

        var t1 = DateTime.UtcNow;
        var analise = AnaliseDeQuadros.Analisar(miniaturas, fps, info.Largura, info.Altura,
            new Progress<double>(f => Console.Write($"\r  analisando... {f * 100:0}%   ")));
        Console.WriteLine($"\r  análise em {Formato.Duracao(DateTime.UtcNow - t1)}");

        var json = JsonSerializer.Serialize(analise, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        await File.WriteAllTextAsync(Path.Combine(saida, "analise.json"), json).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"  conteúdo: {Descrever(analise.RetanguloDoConteudo)}");
        Console.WriteLine($"  slide:    {Descrever(analise.RetanguloDoSlide)}");
        Console.WriteLine($"  slides encontrados: {analise.Slides}");
        Console.WriteLine($"  não pertinente: {Formato.Duracao(TimeSpan.FromSeconds(analise.SegundosNaoPertinentes))}");
        foreach (var d in analise.Diagnostico) Console.WriteLine($"    {d.Key} = {d.Value:0.###}");

        Console.WriteLine();
        Console.WriteLine("  linha do tempo:");
        foreach (var t in analise.Trechos)
            Console.WriteLine($"    {Formato.Carimbo(t.DeSegundos),8} -> {Formato.Carimbo(t.AteSegundos),8}  {t.Tipo,-14} q{t.QuadroRepresentativo}  {Descrever(t.Recorte)}");

        var slides = analise.Trechos.Where(t => t.Tipo == TipoDeTrecho.Slide).ToList();
        var pastaSlides = Path.Combine(saida, "slides");
        Directory.CreateDirectory(pastaSlides);
        var idx = 1;
        foreach (var t in slides)
        {
            var em = TimeSpan.FromSeconds((t.QuadroRepresentativo + 0.5) / fps);
            var destino = Path.Combine(pastaSlides, $"{idx:000}_slide_{(int)em.TotalMinutes:00}m{em.Seconds:00}s.jpg");
            Console.Write($"\r  extraindo slide {idx} de {slides.Count}...   ");
            var recorte = config.ImportacaoRecortarNoConteudo ? (t.Recorte ?? analise.RetanguloDoConteudo) : null;
            await Ffmpeg.ExtrairQuadroAsync(video, em, destino, recorte?.Tupla, config.ImportacaoLarguraQuadroFinal).ConfigureAwait(false);
            idx++;
        }
        Console.WriteLine($"\r  {idx - 1} slides em {pastaSlides}");
        return 0;
    }

    private static string Descrever(Retangulo? r) => r == null ? "(não achou)" : $"{r.Largura}x{r.Altura} em ({r.X},{r.Y})";

    private static async Task<int> Transcrever(string[] argv)
    {
        if (argv.Length == 0) { Console.Error.WriteLine("Informe a pasta da sessão."); return 2; }
        var sessao = SessaoGravacao.Abrir(argv[0]);
        if (sessao == null) { Console.Error.WriteLine($"Não achei uma sessão em {argv[0]}."); return 2; }

        var config = AppSettings.Carregar();
        var motor = MotorTranscricao.Whisper;
        var idioma = "auto";
        var traduzir = true;
        var resumir = false;
        for (var i = 1; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "--motor" when i + 1 < argv.Length: motor = LerMotor(argv[++i]); break;
                case "--idioma" when i + 1 < argv.Length: idioma = argv[++i]; break;
                case "--modelo" when i + 1 < argv.Length: config.WhisperModelo = argv[++i]; break;
                case "--sem-traducao": traduzir = false; break;
                case "--resumir": resumir = true; break;
                case "--refazer": sessao.SubstituirFalas([]); break;
            }
        }
        config.Sanear();

        await PosProcessamento.ExecutarAsync(sessao, config,
            new PosProcessamento.Opcoes { Motor = motor, Idioma = idioma, Traduzir = traduzir, Resumir = resumir },
            Progresso(), a => Console.Error.WriteLine($"  aviso: {a}"), CancellationToken.None).ConfigureAwait(false);
        LimparLinha();

        Console.WriteLine($"transcrição: {sessao.Falas.Count} trechos em {Tradutor.NomeDoIdioma(sessao.Idioma)} → {sessao.CaminhoTranscricao}");
        if (sessao.TemTraducao) Console.WriteLine($"tradução:    {sessao.CaminhoTraducao}");
        return 0;
    }

    private static async Task<int> Traduzir(string[] argv)
    {
        if (argv.Length == 0) { Console.Error.WriteLine("Informe a pasta da sessão."); return 2; }
        var sessao = SessaoGravacao.Abrir(argv[0]);
        if (sessao == null) { Console.Error.WriteLine($"Não achei uma sessão em {argv[0]}."); return 2; }

        var config = AppSettings.Carregar();
        for (var i = 1; i < argv.Length; i++)
            if (argv[i].Equals("--para", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length) config.IdiomaDestino = argv[++i];

        var conta = ContaClaude.Estado(config);
        if (!conta.Conectado) { Console.Error.WriteLine(conta.Descricao); return 3; }

        var r = await Tradutor.TraduzirAsync(sessao, config, PosProcessamento.TokenDe(conta), Progresso()).ConfigureAwait(false);
        LimparLinha();
        if (!r.Ok) { Console.Error.WriteLine("Não deu: " + r.Erro); return 1; }
        Console.WriteLine($"tradução em {sessao.CaminhoTraducao}  ({r.TokensEntrada:N0} tokens de entrada, {r.TokensSaida:N0} de saída, {Formato.Duracao(r.Duracao)})");
        return 0;
    }

    private static async Task<int> FerramentasStatus(string[] argv)
    {
        var config = AppSettings.Carregar();
        var lista = new[] { Ferramentas.Ffmpeg, Ferramentas.Ffprobe, Ferramentas.WhisperCli, Ferramentas.ModeloWhisper(config.WhisperModelo) };
        Console.WriteLine();
        Console.WriteLine($"Pasta: {AppInfo.PastaFerramentas}");
        foreach (var f in lista)
            Console.WriteLine($"  {f.Nome,-24} {(f.Localizar() is { } c ? c : $"não baixada (~{f.TamanhoAproximadoMb} MB)")}");

        if (argv.Contains("--baixar"))
        {
            foreach (var f in lista.Where(f => !f.Disponivel))
            {
                Console.WriteLine();
                await f.GarantirAsync(new Progress<ProgressoDeFerramenta>(p =>
                    Console.Write($"\r  {p.Ferramenta}: {p.Etapa} {(p.Fracao is { } fr ? $"{fr * 100:0}%" : "")}      "))).ConfigureAwait(false);
                Console.WriteLine($"\r  {f.Nome}: pronto → {f.CaminhoLocal}");
            }
        }
        return 0;
    }

    /// <summary>Uma pergunta ao Claude sobre a sessão, com a resposta saindo conforme chega.</summary>
    private static async Task<int> Conversar(string[] argv)
    {
        if (argv.Length < 2) { Console.Error.WriteLine("Uso: gravador-cli conversar <pasta> \"pergunta\" [--reiniciar]"); return 2; }
        var sessao = SessaoGravacao.Abrir(argv[0]);
        if (sessao == null) { Console.Error.WriteLine($"Não achei uma sessão em {argv[0]}."); return 2; }

        var config = AppSettings.Carregar();
        var conversa = new Conversa(sessao, config);
        if (argv.Contains("--reiniciar")) conversa.Reiniciar();

        var pergunta = string.Join(" ", argv.Skip(1).Where(a => !a.StartsWith("--")));
        Console.WriteLine();
        var r = await conversa.PerguntarAsync(pergunta,
            aoReceberTexto: t => Console.Write(t),
            aoChamarFerramenta: f => Console.Error.WriteLine($"  [{f.Replace("mcp__gravador__", "")}]")).ConfigureAwait(false);
        Console.WriteLine();
        if (!r.Ok) { Console.Error.WriteLine("Não deu: " + r.Erro); return 1; }
        Console.WriteLine();
        Console.WriteLine($"  ({r.TokensEntrada:N0} tokens de entrada, {r.TokensCacheLidos:N0} do cache, {r.TokensSaida:N0} de saída, "
            + $"{r.Turnos} turno(s), {Formato.Duracao(r.Duracao)}{(r.CustoUsd is { } c ? $", US$ {c:0.000}" : "")})");
        return 0;
    }

    /// <summary>
    /// Servidor MCP da sessão, por stdio. É assim que o `claude` recebe as ferramentas do Gravador:
    /// ele nos executa como subprocesso e conversa em JSON-RPC pelo stdin/stdout.
    /// </summary>
    private static async Task<int> Mcp(string[] argv)
    {
        string? pasta = null;
        for (var i = 0; i < argv.Length; i++)
            if (argv[i].Equals("--sessao", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length) pasta = argv[++i];
        if (pasta == null)
        {
            Console.Error.WriteLine("Uso: gravador-cli mcp --sessao <pasta>");
            return 2;
        }

        var sessao = SessaoGravacao.Abrir(pasta);
        if (sessao == null)
        {
            Console.Error.WriteLine($"Não achei uma sessão em {pasta}.");
            return 2;
        }

        var servidor = new Gravador.Core.Claude.Mcp.ServidorMcp("gravador",
            Gravador.Core.Claude.Mcp.FerramentasDaSessao.Instrucoes(sessao),
            Gravador.Core.Claude.Mcp.FerramentasDaSessao.Para(sessao));
        return await servidor.RodarAsync().ConfigureAwait(false);
    }

    private static MotorTranscricao LerMotor(string texto) => texto.ToLowerInvariant() switch
    {
        "whisper" => MotorTranscricao.Whisper,
        "windows" => MotorTranscricao.Windows,
        "remoto" => MotorTranscricao.Remoto,
        "nenhum" or "nao" or "não" => MotorTranscricao.Nenhum,
        _ => MotorTranscricao.Whisper,
    };

    /// <summary>Etapas na mesma linha, que se sobrescrevem — o log de uma importação tem centenas.</summary>
    private static IProgress<string> Progresso() => new Progress<string>(e =>
    {
        if (Console.IsOutputRedirected) { Console.WriteLine("  " + e); return; }
        var linha = "  " + e;
        Console.Write("\r" + linha.PadRight(Math.Max(_ultimoTamanho, linha.Length)));
        _ultimoTamanho = linha.Length;
        if (e.EndsWith('.') && !e.EndsWith("...")) { Console.WriteLine(); _ultimoTamanho = 0; }
    });

    private static void LimparLinha()
    {
        if (Console.IsOutputRedirected || _ultimoTamanho == 0) return;
        Console.Write("\r" + new string(' ', _ultimoTamanho) + "\r");
        _ultimoTamanho = 0;
    }
}
