using System.Text.Json;
using System.Text.Json.Serialization;
using Gravador.Core.Importacao;
using Gravador.Core.Settings;

namespace Gravador.Core.Session;

/// <summary>Os arquivos de áudio que a sessão produziu.</summary>
public sealed class ArquivosDaSessao
{
    [JsonPropertyName("sistema")] public string? Sistema { get; set; }
    [JsonPropertyName("microfone")] public string? Microfone { get; set; }
    [JsonPropertyName("mixado")] public string? Mixado { get; set; }

    [JsonIgnore]
    public IEnumerable<string> Todos =>
        new[] { Sistema, Microfone, Mixado }.Where(a => !string.IsNullOrEmpty(a))!;
}

public enum OrigemDaSessao
{
    /// <summary>Gravada pelo Gravador, ao vivo.</summary>
    Gravada,

    /// <summary>Importada de um arquivo de áudio ou vídeo que já existia.</summary>
    Importada,
}

/// <summary>
/// Uma gravação: a pasta em disco, a linha do tempo e o que sobrou dela.
///
/// A pasta é a unidade. Tudo o que a sessão produz mora dentro dela — áudio, imagens, transcrição,
/// resumo e o <c>sessao.json</c> que amarra as três coisas com carimbos de tempo. É esse pacote que
/// se arrasta inteiro para dentro de uma IA depois, e é por isso que a pasta é auto-suficiente: sem
/// caminho absoluto para fora, sem depender do banco de nada.
///
/// A exceção consciente é a importação: o vídeo original NÃO é copiado para dentro. Ele pesa
/// centenas de megabytes e o valor dele já foi extraído (áudio e quadros); o que fica é o caminho,
/// o tamanho e a data, para reconhecê-lo. A pasta continua auto-suficiente para a IA, que é o que
/// importa.
///
/// O <c>sessao.json</c> é regravado a cada marca, não só no fim. Uma reunião de duas horas que
/// termina em tela azul precisa deixar a linha do tempo até ali.
/// </summary>
public sealed class SessaoGravacao
{
    private readonly object _trava = new();
    private readonly List<Marca> _marcas = new();
    private readonly List<TrechoMudo> _trechosMudos = new();
    private readonly List<TrechoFalado> _falas = new();
    private readonly List<Capitulo> _capitulos = new();
    private readonly List<TrechoDeVideo> _trechosDeVideo = new();

    private double? _mudoAbertoEm;
    private string _mudoAbertoOrigem = "";
    private bool _mudoAbertoConfirmado;

    private SessaoGravacao(string pasta, string id, DateTimeOffset inicio)
    {
        Pasta = pasta;
        Id = id;
        Inicio = inicio;
        Titulo = $"Gravação de {inicio.LocalDateTime:dd/MM/yyyy HH:mm}";
    }

    public string Id { get; private set; }
    public string Pasta { get; private set; }
    public DateTimeOffset Inicio { get; private set; }
    public string Titulo { get; set; }
    public TimeSpan Duracao { get; set; }
    public ArquivosDaSessao Arquivos { get; } = new();

    public OrigemDaSessao Origem { get; set; } = OrigemDaSessao.Gravada;

    /// <summary>Caminho do arquivo importado, quando <see cref="Origem"/> é Importada.</summary>
    public string? ArquivoOriginal { get; set; }
    public long? BytesDoOriginal { get; set; }

    /// <summary>Idioma da fala ("en", "pt-BR"). Vazio enquanto não se sabe.</summary>
    public string Idioma { get; set; } = "";

    /// <summary>Idioma da tradução em <see cref="CaminhoTraducao"/>, se ela existir.</summary>
    public string? IdiomaDaTraducao { get; set; }

    /// <summary>Id da conversa com o Claude sobre esta sessão, para retomar de onde parou.</summary>
    public string? ConversaId { get; set; }

    /// <summary>Subpasta com as imagens. Criada só quando a primeira captura acontece.</summary>
    public string PastaCapturas => Path.Combine(Pasta, "capturas");

    /// <summary>Miniaturas de análise da importação — material de trabalho, apagado por padrão.</summary>
    public string PastaMiniaturas => Path.Combine(Pasta, "miniaturas");

    public string CaminhoJson => Path.Combine(Pasta, "sessao.json");
    public string CaminhoResumo => Path.Combine(Pasta, "resumo.md");
    public string CaminhoTranscricao => Path.Combine(Pasta, "transcricao.md");
    public string CaminhoTraducao => Path.Combine(Pasta, "traducao.md");
    public string CaminhoConversa => Path.Combine(Pasta, "conversa.md");
    public string CaminhoAnalise => Path.Combine(Pasta, "analise.json");

    public IReadOnlyList<Marca> Marcas { get { lock (_trava) return _marcas.ToList(); } }
    public IReadOnlyList<TrechoMudo> TrechosMudos { get { lock (_trava) return _trechosMudos.ToList(); } }
    public IReadOnlyList<TrechoFalado> Falas { get { lock (_trava) return _falas.ToList(); } }
    public IReadOnlyList<Capitulo> Capitulos { get { lock (_trava) return _capitulos.ToList(); } }
    public IReadOnlyList<TrechoDeVideo> TrechosDeVideo { get { lock (_trava) return _trechosDeVideo.ToList(); } }

    public int QuantidadeDeCapturas { get { lock (_trava) return _marcas.Count(m => m.Tipo == TipoDeMarca.Captura); } }
    public int QuantidadeDeMarcadores { get { lock (_trava) return _marcas.Count(m => m.Tipo == TipoDeMarca.Marcador); } }

    public bool TemTranscricao => File.Exists(CaminhoTranscricao) || Falas.Count > 0;
    public bool TemTraducao => File.Exists(CaminhoTraducao);
    public bool TemResumo => File.Exists(CaminhoResumo);

    /// <summary>Disparado a cada marca nova, para a interface acompanhar a linha do tempo.</summary>
    public event Action<Marca>? MarcaAdicionada;

    // ------------------------------------------------------------------

    /// <summary>
    /// Cria a pasta da sessão. O nome carrega a data e a hora porque é o único jeito de a lista de
    /// pastas do Explorer já sair na ordem certa, sem a ferramenta aberta.
    /// </summary>
    public static SessaoGravacao Criar(AppSettings config, string? titulo = null)
    {
        var agora = DateTimeOffset.Now;
        var id = agora.LocalDateTime.ToString("yyyy-MM-dd_HH-mm-ss");
        var raiz = config.PastaSaidaEfetiva;

        var pasta = Path.Combine(raiz, id);
        var sufixo = 1;
        while (Directory.Exists(pasta)) pasta = Path.Combine(raiz, $"{id}_{++sufixo}");
        Directory.CreateDirectory(pasta);

        var s = new SessaoGravacao(pasta, Path.GetFileName(pasta), agora);
        if (!string.IsNullOrWhiteSpace(titulo)) s.Titulo = titulo!;
        s.Adicionar(new Marca(0, TipoDeMarca.Inicio));
        return s;
    }

    /// <summary>
    /// Sessão para um arquivo importado. A pasta ganha o nome do arquivo (e não a hora de agora),
    /// porque a data que interessa é a da apresentação, não a do dia em que você a importou.
    /// </summary>
    public static SessaoGravacao CriarImportada(AppSettings config, string arquivoOriginal, string? titulo = null)
    {
        var info = new FileInfo(arquivoOriginal);
        var quando = info.Exists ? new DateTimeOffset(info.LastWriteTime) : DateTimeOffset.Now;
        var nome = Path.GetFileNameWithoutExtension(arquivoOriginal);
        var id = Limpar($"{quando.LocalDateTime:yyyy-MM-dd}_{nome}");
        var raiz = config.PastaSaidaEfetiva;

        var pasta = Path.Combine(raiz, id);
        var sufixo = 1;
        while (Directory.Exists(pasta)) pasta = Path.Combine(raiz, $"{id}_{++sufixo}");
        Directory.CreateDirectory(pasta);

        var s = new SessaoGravacao(pasta, Path.GetFileName(pasta), quando)
        {
            Origem = OrigemDaSessao.Importada,
            ArquivoOriginal = Path.GetFullPath(arquivoOriginal),
            BytesDoOriginal = info.Exists ? info.Length : null,
            Titulo = string.IsNullOrWhiteSpace(titulo) ? nome : titulo!,
        };
        s.Adicionar(new Marca(0, TipoDeMarca.Inicio));
        return s;
    }

    private static string Limpar(string nome)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        var limpo = new string(nome.Select(c => invalidos.Contains(c) ? '_' : c).ToArray()).Trim();
        return limpo.Length > 80 ? limpo[..80] : limpo;
    }

    public Marca Adicionar(Marca marca)
    {
        lock (_trava) _marcas.Add(marca);
        Salvar();
        MarcaAdicionada?.Invoke(marca);
        return marca;
    }

    public Marca AdicionarMarcador(TimeSpan em, string? texto = null) =>
        Adicionar(new Marca(em.TotalSeconds, TipoDeMarca.Marcador, texto));

    public Marca AdicionarCaptura(TimeSpan em, string arquivo, string? janela = null) =>
        Adicionar(new Marca(em.TotalSeconds, TipoDeMarca.Captura,
            Texto: janela, Arquivo: Path.GetRelativePath(Pasta, arquivo)));

    /// <summary>
    /// Registra a virada do mudo. Abre um trecho quando fica mudo e o fecha quando volta — assim a
    /// linha do tempo tem intervalos, e não só eventos soltos que alguém teria de parear depois.
    /// </summary>
    public void RegistrarMudo(TimeSpan em, bool mudo, string origem, bool confirmado)
    {
        lock (_trava)
        {
            if (mudo)
            {
                if (_mudoAbertoEm != null) return;
                _mudoAbertoEm = em.TotalSeconds;
                _mudoAbertoOrigem = origem;
                _mudoAbertoConfirmado = confirmado;
            }
            else
            {
                if (_mudoAbertoEm is not { } de) return;
                _trechosMudos.Add(new TrechoMudo(de, em.TotalSeconds, _mudoAbertoOrigem, _mudoAbertoConfirmado));
                _mudoAbertoEm = null;
            }
        }
        Adicionar(new Marca(em.TotalSeconds, mudo ? TipoDeMarca.Mudo : TipoDeMarca.Aberto, Detalhe: mudo ? origem : null));
    }

    public void AdicionarFala(TrechoFalado trecho)
    {
        lock (_trava) _falas.Add(trecho);
        // A fala não entra em Marcas: uma reunião gera milhares de trechos e a linha do tempo da
        // interface viraria uma parede. Ela tem seção própria no JSON e arquivo próprio em Markdown.
        Salvar();
    }

    /// <summary>Muitos trechos de uma vez, com um salvamento só — a transcrição de arquivo devolve centenas.</summary>
    public void AdicionarFalas(IEnumerable<TrechoFalado> trechos)
    {
        lock (_trava) _falas.AddRange(trechos);
        Salvar();
    }

    /// <summary>Troca a transcrição inteira (refazer com outro motor).</summary>
    public void SubstituirFalas(IEnumerable<TrechoFalado> trechos)
    {
        lock (_trava)
        {
            _falas.Clear();
            _falas.AddRange(trechos);
        }
        Salvar();
    }

    public Capitulo AdicionarCapitulo(TimeSpan em, string titulo)
    {
        var c = new Capitulo(em.TotalSeconds, titulo.Trim());
        lock (_trava)
        {
            _capitulos.RemoveAll(x => Math.Abs(x.EmSegundos - c.EmSegundos) < 0.5);
            _capitulos.Add(c);
            _capitulos.Sort((a, b) => a.EmSegundos.CompareTo(b.EmSegundos));
        }
        Salvar();
        return c;
    }

    public void DefinirTrechosDeVideo(IEnumerable<TrechoDeVideo> trechos)
    {
        lock (_trava)
        {
            _trechosDeVideo.Clear();
            _trechosDeVideo.AddRange(trechos);
        }
        Salvar();
    }

    /// <summary>Reclassifica um intervalo (o Claude ou você corrigindo a análise automática).</summary>
    public void ReclassificarTrecho(double de, double ate, TipoDeTrecho tipo)
    {
        lock (_trava)
        {
            var novos = new List<TrechoDeVideo>();
            foreach (var t in _trechosDeVideo)
            {
                if (t.AteSegundos <= de || t.DeSegundos >= ate) { novos.Add(t); continue; }
                if (t.DeSegundos < de) novos.Add(t with { AteSegundos = de });
                if (t.AteSegundos > ate) novos.Add(t with { DeSegundos = ate });
            }
            novos.Add(new TrechoDeVideo(de, ate, tipo, -1));
            novos.Sort((a, b) => a.DeSegundos.CompareTo(b.DeSegundos));
            _trechosDeVideo.Clear();
            _trechosDeVideo.AddRange(novos);
        }
        Salvar();
    }

    /// <summary>Fecha um trecho de mudo que tenha ficado aberto quando a gravação parou.</summary>
    public void Encerrar(TimeSpan duracao)
    {
        lock (_trava)
        {
            if (_mudoAbertoEm is { } de)
            {
                _trechosMudos.Add(new TrechoMudo(de, duracao.TotalSeconds, _mudoAbertoOrigem, _mudoAbertoConfirmado));
                _mudoAbertoEm = null;
            }
            Duracao = duracao;
        }
        Adicionar(new Marca(duracao.TotalSeconds, TipoDeMarca.Fim));
    }

    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>O que o <c>sessao.json</c> guarda. Separado da classe viva para o formato ser estável.</summary>
    private sealed class Dto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("titulo")] public string Titulo { get; set; } = "";
        [JsonPropertyName("origem")] public OrigemDaSessao Origem { get; set; }
        [JsonPropertyName("arquivoOriginal")] public string? ArquivoOriginal { get; set; }
        [JsonPropertyName("bytesDoOriginal")] public long? BytesDoOriginal { get; set; }
        [JsonPropertyName("idioma")] public string Idioma { get; set; } = "";
        [JsonPropertyName("idiomaDaTraducao")] public string? IdiomaDaTraducao { get; set; }
        [JsonPropertyName("conversaId")] public string? ConversaId { get; set; }
        [JsonPropertyName("inicio")] public DateTimeOffset Inicio { get; set; }
        [JsonPropertyName("duracaoSegundos")] public double Duracao { get; set; }
        [JsonPropertyName("versaoDoGravador")] public string Versao { get; set; } = "";
        [JsonPropertyName("arquivos")] public ArquivosDaSessao Arquivos { get; set; } = new();
        [JsonPropertyName("marcas")] public List<Marca> Marcas { get; set; } = new();
        [JsonPropertyName("capitulos")] public List<Capitulo> Capitulos { get; set; } = new();
        [JsonPropertyName("trechosMudos")] public List<TrechoMudo> TrechosMudos { get; set; } = new();
        [JsonPropertyName("trechosDeVideo")] public List<TrechoDeVideo> TrechosDeVideo { get; set; } = new();
        [JsonPropertyName("falas")] public List<TrechoFalado> Falas { get; set; } = new();
    }

    public void Salvar()
    {
        try
        {
            Dto dto;
            lock (_trava)
            {
                dto = new Dto
                {
                    Id = Id,
                    Titulo = Titulo,
                    Origem = Origem,
                    ArquivoOriginal = ArquivoOriginal,
                    BytesDoOriginal = BytesDoOriginal,
                    Idioma = Idioma,
                    IdiomaDaTraducao = IdiomaDaTraducao,
                    ConversaId = ConversaId,
                    Inicio = Inicio,
                    Duracao = Duracao.TotalSeconds,
                    Versao = AppInfo.Versao,
                    Arquivos = Arquivos,
                    Marcas = _marcas.ToList(),
                    Capitulos = _capitulos.ToList(),
                    TrechosMudos = _trechosMudos.ToList(),
                    TrechosDeVideo = _trechosDeVideo.ToList(),
                    Falas = _falas.ToList(),
                };
            }
            File.WriteAllText(CaminhoJson, JsonSerializer.Serialize(dto, Json));
        }
        catch
        {
            // não pode derrubar a gravação: o áudio é o que importa, o JSON se refaz
        }
    }

    /// <summary>Relê uma sessão de uma pasta. Devolve null se a pasta não for uma sessão.</summary>
    public static SessaoGravacao? Abrir(string pasta)
    {
        try
        {
            var arquivo = Path.Combine(pasta, "sessao.json");
            if (!File.Exists(arquivo)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(arquivo), Json);
            if (dto == null) return null;

            var s = new SessaoGravacao(pasta, string.IsNullOrEmpty(dto.Id) ? Path.GetFileName(pasta) : dto.Id, dto.Inicio)
            {
                Titulo = string.IsNullOrWhiteSpace(dto.Titulo) ? Path.GetFileName(pasta) : dto.Titulo,
                Duracao = TimeSpan.FromSeconds(dto.Duracao),
                Origem = dto.Origem,
                ArquivoOriginal = dto.ArquivoOriginal,
                BytesDoOriginal = dto.BytesDoOriginal,
                Idioma = dto.Idioma ?? "",
                IdiomaDaTraducao = dto.IdiomaDaTraducao,
                ConversaId = dto.ConversaId,
            };
            s.Arquivos.Sistema = dto.Arquivos.Sistema;
            s.Arquivos.Microfone = dto.Arquivos.Microfone;
            s.Arquivos.Mixado = dto.Arquivos.Mixado;
            s._marcas.AddRange(dto.Marcas);
            s._capitulos.AddRange(dto.Capitulos);
            s._trechosMudos.AddRange(dto.TrechosMudos);
            s._trechosDeVideo.AddRange(dto.TrechosDeVideo);
            s._falas.AddRange(dto.Falas);
            return s;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lista as sessões de uma pasta raiz, da mais nova para a mais velha.</summary>
    public static List<SessaoGravacao> Listar(string raiz)
    {
        var lista = new List<SessaoGravacao>();
        try
        {
            if (!Directory.Exists(raiz)) return lista;
            foreach (var pasta in Directory.EnumerateDirectories(raiz))
                if (Abrir(pasta) is { } s) lista.Add(s);
        }
        catch
        {
            // pasta sem permissão
        }
        lista.Sort((a, b) => b.Inicio.CompareTo(a.Inicio));
        return lista;
    }

    /// <summary>Soma dos arquivos da pasta, para a lista mostrar o peso da sessão.</summary>
    public long BytesEmDisco()
    {
        try
        {
            return new DirectoryInfo(Pasta).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }
}
