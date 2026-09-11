using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gravador.Core.Settings;

/// <summary>Quais arquivos de áudio a sessão deixa no fim.</summary>
public enum ModoTrilhas
{
    /// <summary>Só o arquivo misturado (sistema + microfone juntos).</summary>
    Mixada,

    /// <summary>Só as trilhas separadas — o formato que a IA aproveita melhor.</summary>
    Separadas,

    /// <summary>As duas coisas. Padrão.</summary>
    Ambas,
}

public enum FormatoSaida
{
    /// <summary>PCM sem perda. Enorme: ~345 MB por hora de trilha mono a 48 kHz.</summary>
    Wav,

    /// <summary>MP3 pelo codificador do Media Foundation, que já vem no Windows.</summary>
    Mp3,
}

/// <summary>O que fazer com o áudio do microfone nos trechos em que você está mudo.</summary>
public enum AcaoQuandoMudo
{
    /// <summary>Grava tudo e apenas anota o trecho na linha do tempo.</summary>
    SomenteMarcar,

    /// <summary>A trilha separada guarda tudo; a mixada recebe silêncio. Padrão.</summary>
    SilenciarNaMixagem,

    /// <summary>Silêncio nas duas — nada do que foi dito no mudo sobra.</summary>
    SilenciarEmTudo,
}

public enum AlvoCaptura
{
    JanelaAtiva,
    MonitorAtivo,
    TelaToda,
}

public enum FormatoImagem
{
    Png,
    Jpeg,
}

public enum MotorTranscricao
{
    /// <summary>Nenhuma. A sessão entrega áudio e imagens para você mandar para a IA. Padrão.</summary>
    Nenhum,

    /// <summary>Reconhecimento de fala do próprio Windows: offline, de graça, leve.</summary>
    Windows,

    /// <summary>Serviço remoto compatível com a API de transcrição da OpenAI (Whisper, Groq...).</summary>
    Remoto,

    /// <summary>
    /// whisper.cpp local, baixado sob demanda. Roda depois da gravação, sobre o arquivo — não ao
    /// vivo, porque em máquina modesta ele disputaria a CPU com a reunião. Excelente em inglês, bom
    /// em português, de graça, offline.
    /// </summary>
    Whisper,
}

/// <summary>
/// Quem traduz a legenda ao vivo.
///
/// A CLI do Claude não está aqui, e não por esquecimento: medida numa sessão persistente já
/// aquecida, ela levou ~4 s por frase — atraso demais para legenda — e ainda recusou o papel,
/// tratando a instrução de tradutor como injeção de prompt. Ela continua sendo quem faz o resumo e
/// a tradução da transcrição depois, onde 4 s não custam nada.
/// </summary>
public enum MotorTraducao
{
    /// <summary>
    /// Marian (opus-mt) rodando nesta máquina pelo ONNX Runtime. Offline, sem chave, sem limite e
    /// sem mandar para fora o que foi falado. Padrão.
    /// </summary>
    Marian,

    /// <summary>DeepL. A melhor qualidade em pt-BR; chave gratuita cobre ~500 mil caracteres por mês.</summary>
    DeepL,

    /// <summary>Azure Translator. Chave gratuita cobre 2 milhões de caracteres por mês.</summary>
    Azure,

    /// <summary>Nenhum: a legenda sai no idioma original, sem traduzir.</summary>
    Nenhum,
}

public enum FonteCredencialClaude
{
    /// <summary>Usa o login do Claude Code desta máquina, se existir. Padrão.</summary>
    Automatica,

    /// <summary>Login feito dentro desta ferramenta.</summary>
    LoginNoApp,

    /// <summary>Token colado à mão nas configurações.</summary>
    Manual,
}

/// <summary>
/// Todas as preferências. Persistido em %APPDATA%\Gravador\config.json, legível e editável à mão.
/// </summary>
public sealed class AppSettings
{
    // ---------------- Saída ----------------

    /// <summary>Pasta onde cada sessão ganha sua subpasta. Vazio = Documentos\Gravador.</summary>
    public string PastaSaida { get; set; } = "";

    public ModoTrilhas Trilhas { get; set; } = ModoTrilhas.Ambas;
    public FormatoSaida Formato { get; set; } = FormatoSaida.Mp3;

    /// <summary>Taxa do MP3, por trilha mono. 64 kbps é transparente para fala.</summary>
    public int Mp3Kbps { get; set; } = 64;

    /// <summary>Guardar também o WAV depois de codificar. Custa ~345 MB por hora e por trilha.</summary>
    public bool ManterWav { get; set; }

    // ---------------- Áudio ----------------

    /// <summary>ID do endpoint de reprodução a capturar em loopback. Vazio = o padrão do Windows.</summary>
    public string DispositivoSistema { get; set; } = "";

    /// <summary>ID do endpoint de captura. Vazio = o padrão de comunicações do Windows.</summary>
    public string DispositivoMicrofone { get; set; } = "";

    public bool GravarSistema { get; set; } = true;
    public bool GravarMicrofone { get; set; } = true;

    /// <summary>
    /// 48 000 Hz porque é a taxa nativa de praticamente todo endpoint do Windows: nessa taxa não há
    /// reamostragem nenhuma, que é o caminho mais barato de CPU num notebook modesto.
    /// </summary>
    public int TaxaAmostragem { get; set; } = 48000;

    /// <summary>Mono. Fala não ganha nada com estéreo e o arquivo tem metade do tamanho.</summary>
    public int Canais { get; set; } = 1;

    public double GanhoSistema { get; set; } = 1.0;
    public double GanhoMicrofone { get; set; } = 1.0;

    // ---------------- Microfone mudo ----------------

    public AcaoQuandoMudo QuandoMudo { get; set; } = AcaoQuandoMudo.SilenciarNaMixagem;

    /// <summary>
    /// Vigiar os atalhos de mudo dos aplicativos de reunião (Teams, Meet, Zoom, Discord...).
    ///
    /// É a única forma de saber que você se mutou DENTRO do aplicativo: o Teams não mexe no mudo do
    /// dispositivo, ele só para de enviar o seu áudio — do lado do Windows nada muda. Ver
    /// docs/arquitetura.md, "As quatro perguntas sobre o mudo".
    /// </summary>
    public bool DetectarMudoDeApps { get; set; } = true;

    /// <summary>Combinações extras a vigiar, além das conhecidas.</summary>
    public List<string> AtalhosDeMudoExtras { get; set; } = new();

    /// <summary>Abaixo disto o microfone conta como silêncio, para a linha do tempo de fala.</summary>
    public double LimiarVozDb { get; set; } = -42;

    // ---------------- Capturas de tela ----------------

    public AlvoCaptura AlvoDaCaptura { get; set; } = AlvoCaptura.JanelaAtiva;
    public FormatoImagem FormatoDaCaptura { get; set; } = FormatoImagem.Jpeg;
    public int QualidadeJpeg { get; set; } = 82;

    /// <summary>Reduz o lado maior da imagem para no máximo isto. 0 = tamanho original.</summary>
    public int LarguraMaximaCaptura { get; set; } = 1920;

    /// <summary>Captura sozinho a cada N segundos enquanto grava. 0 = desligado.</summary>
    public int CapturaAutomaticaSegundos { get; set; }

    // ---------------- Atalhos ----------------

    public string AtalhoGravar { get; set; } = "Ctrl+Alt+G";
    public string AtalhoPausar { get; set; } = "Ctrl+Alt+Espaço";
    public string AtalhoCapturar { get; set; } = "Ctrl+Alt+P";

    /// <summary>
    /// N de "nota", e não M de "marcar": Ctrl+Alt+M já vem tomado em boa parte das máquinas
    /// Windows (utilitários de placa de vídeo e de fabricante costumam registrá-lo), e o
    /// RegisterHotKey devolve ERROR_HOTKEY_ALREADY_REGISTERED. Um atalho padrão que não funciona
    /// na primeira execução é pior do que um sem mnemônico perfeito.
    /// </summary>
    public string AtalhoMarcar { get; set; } = "Ctrl+Alt+N";

    // ---------------- Transcrição ----------------

    public MotorTranscricao Transcricao { get; set; } = MotorTranscricao.Nenhum;
    public string IdiomaTranscricao { get; set; } = "pt-BR";

    /// <summary>Transcreve enquanto grava, em vez de só no fim.</summary>
    public bool TranscreverAoVivo { get; set; } = true;

    public string RemotoUrl { get; set; } = "https://api.openai.com/v1/audio/transcriptions";
    public string RemotoModelo { get; set; } = "whisper-1";

    /// <summary>Nome da variável de ambiente com a chave. A chave em si nunca entra neste arquivo.</summary>
    public string RemotoChaveEnv { get; set; } = "OPENAI_API_KEY";

    /// <summary>
    /// Modelo ggml do whisper.cpp: base (147 MB, razoável), small (487 MB, bom), medium (1,5 GB, o
    /// melhor que cabe numa CPU). Baixado sob demanda na primeira transcrição.
    /// </summary>
    public string WhisperModelo { get; set; } = "base";

    /// <summary>Threads do whisper. 0 = metade dos núcleos lógicos, para a máquina continuar usável.</summary>
    public int WhisperThreads { get; set; }

    // ---------------- Legenda ao vivo ----------------

    /// <summary>Mostrar a legenda traduzida numa janela sobre a tela enquanto o áudio toca.</summary>
    public bool LegendaLigada { get; set; }

    /// <summary>
    /// Qual áudio legendar. "sistema" é o certo para assistir a uma apresentação: é a voz que vem
    /// do computador. "microfone" legendaria você mesmo.
    /// </summary>
    public string LegendaFonte { get; set; } = "sistema";

    /// <summary>
    /// Modelo do whisper para a legenda — separado do usado na transcrição final, de propósito.
    ///
    /// Aqui manda o atraso, não a qualidade: medido nesta máquina, o encoder do `tiny` custa 548 ms
    /// a 4 threads contra 1403 ms do `base`. A transcrição final continua podendo usar um modelo
    /// grande, porque lá esperar minutos é aceitável.
    /// </summary>
    public string LegendaModelo { get; set; } = "tiny";

    /// <summary>Idioma FALADO no áudio ("en", "auto"). O destino é <see cref="IdiomaDestino"/>.</summary>
    public string LegendaIdiomaFala { get; set; } = "en";

    /// <summary>Piso do intervalo entre passadas. O real é o maior entre isto e o que a última levou.</summary>
    public int LegendaIntervaloMs { get; set; } = 900;

    /// <summary>Threads do whisper na legenda. 0 = um quarto dos núcleos, para sobrar máquina para a reunião.</summary>
    public int LegendaThreads { get; set; }

    /// <summary>Qual tradutor usar. Ver <see cref="MotorTraducao"/>.</summary>
    public MotorTraducao LegendaTradutor { get; set; } = MotorTraducao.Marian;

    /// <summary>Mostrar também a linha original em inglês, abaixo da tradução.</summary>
    public bool LegendaMostrarOriginal { get; set; }

    /// <summary>Nome da variável de ambiente com a chave do DeepL. A chave nunca entra neste arquivo.</summary>
    public string DeepLChaveEnv { get; set; } = "DEEPL_API_KEY";

    /// <summary>Nome da variável de ambiente com a chave do Azure Translator.</summary>
    public string AzureChaveEnv { get; set; } = "AZURE_TRANSLATOR_KEY";

    /// <summary>Região do recurso do Azure Translator ("brazilsouth", "eastus"). Exigida pela API.</summary>
    public string AzureRegiao { get; set; } = "";

    // ---------------- Tradução ----------------

    /// <summary>Idioma para o qual a transcrição é traduzida quando o original é outro.</summary>
    public string IdiomaDestino { get; set; } = "pt-BR";

    /// <summary>Traduzir automaticamente no pós-processamento quando o idioma detectado difere do destino.</summary>
    public bool TraduzirQuandoIdiomaDiferente { get; set; } = true;

    // ---------------- Importação de vídeo ----------------

    /// <summary>Quadros amostrados por segundo do vídeo, para a análise. 1 é o que basta para slide.</summary>
    public double ImportacaoQuadrosPorSegundo { get; set; } = 1;

    /// <summary>Largura das miniaturas de análise. 640 px é o suficiente para achar cortes e retângulos.</summary>
    public int ImportacaoLarguraMiniatura { get; set; } = 640;

    /// <summary>Largura máxima do quadro final entregue (o slide recortado).</summary>
    public int ImportacaoLarguraQuadroFinal { get; set; } = 1920;

    /// <summary>Recortar os quadros finais no retângulo do conteúdo (a janela da apresentação).</summary>
    public bool ImportacaoRecortarNoConteudo { get; set; } = true;

    /// <summary>Guardar as miniaturas de análise depois de importar. Custa ~60 MB por hora de vídeo.</summary>
    public bool ImportacaoManterMiniaturas { get; set; }

    // ---------------- Claude ----------------

    public FonteCredencialClaude FonteClaude { get; set; } = FonteCredencialClaude.Automatica;
    public string ModeloClaude { get; set; } = "claude-sonnet-5";

    /// <summary>Pede o resumo ao Claude assim que a gravação termina.</summary>
    public bool ResumirAoFinal { get; set; }

    /// <summary>Manda as capturas de tela junto com o texto no pedido de resumo.</summary>
    public bool EnviarCapturasNoResumo { get; set; } = true;

    /// <summary>Instrução do resumo. Vazio usa a de fábrica.</summary>
    public string PromptResumo { get; set; } = "";

    // ---------------- Geral ----------------

    public bool IniciarComWindows { get; set; }
    public bool ComecarMinimizado { get; set; }
    public bool AvisoSonoro { get; set; } = true;

    /// <summary>Fecha para a bandeja em vez de encerrar — a gravação não pode morrer num X sem querer.</summary>
    public bool FecharParaBandeja { get; set; } = true;

    // ---------------- Atualização ----------------

    /// <summary>Procurar versão nova no GitHub ao abrir. A instalação continua sendo sempre um clique seu.</summary>
    public bool VerificarAtualizacoes { get; set; } = true;

    /// <summary>Dono/repositório de onde vêm as releases. Vazio desliga a procura.</summary>
    public string RepositorioDeAtualizacao { get; set; } = "TeteuPower/gravador";

    /// <summary>
    /// Aceitar também a release "latest", que a esteira regera a cada commit na main.
    /// Ligado por padrão: enquanto a ferramenta está sendo construída, é ali que sai a correção.
    /// </summary>
    public bool IncluirPreReleases { get; set; } = true;

    /// <summary>
    /// Última versão sobre a qual o balão da bandeja já avisou. O aviso é uma vez por versão: a
    /// entrada no menu da bandeja continua lá, mas o pop-up não volta a cada abertura.
    /// </summary>
    public string VersaoJaAnunciada { get; set; } = "";

    /// <summary>
    /// Versão que rodou da última vez. Mudou entre uma abertura e outra? Então acabamos de ser
    /// atualizados, e a janela deve aparecer em vez de ficar na bandeja.
    ///
    /// Isto é o que faz a volta da atualização funcionar mesmo quando quem lançou o programa foi um
    /// instalador antigo — ou quando alguém baixou o .exe e rodou na mão. Vazio é primeira execução,
    /// que não é atualização nenhuma.
    /// </summary>
    public string UltimaVersaoExecutada { get; set; } = "";

    // ==================================================================

    [JsonIgnore]
    public string PastaSaidaEfetiva =>
        string.IsNullOrWhiteSpace(PastaSaida) ? AppInfo.PastaSessoesPadrao : PastaSaida;

    [JsonIgnore] public Hotkey HotkeyGravar => Hotkey.Parse(AtalhoGravar);
    [JsonIgnore] public Hotkey HotkeyPausar => Hotkey.Parse(AtalhoPausar);
    [JsonIgnore] public Hotkey HotkeyCapturar => Hotkey.Parse(AtalhoCapturar);
    [JsonIgnore] public Hotkey HotkeyMarcar => Hotkey.Parse(AtalhoMarcar);

    // ------------------------------------------------------------------

    private static string Arquivo => Path.Combine(AppInfo.PastaDados, "config.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Carregar()
    {
        try
        {
            if (File.Exists(Arquivo))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Arquivo), Json);
                if (s != null) { s.Sanear(); return s; }
            }
        }
        catch
        {
            // config quebrada não pode impedir a ferramenta de abrir: cai no padrão
        }
        var novo = new AppSettings();
        novo.Sanear();
        return novo;
    }

    public void Salvar()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.PastaDados);
            File.WriteAllText(Arquivo, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // disco cheio ou pasta sem permissão: a sessão em andamento continua valendo
        }
    }

    /// <summary>Deixa os números dentro do que o resto do código sabe tratar.</summary>
    public void Sanear()
    {
        if (TaxaAmostragem is not (8000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
            TaxaAmostragem = 48000;
        Canais = Canais is 2 ? 2 : 1;
        Mp3Kbps = Math.Clamp(Mp3Kbps, 16, 320);
        QualidadeJpeg = Math.Clamp(QualidadeJpeg, 40, 100);
        LarguraMaximaCaptura = LarguraMaximaCaptura <= 0 ? 0 : Math.Clamp(LarguraMaximaCaptura, 640, 7680);
        CapturaAutomaticaSegundos = CapturaAutomaticaSegundos <= 0 ? 0 : Math.Clamp(CapturaAutomaticaSegundos, 5, 3600);
        GanhoSistema = Math.Clamp(GanhoSistema, 0, 8);
        GanhoMicrofone = Math.Clamp(GanhoMicrofone, 0, 8);
        LimiarVozDb = Math.Clamp(LimiarVozDb, -80, -10);
        if (!GravarSistema && !GravarMicrofone) GravarSistema = true;
        AtalhosDeMudoExtras ??= new List<string>();
        if (WhisperModelo is not ("tiny" or "base" or "small" or "medium" or "large-v3-turbo")) WhisperModelo = "base";
        WhisperThreads = Math.Clamp(WhisperThreads, 0, 64);
        ImportacaoQuadrosPorSegundo = Math.Clamp(ImportacaoQuadrosPorSegundo, 0.2, 5);
        ImportacaoLarguraMiniatura = Math.Clamp(ImportacaoLarguraMiniatura, 320, 1280);
        ImportacaoLarguraQuadroFinal = Math.Clamp(ImportacaoLarguraQuadroFinal, 640, 3840);
        if (string.IsNullOrWhiteSpace(IdiomaDestino)) IdiomaDestino = "pt-BR";

        if (LegendaModelo is not ("tiny" or "base" or "small")) LegendaModelo = "tiny";
        if (!LegendaFonte.Equals("microfone", StringComparison.OrdinalIgnoreCase)) LegendaFonte = "sistema";
        // O piso não pode ser zero: sem ele, uma máquina rápida rodaria passadas em sequência sem
        // folga nenhuma e a legenda comeria um núcleo inteiro à toa.
        LegendaIntervaloMs = Math.Clamp(LegendaIntervaloMs, 300, 5000);
        LegendaThreads = Math.Clamp(LegendaThreads, 0, 64);
        if (string.IsNullOrWhiteSpace(LegendaIdiomaFala)) LegendaIdiomaFala = "en";
    }
}
