using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Serialization;

namespace Gravador.Core.Importacao;

public enum TipoDeTrecho
{
    /// <summary>Um slide parado na tela.</summary>
    Slide,

    /// <summary>Vídeo em movimento na área da apresentação (o palestrante, uma demonstração).</summary>
    Video,

    /// <summary>A apresentação não estava na tela: outra janela na frente, outro programa, tela apagada.</summary>
    NaoPertinente,
}

/// <summary>Retângulo em pixels do vídeo ORIGINAL.</summary>
public sealed record Retangulo(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("largura")] int Largura,
    [property: JsonPropertyName("altura")] int Altura)
{
    [JsonIgnore] public (int X, int Y, int Largura, int Altura) Tupla => (X, Y, Largura, Altura);
    [JsonIgnore] public long Area => (long)Largura * Altura;
}

/// <summary>Um intervalo do vídeo com um tipo só de conteúdo.</summary>
/// <param name="Recorte">Onde o conteúdo deste trecho está na tela — o slide, no lugar em que ele estava naquele momento.</param>
public sealed record TrechoDeVideo(
    [property: JsonPropertyName("de")] double DeSegundos,
    [property: JsonPropertyName("ate")] double AteSegundos,
    [property: JsonPropertyName("tipo")] TipoDeTrecho Tipo,
    [property: JsonPropertyName("quadro")] int QuadroRepresentativo,
    [property: JsonPropertyName("arquivo")] string? Arquivo = null,
    [property: JsonPropertyName("recorte")] Retangulo? Recorte = null)
{
    [JsonIgnore] public TimeSpan Duracao => TimeSpan.FromSeconds(Math.Max(0, AteSegundos - DeSegundos));
}

public sealed class ResultadoDaAnalise
{
    [JsonPropertyName("quadros")] public int Quadros { get; set; }
    [JsonPropertyName("quadrosPorSegundo")] public double QuadrosPorSegundo { get; set; }
    [JsonPropertyName("larguraOriginal")] public int LarguraOriginal { get; set; }
    [JsonPropertyName("alturaOriginal")] public int AlturaOriginal { get; set; }

    /// <summary>A janela da apresentação: tudo o que muda ao longo do tempo, nos quadros pertinentes.</summary>
    [JsonPropertyName("retanguloDoConteudo")] public Retangulo? RetanguloDoConteudo { get; set; }

    /// <summary>O recorte de slide mais frequente. Cada trecho carrega o seu; este é o típico, para a interface.</summary>
    [JsonPropertyName("retanguloDoSlide")] public Retangulo? RetanguloDoSlide { get; set; }

    [JsonPropertyName("trechos")] public List<TrechoDeVideo> Trechos { get; set; } = new();

    [JsonPropertyName("segundosNaoPertinentes")] public double SegundosNaoPertinentes { get; set; }
    [JsonPropertyName("slides")] public int Slides { get; set; }

    /// <summary>Anotações de calibração — o que os limiares viram. Para quem for ajustar depois.</summary>
    [JsonPropertyName("diagnostico")] public Dictionary<string, double> Diagnostico { get; set; } = new();
}

/// <summary>
/// Reduz milhares de quadros amostrados a uma lista de slides e trechos, sem nenhum modelo por cima.
///
/// A ideia que organiza tudo: cada região da tela tem uma ASSINATURA TEMPORAL, e é ela que diz o
/// que a região é — não o conteúdo. A moldura do navegador e o desktop nunca mudam. O slide muda
/// raro, de golpe, e fica parado dezenas de segundos. O vídeo do palestrante e as legendas mudam o
/// tempo todo. E "você navegando por cima" muda TUDO ao mesmo tempo, inclusive o que nunca muda.
///
/// O slide não tem um lugar fixo. Foi a lição de um webinar de 48 minutos em que o painel do slide
/// trocava de lado e de tamanho com o do palestrante, e enquetes apareciam por cima da webcam: um
/// retângulo único para o vídeo inteiro achava a região das enquetes, que muda de golpe como um
/// slide. O que funciona é achar o retângulo POR TROCA: no instante em que muitos pixels viram de
/// uma vez, a caixa do que virou é o slide, onde ele estiver naquele momento. Cada trecho carrega o
/// seu recorte.
///
/// Por que a mediana e não a média: a mediana por pixel dá o layout DOMINANTE mesmo com 10% dos
/// quadros mostrando outra coisa — a média borraria o IDE por cima do slide. É o que permite achar
/// "a apresentação" sem saber antes onde ela está, e depois medir cada quadro contra ela.
///
/// Por que este passo existe em vez de mandar os quadros para a IA: 2913 quadros é 2913 imagens em
/// tokens para descobrir que 2870 são iguais. Aqui o funil vai de 2913 para umas dezenas por
/// conta, e a IA recebe os slides, não a tarefa de achá-los.
/// </summary>
public static class AnaliseDeQuadros
{
    /// <summary>Largura da grade de análise. 160 colunas em 2560 px de tela = 16 px por célula, o que basta para achar retângulos.</summary>
    private const int LarguraAnalise = 160;

    // Limiares, em níveis de cinza de 0 a 255. Calibrados numa apresentação de 48 min (ON24, slide +
    // palestrante + legendas, layout variável) com cinco minutos de outras janelas por cima — ver
    // docs/arquitetura.md.
    private const double DiferencaQuePixelMudou = 24;      // |delta| acima disto conta como "o pixel mudou"
    private const double FrequenciaDeMoldura = 0.02;       // muda em menos de 2% dos quadros: moldura/desktop
    private const double IgualAMediana = 8;                // |delta| até aqui conta como "igual ao layout dominante"
    private const double FracaoDeMoldura = 0.85;           // igual à mediana em 85% dos quadros: moldura (tolera 15% de outra janela)
    private const double MolduraEstruturada = 24;          // moldura com mediana acima disto: aba, cabeçalho, logo — preto sobre preto não conta
    private const double PixelDeMolduraAlterado = 16;      // |delta| na moldura que conta como "isto não é a apresentação"
    private const double FracaoDeMolduraNaoPertinente = 0.30; // fração da moldura estruturada alterada que denuncia outra página/janela
    private const double MediaDeMolduraNaoPertinente = 16; // ou a moldura inteira ligeiramente diferente
    private const double QuadroApagado = 12;               // média de cinza abaixo disto: tela preta
    private const double DesvioMinimoDeConteudo = 6;       // abaixo disto o pixel é considerado imóvel
    private const double TrocaMinima = 0.08;               // fração do conteúdo que muda num quadro para ele contar como troca
    private const double AreaMinimaDeSlide = 0.06;         // caixa da troca menor que 6% da tela é enquete/notificação, não slide
    private const double LinhaMudou = 8;                   // diferença média de uma linha para contar como mudada
    private const double FracaoDeLinhasEstavel = 0.10;     // dentro do recorte, menos de 10% das linhas mudando: parado
    private const double FracaoDeQuadrosParadosParaSlide = 0.70; // trecho parado em 70% dos quadros é slide; senão é vídeo
    private const double DuracaoMinimaDeSlideSegundos = 3;
    private const double MesmoSlideFracao = 0.12;          // representantes com menos de 12% das linhas diferentes são o mesmo slide
    private const double VideoCurtoSegundos = 4;           // vídeo curto entre slides é a animação da troca
    private const double NaoPertinenteCurtoSegundos = 3;   // sumiço de 1-2 s da moldura é notificação passando, não outra janela

    public static ResultadoDaAnalise Analisar(string pastaMiniaturas, double quadrosPorSegundo, int larguraOriginal, int alturaOriginal,
        IProgress<double>? progresso = null, CancellationToken ct = default)
    {
        var arquivos = Directory.GetFiles(pastaMiniaturas, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var r = new ResultadoDaAnalise
        {
            Quadros = arquivos.Length,
            QuadrosPorSegundo = quadrosPorSegundo,
            LarguraOriginal = larguraOriginal,
            AlturaOriginal = alturaOriginal,
        };
        if (arquivos.Length < 3) return r;

        // ---- 1. carrega tudo em cinza, na grade de análise ----
        var (quadros, w, h) = Carregar(arquivos, progresso, ct);
        var n = quadros.Length;
        var pixels = w * h;
        double Segundos(int i) => i / quadrosPorSegundo;
        var escala = (double)larguraOriginal / w;

        // ---- 2. mediana por pixel: o layout dominante ----
        var mediana = MedianaPorPixel(quadros, pixels);

        // ---- 3. frequência de mudança por pixel ----
        var frequencia = new double[pixels];
        for (var i = 1; i < n; i++)
        {
            var a = quadros[i - 1]; var b = quadros[i];
            for (var p = 0; p < pixels; p++)
                if (Math.Abs(a[p] - b[p]) > DiferencaQuePixelMudou) frequencia[p] += 1;
        }
        for (var p = 0; p < pixels; p++) frequencia[p] /= n - 1;

        // ---- 4. moldura = o que é igual ao layout dominante quase o tempo todo ----
        // Pela FRAÇÃO de quadros iguais à mediana, e não pelo desvio médio: os quadros com outra janela
        // por cima inflam o desvio justamente dos pixels que os denunciariam (a barra de abas vira
        // barra do IDE), e pelo desvio médio esses pixels saíam da moldura.
        var iguais = new int[pixels];
        foreach (var q in quadros)
            for (var p = 0; p < pixels; p++)
                if (Math.Abs(q[p] - mediana[p]) <= IgualAMediana) iguais[p]++;
        var moldura = new bool[pixels];
        var qtdMoldura = 0;
        for (var p = 0; p < pixels; p++)
            if ((double)iguais[p] / n >= FracaoDeMoldura && frequencia[p] < FrequenciaDeMoldura) { moldura[p] = true; qtdMoldura++; }

        // Só a moldura ESTRUTURADA vota: abas, barra de endereço, cabeçalho da página, logo. O desktop
        // preto é moldura também, mas preto sobre preto não diz nada — e diluía a fração quando uma
        // busca no Google tomava a mesma janela do navegador: a moldura do Chrome é igual, o que some
        // é o cabeçalho da apresentação.
        var estruturada = new bool[pixels];
        var qtdEstruturada = 0;
        for (var p = 0; p < pixels; p++)
            if (moldura[p] && mediana[p] > MolduraEstruturada) { estruturada[p] = true; qtdEstruturada++; }
        if (qtdEstruturada < 200) { estruturada = moldura; qtdEstruturada = qtdMoldura; }

        var pertinente = new bool[n];
        var descasamentos = new double[n];
        for (var i = 0; i < n; i++)
        {
            double soma = 0, somaTotal = 0;
            var alterados = 0;
            var q = quadros[i];
            for (var p = 0; p < pixels; p++)
            {
                somaTotal += q[p];
                if (!estruturada[p]) continue;
                var d = Math.Abs(q[p] - mediana[p]);
                soma += d;
                if (d > PixelDeMolduraAlterado) alterados++;
            }
            var media = qtdEstruturada > 0 ? soma / qtdEstruturada : 0;
            var fracao = qtdEstruturada > 0 ? (double)alterados / qtdEstruturada : 0;
            var apagado = somaTotal / pixels < QuadroApagado;
            descasamentos[i] = Math.Max(media, fracao * 100);
            pertinente[i] = !apagado && fracao < FracaoDeMolduraNaoPertinente && media < MediaDeMolduraNaoPertinente;
        }
        // um sumiço de um ou dois quadros no meio de um trecho pertinente é notificação passando
        SuavizarCurtos(pertinente, (int)Math.Ceiling(NaoPertinenteCurtoSegundos * quadrosPorSegundo));

        // ---- 5. nos quadros pertinentes: a janela da apresentação ----
        var pert = quadros.Where((_, i) => pertinente[i]).ToArray();
        var medianaP = MedianaPorPixel(pert, pixels);
        var desvP = DesvioAbsolutoMedio(pert, medianaP, pixels);
        var conteudoMask = new bool[pixels];
        var qtdConteudo = 0;
        for (var p = 0; p < pixels; p++) if (desvP[p] >= DesvioMinimoDeConteudo) { conteudoMask[p] = true; qtdConteudo++; }
        var conteudo = MaiorBloco(conteudoMask, w, h, fracaoMinima: 0.04, folga: 2) ?? new Rectangle(0, 0, w, h);
        r.RetanguloDoConteudo = Escalar(conteudo, escala, larguraOriginal, alturaOriginal);

        // ---- 6. as trocas: quadros em que muitos pixels do conteúdo viram de uma vez ----
        // A caixa do que virou é o slide, onde ele estiver naquele momento. Caixa pequena é enquete
        // ou notificação: não abre trecho novo.
        var trocas = new List<(int Quadro, Rectangle Caixa)>();
        var mudancas = new bool[pixels];
        for (var i = 1; i < n; i++)
        {
            if (!pertinente[i] || !pertinente[i - 1]) continue;
            var a = quadros[i - 1]; var b = quadros[i];
            var mudados = 0;
            for (var p = 0; p < pixels; p++)
            {
                mudancas[p] = conteudoMask[p] && Math.Abs(a[p] - b[p]) > DiferencaQuePixelMudou;
                if (mudancas[p]) mudados++;
            }
            if (qtdConteudo == 0 || (double)mudados / qtdConteudo < TrocaMinima) continue;

            // Slide branco sobre slide branco: só o texto muda, e o texto tem buracos. Folga grande
            // nos perfis para a caixa cobrir o slide inteiro e não só o parágrafo mais cheio.
            var caixa = MaiorBloco(mudancas, w, h, fracaoMinima: 0.05, folga: 10);
            if (caixa is not { } c) continue;
            if ((double)c.Width * c.Height / pixels < AreaMinimaDeSlide) continue;
            trocas.Add((i, Crescer(c, 1, w, h)));
        }
        r.Diagnostico["trocas"] = trocas.Count;

        // ---- 7. trechos: entre trocas e fronteiras de pertinência, cada um com o seu recorte ----
        var fronteiras = new SortedSet<int> { 0, n };
        foreach (var t in trocas) fronteiras.Add(t.Quadro);
        for (var i = 1; i < n; i++) if (pertinente[i] != pertinente[i - 1]) fronteiras.Add(i);

        var caixaPorTroca = trocas.ToDictionary(t => t.Quadro, t => t.Caixa);
        var trechos = new List<TrechoDeVideo>();
        var recortes = new List<Rectangle?>();
        var limites = fronteiras.ToList();
        var recorteAtual = conteudo;

        for (var k = 0; k + 1 < limites.Count; k++)
        {
            var de = limites[k]; var ate = limites[k + 1];
            if (ate <= de) continue;
            if (caixaPorTroca.TryGetValue(de, out var caixa)) recorteAtual = caixa;

            if (!pertinente[de])
            {
                trechos.Add(new TrechoDeVideo(Segundos(de), Segundos(ate), TipoDeTrecho.NaoPertinente, de + (ate - de) / 2));
                recortes.Add(null);
                continue;
            }

            // Parado ou em movimento, DENTRO do recorte deste trecho. Fração de linhas mudadas, não
            // diferença média: legenda e cursor mudam poucas linhas; vídeo muda muitas.
            var parados = 0;
            for (var i = de + 1; i < ate; i++)
                if (FracaoDeLinhasMudadas(quadros[i - 1], quadros[i], recorteAtual, w) < FracaoDeLinhasEstavel) parados++;
            var fracaoParada = ate - de > 1 ? (double)parados / (ate - de - 1) : 1;
            var tipo = fracaoParada >= FracaoDeQuadrosParadosParaSlide ? TipoDeTrecho.Slide : TipoDeTrecho.Video;

            // O representante é o penúltimo quadro, não o do meio: um slide que se constrói (um
            // marcador de cada vez) está completo no fim, e o último quadro pode já ser a transição.
            var representativo = Math.Max(de, ate - 2);
            trechos.Add(new TrechoDeVideo(Segundos(de), Segundos(ate), tipo, representativo));
            recortes.Add(recorteAtual);
        }

        // ---- 8. limpeza, sempre entre vizinhos adjacentes (nunca gera sobreposição) ----
        bool MesmoSlide(int i, int j)
        {
            var a = trechos[i]; var b = trechos[j];
            var area = Rectangle.Union(recortes[i] ?? conteudo, recortes[j] ?? conteudo);
            return FracaoDeLinhasMudadas(quadros[a.QuadroRepresentativo], quadros[b.QuadroRepresentativo], area, w) < MesmoSlideFracao;
        }
        void Fundir(int i)
        {
            var j = i + 1;
            trechos[i] = trechos[i] with
            {
                AteSegundos = trechos[j].AteSegundos,
                QuadroRepresentativo = Math.Max(trechos[i].QuadroRepresentativo, trechos[j].QuadroRepresentativo),
            };
            if (recortes[i] is { } ra && recortes[j] is { } rb) recortes[i] = Rectangle.Union(ra, rb);
            trechos.RemoveAt(j);
            recortes.RemoveAt(j);
        }

        // a) slide curto demais é transição
        for (var i = 0; i < trechos.Count; i++)
            if (trechos[i].Tipo == TipoDeTrecho.Slide && trechos[i].Duracao.TotalSeconds < DuracaoMinimaDeSlideSegundos)
                trechos[i] = trechos[i] with { Tipo = TipoDeTrecho.Video };

        // b) funde vizinhos iguais e "slide, vídeo curto, o mesmo slide"
        bool mudou;
        do
        {
            mudou = false;
            for (var i = 0; i + 1 < trechos.Count; i++)
            {
                var a = trechos[i]; var b = trechos[i + 1];
                if (a.Tipo == b.Tipo && (a.Tipo != TipoDeTrecho.Slide || MesmoSlide(i, i + 1)))
                {
                    Fundir(i); mudou = true; break;
                }
                if (i + 2 < trechos.Count && a.Tipo == TipoDeTrecho.Slide && b.Tipo == TipoDeTrecho.Video
                    && trechos[i + 2].Tipo == TipoDeTrecho.Slide && b.Duracao.TotalSeconds <= VideoCurtoSegundos * 3
                    && MesmoSlide(i, i + 2))
                {
                    Fundir(i); Fundir(i); mudou = true; break;
                }
            }
        } while (mudou);

        // c) vídeo curto (animação da troca) some no vizinho: no slide anterior, senão no seguinte
        for (var i = 0; i < trechos.Count; i++)
        {
            if (trechos[i].Tipo != TipoDeTrecho.Video || trechos[i].Duracao.TotalSeconds > VideoCurtoSegundos) continue;
            if (i > 0 && trechos[i - 1].Tipo == TipoDeTrecho.Slide)
            {
                trechos[i - 1] = trechos[i - 1] with { AteSegundos = trechos[i].AteSegundos };
                trechos.RemoveAt(i); recortes.RemoveAt(i); i--;
            }
            else if (i + 1 < trechos.Count && trechos[i + 1].Tipo == TipoDeTrecho.Slide)
            {
                trechos[i + 1] = trechos[i + 1] with { DeSegundos = trechos[i].DeSegundos };
                trechos.RemoveAt(i); recortes.RemoveAt(i); i--;
            }
        }

        // ---- 9. saída: cada trecho com o recorte em pixels do vídeo original ----
        for (var i = 0; i < trechos.Count; i++)
            trechos[i] = trechos[i] with { Recorte = recortes[i] is { } rc ? Escalar(rc, escala, larguraOriginal, alturaOriginal) : null };

        r.Trechos = trechos;
        r.Slides = trechos.Count(t => t.Tipo == TipoDeTrecho.Slide);
        r.SegundosNaoPertinentes = trechos.Where(t => t.Tipo == TipoDeTrecho.NaoPertinente).Sum(t => t.Duracao.TotalSeconds);
        r.RetanguloDoSlide = RecorteTipico(trechos.Where(t => t.Tipo == TipoDeTrecho.Slide && t.Recorte != null).Select(t => t.Recorte!).ToList());

        r.Diagnostico["quadrosPertinentes"] = pertinente.Count(p => p);
        r.Diagnostico["pixelsDeMoldura"] = qtdMoldura;
        r.Diagnostico["pixelsDeMolduraEstruturada"] = qtdEstruturada;
        r.Diagnostico["pixelsDeConteudo"] = qtdConteudo;
        r.Diagnostico["descasamentoMediano"] = Mediana(descasamentos);
        r.Diagnostico["gradeLargura"] = w;
        r.Diagnostico["gradeAltura"] = h;

        progresso?.Report(1);
        return r;
    }

    // ==================================================================

    private static (byte[][] Quadros, int W, int H) Carregar(string[] arquivos, IProgress<double>? progresso, CancellationToken ct)
    {
        var quadros = new byte[arquivos.Length][];
        int w = 0, h = 0;

        for (var i = 0; i < arquivos.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var origem = new Bitmap(arquivos[i]);
            if (w == 0)
            {
                w = LarguraAnalise;
                h = Math.Max(1, (int)Math.Round(origem.Height * (double)LarguraAnalise / origem.Width));
            }
            quadros[i] = ParaCinza(origem, w, h);
            if (progresso != null && i % 50 == 0) progresso.Report(0.6 * i / arquivos.Length);
        }
        return (quadros, w, h);
    }

    private static byte[] ParaCinza(Bitmap origem, int w, int h)
    {
        using var pequeno = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(pequeno))
        {
            // bilinear: rápido, e a média de área é o que se quer para achar retângulos
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(origem, new Rectangle(0, 0, w, h));
        }

        var dados = pequeno.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var saida = new byte[w * h];
            var linha = new byte[dados.Stride];
            for (var y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(dados.Scan0 + y * dados.Stride, linha, 0, dados.Stride);
                for (var x = 0; x < w; x++)
                {
                    var b = linha[x * 3]; var gg = linha[x * 3 + 1]; var rr = linha[x * 3 + 2];
                    saida[y * w + x] = (byte)((rr * 299 + gg * 587 + b * 114) / 1000);
                }
            }
            return saida;
        }
        finally
        {
            pequeno.UnlockBits(dados);
        }
    }

    /// <summary>Mediana por pixel via histograma: O(quadros × pixels), sem ordenar nada.</summary>
    private static byte[] MedianaPorPixel(byte[][] quadros, int pixels)
    {
        var mediana = new byte[pixels];
        if (quadros.Length == 0) return mediana;
        var hist = new int[256];
        var metade = quadros.Length / 2;
        for (var p = 0; p < pixels; p++)
        {
            Array.Clear(hist);
            foreach (var q in quadros) hist[q[p]]++;
            var acumulado = 0;
            for (var v = 0; v < 256; v++)
            {
                acumulado += hist[v];
                if (acumulado > metade) { mediana[p] = (byte)v; break; }
            }
        }
        return mediana;
    }

    private static double[] DesvioAbsolutoMedio(byte[][] quadros, byte[] referencia, int pixels)
    {
        var desvio = new double[pixels];
        if (quadros.Length == 0) return desvio;
        foreach (var q in quadros)
            for (var p = 0; p < pixels; p++) desvio[p] += Math.Abs(q[p] - referencia[p]);
        for (var p = 0; p < pixels; p++) desvio[p] /= quadros.Length;
        return desvio;
    }

    /// <summary>Vira "verdadeiro" corridas de falso mais curtas que <paramref name="maximo"/> entre dois verdadeiros.</summary>
    private static void SuavizarCurtos(bool[] serie, int maximo)
    {
        var n = serie.Length;
        var i = 0;
        while (i < n)
        {
            if (serie[i]) { i++; continue; }
            var j = i;
            while (j < n && !serie[j]) j++;
            if (i > 0 && j < n && j - i <= maximo)
                for (var k = i; k < j; k++) serie[k] = true;
            i = j;
        }
    }

    /// <summary>
    /// A maior caixa contígua de uma máscara, pelos perfis de linha e coluna.
    ///
    /// Perfil e não componente conexo porque o alvo é um retângulo de tela (janela, slide), e o
    /// perfil ignora buracos internos — um slide com fundo branco tem o miolo "parado" e só as
    /// bordas do texto mudando; o componente conexo o picotaria. A <paramref name="folga"/> é o
    /// buraco tolerado no perfil, em células.
    /// </summary>
    private static Rectangle? MaiorBloco(bool[] mask, int w, int h, double fracaoMinima, int folga)
    {
        var colunas = new int[w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (mask[y * w + x]) colunas[x]++;

        var faixaX = MaiorCorrida(colunas, (int)Math.Ceiling(h * fracaoMinima), folga);
        if (faixaX == null) return null;

        // as linhas são medidas SÓ dentro da faixa de colunas achada: senão um widget alto e
        // estreito fora do slide esticaria a caixa
        var linhas = new int[h];
        for (var y = 0; y < h; y++)
            for (var x = faixaX.Value.Inicio; x < faixaX.Value.Fim; x++)
                if (mask[y * w + x]) linhas[y]++;
        var faixaY = MaiorCorrida(linhas, (int)Math.Ceiling((faixaX.Value.Fim - faixaX.Value.Inicio) * fracaoMinima), folga);
        if (faixaY == null) return null;

        return new Rectangle(faixaX.Value.Inicio, faixaY.Value.Inicio,
            faixaX.Value.Fim - faixaX.Value.Inicio, faixaY.Value.Fim - faixaY.Value.Inicio);
    }

    /// <summary>Maior corrida de posições com contagem ≥ mínimo, tolerando buracos de até <paramref name="folga"/> células.</summary>
    private static (int Inicio, int Fim)? MaiorCorrida(int[] perfil, int minimo, int folga)
    {
        minimo = Math.Max(1, minimo);
        (int Inicio, int Fim)? melhor = null;
        var inicio = -1;
        var ultimoAtivo = -1;
        for (var i = 0; i < perfil.Length; i++)
        {
            if (perfil[i] < minimo) continue;
            if (inicio < 0 || i - ultimoAtivo - 1 > folga)
            {
                if (inicio >= 0 && (melhor == null || ultimoAtivo + 1 - inicio > melhor.Value.Fim - melhor.Value.Inicio))
                    melhor = (inicio, ultimoAtivo + 1);
                inicio = i;
            }
            ultimoAtivo = i;
        }
        if (inicio >= 0 && (melhor == null || ultimoAtivo + 1 - inicio > melhor.Value.Fim - melhor.Value.Inicio))
            melhor = (inicio, ultimoAtivo + 1);
        return melhor;
    }

    /// <summary>Fração das linhas do retângulo cuja diferença média passou de <see cref="LinhaMudou"/>.</summary>
    private static double FracaoDeLinhasMudadas(byte[] a, byte[] b, Rectangle area, int w)
    {
        if (area.Height <= 0 || area.Width <= 0) return 0;
        var mudadas = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            double soma = 0;
            for (var x = area.Left; x < area.Right; x++)
            {
                var p = y * w + x;
                soma += Math.Abs(a[p] - b[p]);
            }
            if (soma / area.Width > LinhaMudou) mudadas++;
        }
        return (double)mudadas / area.Height;
    }

    private static Rectangle Crescer(Rectangle r, int celulas, int w, int h)
    {
        var x = Math.Max(0, r.X - celulas);
        var y = Math.Max(0, r.Y - celulas);
        var x2 = Math.Min(w, r.Right + celulas);
        var y2 = Math.Min(h, r.Bottom + celulas);
        return new Rectangle(x, y, x2 - x, y2 - y);
    }

    /// <summary>Da grade para pixels do vídeo original, com meia célula de folga de cada lado.</summary>
    private static Retangulo Escalar(Rectangle r, double escala, int larguraOriginal, int alturaOriginal)
    {
        var x = (int)Math.Floor((r.X - 0.5) * escala);
        var y = (int)Math.Floor((r.Y - 0.5) * escala);
        var x2 = (int)Math.Ceiling((r.Right + 0.5) * escala);
        var y2 = (int)Math.Ceiling((r.Bottom + 0.5) * escala);
        x = Math.Clamp(x, 0, larguraOriginal - 1);
        y = Math.Clamp(y, 0, alturaOriginal - 1);
        x2 = Math.Clamp(x2, x + 1, larguraOriginal);
        y2 = Math.Clamp(y2, y + 1, alturaOriginal);
        return new Retangulo(x, y, x2 - x, y2 - y);
    }

    /// <summary>O recorte que mais se repete entre os slides (por sobreposição), para a interface e para o fallback.</summary>
    private static Retangulo? RecorteTipico(List<Retangulo> recortes)
    {
        if (recortes.Count == 0) return null;
        Retangulo? melhor = null;
        var melhorVotos = -1;
        foreach (var candidato in recortes)
        {
            var votos = recortes.Count(o => Sobreposicao(candidato, o) > 0.7);
            if (votos > melhorVotos) { melhorVotos = votos; melhor = candidato; }
        }
        return melhor;
    }

    private static double Sobreposicao(Retangulo a, Retangulo b)
    {
        var x1 = Math.Max(a.X, b.X); var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Largura, b.X + b.Largura); var y2 = Math.Min(a.Y + a.Altura, b.Y + b.Altura);
        if (x2 <= x1 || y2 <= y1) return 0;
        double inter = (double)(x2 - x1) * (y2 - y1);
        return inter / (a.Area + b.Area - inter);
    }

    private static double Mediana(double[] v)
    {
        if (v.Length == 0) return 0;
        var c = (double[])v.Clone();
        Array.Sort(c);
        return c[c.Length / 2];
    }
}
