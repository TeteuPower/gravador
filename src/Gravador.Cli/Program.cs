using System.Globalization;
using System.Text;
using Gravador.Core;
using Gravador.Core.Atualizacao;
using Gravador.Core.Audio;
using Gravador.Core.Claude;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Cli;

/// <summary>
/// Modo linha de comando.
///
/// Existe por dois motivos. O primeiro é imediato: dá para gravar uma reunião sem abrir janela
/// nenhuma, e dá para verificar a captura de áudio numa máquina nova em dez segundos. O segundo é o
/// que interessa mais adiante — quando esta ferramenta entrar no conjunto com as outras, é por aqui
/// (e pelo modo `ipc`) que elas vão falar com ela, do mesmo jeito que o app do Limpador fala com o
/// motor de varredura dele.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        AppInfo.GarantirPastas();

        try
        {
            return Executar(argv);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Erro: {ex.Message}");
            return 1;
        }
    }

    private static int Executar(string[] argv)
    {
        var comando = argv.Length > 0 ? argv[0].ToLowerInvariant() : "ajuda";
        var resto = argv.Skip(1).ToArray();

        return comando switch
        {
            "ipc" => IpcServer.Run(),
            "dispositivos" or "devices" => Dispositivos(),
            "gravar" or "rec" => Gravar(resto).GetAwaiter().GetResult(),
            "sessoes" or "sessões" => Sessoes(),
            "resumir" => Resumir(resto).GetAwaiter().GetResult(),
            "conta" => Conta(),
            "entrar" => Entrar(resto).GetAwaiter().GetResult(),
            "importar" => Importar(resto).GetAwaiter().GetResult(),
            "quadros" => Quadros(resto).GetAwaiter().GetResult(),
            "transcrever" => Transcrever(resto).GetAwaiter().GetResult(),
            "traduzir" => Traduzir(resto).GetAwaiter().GetResult(),
            "ferramentas" => FerramentasStatus(resto).GetAwaiter().GetResult(),
            "mcp" => Mcp(resto).GetAwaiter().GetResult(),
            "conversar" => Conversar(resto).GetAwaiter().GetResult(),
            "atualizacao" or "atualização" => Atualizacao(resto).GetAwaiter().GetResult(),
            "ajuda" or "--ajuda" or "-h" or "--help" => Ajuda(),
            _ => Desconhecido(comando),
        };
    }

    private static int Desconhecido(string comando)
    {
        Console.Error.WriteLine($"Comando desconhecido: {comando}");
        Ajuda();
        return 2;
    }

    private static int Ajuda()
    {
        Console.WriteLine($"""
            Gravador {AppInfo.Versao} — grava o áudio do computador e o seu microfone.

            USO
              gravador dispositivos            lista os dispositivos de áudio
              gravador gravar [opções]         grava até você apertar Enter
              gravador sessoes                 lista as gravações já feitas
              gravador resumir <pasta>         pede o resumo da sessão ao Claude
              gravador conta                   mostra por onde o Claude será chamado
              gravador entrar [--manual]       entra com a conta Claude
              gravador ipc                     modo de integração (JSON por linha)
              gravador atualizacao [--baixar] [--instalar] [--repo dono/nome]
                                               procura versão nova nas releases do GitHub

              gravador importar <arquivo>      vira sessão: áudio, slides, transcrição, tradução
              gravador transcrever <pasta>     transcreve (ou refaz) uma sessão já existente
              gravador traduzir <pasta>        traduz a transcrição com o Claude
              gravador quadros --video <mp4> --saida <pasta>   só o funil de slides, para calibrar
              gravador ferramentas [--baixar]  estado do ffmpeg, whisper e modelo
              gravador conversar <pasta> "pergunta"   pergunta ao Claude sobre a sessao
              gravador mcp --sessao <pasta>    servidor MCP da sessao (o claude nos chama assim)

            OPÇÕES DE `gravar`
              --segundos N        para sozinho depois de N segundos
              --saida PASTA       onde criar a pasta da sessão
              --so-sistema        não grava o microfone
              --so-microfone      não grava o áudio do computador
              --wav               deixa em WAV, sem converter para MP3
              --kbps N            taxa do MP3 (padrão: {new AppSettings().Mp3Kbps})
              --titulo TEXTO      nome da sessão

            OPÇÕES DE `importar`
              --motor whisper|windows|remoto|nenhum   quem transcreve (padrão: whisper)
              --idioma auto|en|pt-BR   idioma da fala (padrão: detecta)
              --modelo base|small|medium   modelo do whisper (padrão: base)
              --titulo TEXTO   --sem-quadros   --sem-transcricao   --sem-traducao   --resumir

            A configuração completa fica em {Path.Combine(AppInfo.PastaDados, "config.json")}
            e é a mesma que a janela do Gravador usa.
            """);
        return 0;
    }

    // ==================================================================

    /// <summary>
    /// Procura versão nova e, se pedirem, baixa e instala.
    ///
    /// A janela faz o mesmo pelo cartão de Atualização; existir também aqui é o que permite
    /// atualizar uma máquina por script — e é como a consulta ao GitHub se confere sem abrir a
    /// interface.
    /// </summary>
    private static async Task<int> Atualizacao(string[] argv)
    {
        var config = AppSettings.Carregar();
        var baixar = argv.Contains("--baixar");
        var instalar = argv.Contains("--instalar");

        var repo = Array.IndexOf(argv, "--repo");
        if (repo >= 0 && repo + 1 < argv.Length) config.RepositorioDeAtualizacao = argv[repo + 1];
        if (argv.Contains("--sem-previas")) config.IncluirPreReleases = false;

        Console.WriteLine($"Instalada: versão {AppInfo.Versao}");
        Console.WriteLine($"Procurando em https://github.com/{config.RepositorioDeAtualizacao}/releases");

        var nova = await new Atualizador().ProcurarAsync(config, forcar: true);
        if (nova == null)
        {
            Console.WriteLine("Nada mais novo por lá (ou o repositório não respondeu).");
            return 0;
        }

        Console.WriteLine($"Disponível: versão {nova.Versao}  (tag {nova.Tag})");
        Console.WriteLine($"  {nova.UrlDaPagina}");
        if (nova.UrlDoInstalador.Length > 0)
            Console.WriteLine($"  instalador: {Formato.Tamanho(nova.Bytes)}");
        else
            Console.WriteLine("  sem instalador anexado nessa release");

        if (!baixar && !instalar) return 0;
        if (nova.UrlDoInstalador.Length == 0) { Console.Error.WriteLine("Nada para baixar."); return 1; }

        var ultimo = -1;
        var progresso = new Progress<double>(p =>
        {
            var dez = (int)(p * 10);
            if (dez == ultimo) return; // uma linha a cada 10%: barra de progresso em log vira lixo
            ultimo = dez;
            Console.WriteLine($"  baixando… {p * 100:0}%");
        });

        var arquivo = await Atualizador.BaixarAsync(nova, progresso);
        if (arquivo == null) { Console.Error.WriteLine("Não deu para baixar."); return 1; }
        Console.WriteLine($"Baixado em {arquivo}");

        if (!instalar) return 0;

        Console.WriteLine("Instalando em silêncio. O Gravador vai fechar e abrir de volta na bandeja.");
        return Atualizador.Instalar(arquivo) ? 0 : 1;
    }

    private static int Dispositivos()
    {
        Console.WriteLine();
        Console.WriteLine("REPRODUÇÃO (é daqui que sai o \"áudio do computador\")");
        foreach (var d in DeviceCatalog.Reproducao())
            Console.WriteLine($"  {d.Rotulo}\n      {d.TaxaNativa} Hz · {d.CanaisNativos} canal(is)\n      {d.Id}");

        Console.WriteLine();
        Console.WriteLine("CAPTURA (microfones)");
        foreach (var d in DeviceCatalog.Captura())
            Console.WriteLine($"  {d.Rotulo}\n      {d.TaxaNativa} Hz · {d.CanaisNativos} canal(is)\n      {d.Id}");

        Console.WriteLine();
        Console.WriteLine(AudioEncoder.Mp3Disponivel
            ? "Codificador de MP3 do Windows: disponível."
            : "Codificador de MP3 do Windows: AUSENTE — as gravações ficarão em WAV.");
        return 0;
    }

    private static async Task<int> Gravar(string[] argv)
    {
        var config = AppSettings.Carregar();
        double? limite = null;
        string? titulo = null;

        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "--segundos" when i + 1 < argv.Length:
                    limite = double.Parse(argv[++i], CultureInfo.InvariantCulture); break;
                case "--saida" when i + 1 < argv.Length:
                    config.PastaSaida = argv[++i]; break;
                case "--titulo" when i + 1 < argv.Length:
                    titulo = argv[++i]; break;
                case "--kbps" when i + 1 < argv.Length:
                    config.Mp3Kbps = int.Parse(argv[++i], CultureInfo.InvariantCulture); break;
                case "--so-sistema": config.GravarMicrofone = false; break;
                case "--so-microfone": config.GravarSistema = false; break;
                case "--wav": config.Formato = FormatoSaida.Wav; break;
            }
        }
        config.Sanear();

        using var servico = new ServicoDeGravacao(config);
        servico.Aviso += a => Console.Error.WriteLine($"  aviso: {a}");
        servico.Niveis += n => AtualizarPicos(n.Sistema, n.Microfone);

        var sessao = servico.Iniciar(titulo);
        Console.WriteLine();
        Console.WriteLine($"Gravando em {sessao.Pasta}");
        Console.WriteLine($"  computador: {servico.Motor.FormatoSistema ?? "(desligado)"}");
        Console.WriteLine($"  microfone:  {servico.Motor.FormatoMicrofone ?? "(desligado)"}");
        Console.WriteLine(limite is { } s
            ? $"  parando em {s:0} s"
            : "  Enter para parar.");
        Console.WriteLine();

        var interativo = !Console.IsInputRedirected;
        var parar = new CancellationTokenSource();
        if (interativo && limite == null)
            _ = Task.Run(() => { Console.ReadLine(); parar.Cancel(); });

        var pintou = false;
        while (!parar.IsCancellationRequested)
        {
            if (limite is { } max && servico.Decorrido.TotalSeconds >= max) break;
            DesenharLinha(servico);
            pintou = true;
            await Task.Delay(200).ConfigureAwait(false);
        }
        if (pintou) Console.WriteLine();

        Console.WriteLine();
        var progresso = new Progress<string>(e => Console.WriteLine($"  {e}"));
        var resultado = await servico.PararAsync(progresso).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Pronto: {Formato.Duracao(resultado.Duracao)} em {resultado.Sessao.Pasta}");
        foreach (var arquivo in resultado.Sessao.Arquivos.Todos)
        {
            var caminho = Path.Combine(resultado.Sessao.Pasta, arquivo);
            Console.WriteLine($"  {arquivo}  ({Formato.Tamanho(AudioEncoder.TamanhoDe(caminho))})");
        }
        if (resultado.Sessao.QuantidadeDeCapturas > 0)
            Console.WriteLine($"  {resultado.Sessao.QuantidadeDeCapturas} captura(s) de tela");
        foreach (var aviso in resultado.Avisos) Console.WriteLine($"  aviso: {aviso}");
        return 0;
    }

    private static int _ultimoTamanho;

    private static void DesenharLinha(ServicoDeGravacao servico)
    {
        if (Console.IsOutputRedirected) return;
        var mudo = servico.Mudo.Estado;
        var linha = $"  {servico.Decorrido:hh\\:mm\\:ss}  "
            + $"computador {Barra(servico.Motor.FormatoSistema != null ? _picoSistema : 0)}  "
            + $"microfone {Barra(_picoMicrofone)}  "
            + (mudo.Mudo ? $"[{mudo.Descricao}]" : "");

        Console.Write("\r" + linha.PadRight(Math.Max(_ultimoTamanho, linha.Length)));
        _ultimoTamanho = linha.Length;
    }

    private static float _picoSistema, _picoMicrofone;

    private static string Barra(float pico)
    {
        const int largura = 12;
        var cheio = (int)Math.Round(Math.Clamp(pico, 0, 1) * largura);
        return "[" + new string('#', cheio) + new string('.', largura - cheio) + "]";
    }

    // ==================================================================

    private static int Sessoes()
    {
        var config = AppSettings.Carregar();
        var lista = SessaoGravacao.Listar(config.PastaSaidaEfetiva);
        if (lista.Count == 0)
        {
            Console.WriteLine($"Nenhuma gravação em {config.PastaSaidaEfetiva}");
            return 0;
        }

        Console.WriteLine();
        foreach (var s in lista)
        {
            Console.WriteLine($"{s.Inicio.LocalDateTime:dd/MM/yyyy HH:mm}  {Formato.Duracao(s.Duracao),9}  "
                + $"{Formato.Tamanho(s.BytesEmDisco()),9}  {s.Titulo}");
            Console.WriteLine($"    {s.Pasta}");
            var partes = new List<string>();
            if (s.QuantidadeDeCapturas > 0) partes.Add($"{s.QuantidadeDeCapturas} captura(s)");
            if (s.QuantidadeDeMarcadores > 0) partes.Add($"{s.QuantidadeDeMarcadores} marcador(es)");
            if (s.TrechosMudos.Count > 0) partes.Add($"{s.TrechosMudos.Count} trecho(s) mudo(s)");
            if (s.Falas.Count > 0) partes.Add("transcrita");
            if (File.Exists(s.CaminhoResumo)) partes.Add("com resumo");
            if (partes.Count > 0) Console.WriteLine("    " + string.Join(" · ", partes));
        }
        return 0;
    }

    private static async Task<int> Resumir(string[] argv)
    {
        if (argv.Length == 0)
        {
            Console.Error.WriteLine("Informe a pasta da sessão. Use `gravador sessoes` para vê-las.");
            return 2;
        }

        var sessao = SessaoGravacao.Abrir(argv[0]);
        if (sessao == null)
        {
            Console.Error.WriteLine($"Não achei uma sessão em {argv[0]} (falta o sessao.json).");
            return 2;
        }

        var config = AppSettings.Carregar();
        var conta = ContaClaude.Estado(config);
        if (!conta.Conectado)
        {
            Console.Error.WriteLine(conta.Descricao);
            return 3;
        }

        Console.WriteLine(conta.Descricao);
        var resposta = await new Analista()
            .ResumirAsync(sessao, config, new Progress<string>(e => Console.WriteLine($"  {e}")))
            .ConfigureAwait(false);

        if (!resposta.Ok)
        {
            Console.Error.WriteLine($"Não deu: {resposta.Erro}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine(resposta.Texto);
        Console.WriteLine();
        Console.WriteLine($"Gravado em {sessao.CaminhoResumo}");
        return 0;
    }

    private static int Conta()
    {
        var estado = ContaClaude.Estado(AppSettings.Carregar());
        Console.WriteLine();
        Console.WriteLine($"  Meio:          {estado.Meio}");
        Console.WriteLine($"  Conectado:     {(estado.Conectado ? "sim" : "não")}");
        if (estado.Email is { Length: > 0 }) Console.WriteLine($"  Conta:         {estado.Email}");
        if (estado.Organizacao is { Length: > 0 }) Console.WriteLine($"  Organização:   {estado.Organizacao}");
        Console.WriteLine($"  Claude Code:   {(estado.CliInstalado ? ClaudeCli.Localizar() : "não instalado")}");
        Console.WriteLine($"  {estado.Descricao}");
        return estado.Conectado ? 0 : 3;
    }

    private static async Task<int> Entrar(string[] argv)
    {
        var manual = argv.Contains("--manual");
        var (url, ehManual) = ClaudeLogin.Iniciar(manual);

        Console.WriteLine();
        Console.WriteLine("Abra este endereço no navegador e autorize:");
        Console.WriteLine();
        Console.WriteLine("  " + url);
        Console.WriteLine();

        if (!ehManual)
        {
            Console.WriteLine("Esperando o navegador voltar... (Ctrl+C cancela)");
            var pronto = new TaskCompletionSource<string?>();
            ClaudeLogin.Concluiu += (_, erro) => pronto.TrySetResult(erro);
            var erroFinal = await pronto.Task.ConfigureAwait(false);
            if (erroFinal != null)
            {
                Console.Error.WriteLine("Não deu: " + erroFinal);
                return 1;
            }
        }
        else
        {
            Console.Write("Cole aqui o código que o site mostrou: ");
            var colado = Console.ReadLine() ?? "";
            try
            {
                await ClaudeLogin.ConcluirManualAsync(colado).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Não deu: " + ex.Message);
                return 1;
            }
        }

        Console.WriteLine("Conectado.");
        return Conta();
    }

    // ------------------------------------------------------------------

    /// <summary>Alimentado pelo motor para a linha de status ter os medidores.</summary>
    internal static void AtualizarPicos(float sistema, float microfone)
    {
        _picoSistema = sistema;
        _picoMicrofone = microfone;
    }
}
