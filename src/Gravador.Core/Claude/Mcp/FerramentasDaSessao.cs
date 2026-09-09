using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gravador.Core.Importacao;
using Gravador.Core.Session;

namespace Gravador.Core.Claude.Mcp;

/// <summary>
/// As ferramentas que o Claude recebe para trabalhar numa sessão — e só nela.
///
/// Dois grupos. O primeiro RECRIA as três ferramentas de leitura do Claude Code (<c>Read</c>,
/// <c>Glob</c>, <c>Grep</c>) com a mesma forma de saída que ele conhece, mas trancadas na pasta da
/// sessão: ele lê o que está ali e não lê mais nada. O segundo é o que o Claude Code não tem e a
/// tarefa precisa: a transcrição fatiada por tempo, a busca com carimbo, os slides um a um, e as
/// duas únicas escritas permitidas — nomear um capítulo e reclassificar um trecho.
///
/// A economia é dupla. Cada descrição aqui tem uma linha, contra os milhares de tokens das
/// ferramentas nativas. E a transcrição nunca vai inteira no prompt: ele pede o minuto que quer.
///
/// As imagens saem redimensionadas para 1280 px de largura no máximo. Um slide a 2560 px custa quatro
/// vezes mais tokens de visão e não se lê melhor.
/// </summary>
public static class FerramentasDaSessao
{
    private const int LinhasPorPagina = 2000;
    private const int CaracteresPorFatia = 12000;
    private const int LarguraMaximaDaImagem = 1280;

    public static string Instrucoes(SessaoGravacao sessao) =>
        $"Ferramentas sobre a sessão \"{sessao.Titulo}\" ({Formato.Duracao(sessao.Duracao)}). "
        + "Comece por linha_do_tempo. Peça a transcrição por intervalo em vez de inteira; use buscar para achar um assunto. "
        + "Tempos aceitam segundos ou mm:ss.";

    public static IReadOnlyList<FerramentaMcp> Para(SessaoGravacao sessao)
    {
        var raiz = Path.GetFullPath(sessao.Pasta);
        return
        [
            // ---------------- recriações das nativas ----------------
            new FerramentaMcp("Read",
                "Lê um arquivo da pasta da sessão. Texto vem com número de linha; .jpg/.png vêm como imagem. Use offset/limit em arquivos grandes.",
                ServidorMcp.Esquema(
                    ("arquivo", "string", "Caminho relativo à pasta da sessão (ex.: transcricao.md, capturas/003_slide_12m04s.jpg).", true),
                    ("offset", "integer", "Linha inicial (1 = primeira).", false),
                    ("limit", "integer", $"Quantidade de linhas (padrão {LinhasPorPagina}).", false)),
                (a, _) => Task.FromResult(Read(raiz, a))),

            new FerramentaMcp("Glob",
                "Lista arquivos da sessão que casam com um padrão (ex.: capturas/*.jpg, *.md).",
                ServidorMcp.Esquema(("padrao", "string", "Padrão com * e ?.", true)),
                (a, _) => Task.FromResult(Glob(raiz, a))),

            new FerramentaMcp("Grep",
                "Procura uma expressão regular nos arquivos de texto da sessão. Devolve arquivo:linha: texto.",
                ServidorMcp.Esquema(
                    ("padrao", "string", "Expressão regular (sem distinguir maiúsculas).", true),
                    ("arquivo", "string", "Limita a um arquivo ou padrão (ex.: transcricao.md). Vazio = todos os .md/.json/.txt.", false),
                    ("contexto", "integer", "Linhas de contexto antes e depois (padrão 0).", false)),
                (a, _) => Task.FromResult(Grep(raiz, a))),

            // ---------------- ferramentas da sessão ----------------
            new FerramentaMcp("linha_do_tempo",
                "Visão geral da sessão: duração, idioma, arquivos, capítulos, slides, trechos descartados, trechos com microfone mudo.",
                ServidorMcp.Esquema(),
                (_, _) => Task.FromResult(LinhaDoTempo(sessao))),

            new FerramentaMcp("transcricao",
                $"A transcrição entre dois instantes, uma linha por trecho com carimbo mm:ss. Máximo de {CaracteresPorFatia} caracteres por chamada; a resposta diz onde parou.",
                ServidorMcp.Esquema(
                    ("de", "string", "Início (segundos ou mm:ss). Padrão 0.", false),
                    ("ate", "string", "Fim (segundos ou mm:ss). Padrão: fim da sessão.", false),
                    ("versao", "string", "'original' (padrão) ou 'traducao'.", false)),
                (a, _) => Task.FromResult(Transcricao(sessao, a))),

            new FerramentaMcp("buscar",
                "Procura um termo na transcrição (original e tradução) e devolve os trechos com carimbo de tempo.",
                ServidorMcp.Esquema(
                    ("texto", "string", "Palavra ou expressão. Sem distinguir maiúsculas.", true),
                    ("maximo", "integer", "Máximo de resultados (padrão 20).", false)),
                (a, _) => Task.FromResult(Buscar(sessao, a))),

            new FerramentaMcp("quadros",
                "Lista as imagens da sessão (slides ou capturas), com carimbo, rótulo e id para ver_quadro.",
                ServidorMcp.Esquema(
                    ("de", "string", "Só a partir deste instante.", false),
                    ("ate", "string", "Só até este instante.", false)),
                (a, _) => Task.FromResult(Quadros(sessao, a))),

            new FerramentaMcp("ver_quadro",
                "Devolve uma imagem da sessão pelo id de `quadros` (ou pelo nome do arquivo).",
                ServidorMcp.Esquema(("id", "string", "Id numérico da lista de quadros, ou o nome do arquivo.", true)),
                (a, _) => Task.FromResult(VerQuadro(sessao, a))),

            new FerramentaMcp("definir_capitulo",
                "Nomeia um ponto da linha do tempo como capítulo (substitui se já houver um no mesmo instante).",
                ServidorMcp.Esquema(
                    ("em", "string", "Instante (segundos ou mm:ss).", true),
                    ("titulo", "string", "Título curto do capítulo.", true)),
                (a, _) => Task.FromResult(DefinirCapitulo(sessao, a))),

            new FerramentaMcp("marcar_trecho",
                "Reclassifica um intervalo do vídeo: 'slide', 'video' ou 'nao_pertinente' (para corrigir a análise automática).",
                ServidorMcp.Esquema(
                    ("de", "string", "Início (segundos ou mm:ss).", true),
                    ("ate", "string", "Fim (segundos ou mm:ss).", true),
                    ("tipo", "string", "slide | video | nao_pertinente", true)),
                (a, _) => Task.FromResult(MarcarTrecho(sessao, a))),
        ];
    }

    // ==================================================================

    /// <summary>Resolve um caminho relativo dentro da raiz, recusando qualquer saída dela.</summary>
    private static string? Dentro(string raiz, string? relativo)
    {
        if (string.IsNullOrWhiteSpace(relativo)) return null;
        var limpo = relativo.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var completo = Path.GetFullPath(Path.Combine(raiz, limpo));
        var raizComBarra = raiz.EndsWith(Path.DirectorySeparatorChar) ? raiz : raiz + Path.DirectorySeparatorChar;
        return completo.StartsWith(raizComBarra, StringComparison.OrdinalIgnoreCase) || completo.Equals(raiz, StringComparison.OrdinalIgnoreCase)
            ? completo : null;
    }

    private static bool EhImagem(string caminho) =>
        Path.GetExtension(caminho).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";

    private static ConteudoMcp Read(string raiz, JsonElement a)
    {
        var caminho = Dentro(raiz, ServidorMcp.Str(a, "arquivo"));
        if (caminho == null) return ConteudoMcp.Falha("Caminho fora da pasta da sessão, ou vazio.");
        if (!File.Exists(caminho)) return ConteudoMcp.Falha($"Arquivo não existe: {Path.GetRelativePath(raiz, caminho)}");

        if (EhImagem(caminho)) return Imagem(caminho);

        var offset = (int)Math.Max(1, ServidorMcp.Num(a, "offset") ?? 1);
        var limit = (int)Math.Clamp(ServidorMcp.Num(a, "limit") ?? LinhasPorPagina, 1, 5000);

        var linhas = File.ReadAllLines(caminho);
        var sb = new StringBuilder();
        var fim = Math.Min(linhas.Length, offset - 1 + limit);
        for (var i = offset - 1; i < fim; i++)
            sb.Append(i + 1).Append('\t').AppendLine(linhas[i]);
        if (fim < linhas.Length) sb.AppendLine($"… ({linhas.Length - fim} linhas restantes; continue com offset={fim + 1})");
        if (linhas.Length == 0) sb.AppendLine("(arquivo vazio)");
        return ConteudoMcp.Texto(sb.ToString());
    }

    private static ConteudoMcp Glob(string raiz, JsonElement a)
    {
        var padrao = ServidorMcp.Str(a, "padrao") ?? "*";
        var subpasta = raiz;
        var nome = padrao.Replace('/', Path.DirectorySeparatorChar);
        var barra = nome.LastIndexOf(Path.DirectorySeparatorChar);
        if (barra >= 0)
        {
            subpasta = Dentro(raiz, nome[..barra]) ?? raiz;
            nome = nome[(barra + 1)..];
        }
        if (!Directory.Exists(subpasta)) return ConteudoMcp.Texto("(nenhum arquivo)");

        var arquivos = Directory.EnumerateFiles(subpasta, nome, SearchOption.TopDirectoryOnly)
            .Concat(barra < 0 && padrao.Contains('*') ? Directory.EnumerateFiles(raiz, nome, SearchOption.AllDirectories) : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "miniaturas" + Path.DirectorySeparatorChar))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{Path.GetRelativePath(raiz, f).Replace('\\', '/')}  ({Formato.Tamanho(new FileInfo(f).Length)})")
            .ToList();
        return ConteudoMcp.Texto(arquivos.Count == 0 ? "(nenhum arquivo)" : string.Join("\n", arquivos));
    }

    private static ConteudoMcp Grep(string raiz, JsonElement a)
    {
        var padrao = ServidorMcp.Str(a, "padrao");
        if (string.IsNullOrWhiteSpace(padrao)) return ConteudoMcp.Falha("Informe o padrão.");
        Regex regex;
        try { regex = new Regex(padrao, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return ConteudoMcp.Falha("Expressão inválida: " + ex.Message); }

        var contexto = (int)Math.Clamp(ServidorMcp.Num(a, "contexto") ?? 0, 0, 5);
        var filtro = ServidorMcp.Str(a, "arquivo");
        IEnumerable<string> arquivos = string.IsNullOrWhiteSpace(filtro)
            ? Directory.EnumerateFiles(raiz, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".md" or ".json" or ".txt")
            : Directory.EnumerateFiles(raiz, filtro.Replace('/', Path.DirectorySeparatorChar), SearchOption.TopDirectoryOnly);

        var sb = new StringBuilder();
        var total = 0;
        foreach (var arquivo in arquivos.OrderBy(f => f))
        {
            var linhas = File.ReadAllLines(arquivo);
            var rel = Path.GetRelativePath(raiz, arquivo);
            for (var i = 0; i < linhas.Length && total < 200; i++)
            {
                if (!regex.IsMatch(linhas[i])) continue;
                total++;
                var de = Math.Max(0, i - contexto);
                var ate = Math.Min(linhas.Length - 1, i + contexto);
                for (var j = de; j <= ate; j++)
                    sb.AppendLine($"{rel}:{j + 1}{(j == i ? ":" : "-")} {linhas[j]}");
                if (contexto > 0) sb.AppendLine("--");
            }
        }
        if (total == 0) return ConteudoMcp.Texto("(nenhuma ocorrência)");
        if (total >= 200) sb.AppendLine("… (limite de 200 ocorrências; refine o padrão)");
        return ConteudoMcp.Texto(sb.ToString());
    }

    private static ConteudoMcp Imagem(string caminho)
    {
        try
        {
            using var origem = new Bitmap(caminho);
            if (origem.Width <= LarguraMaximaDaImagem)
                return new ConteudoMcp().ComImagem(File.ReadAllBytes(caminho), Mime(caminho));

            var escala = (double)LarguraMaximaDaImagem / origem.Width;
            using var menor = new Bitmap(LarguraMaximaDaImagem, Math.Max(1, (int)Math.Round(origem.Height * escala)));
            using (var g = Graphics.FromImage(menor))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(origem, 0, 0, menor.Width, menor.Height);
            }
            using var ms = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var pars = new EncoderParameters(1);
            pars.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            menor.Save(ms, codec, pars);
            return new ConteudoMcp().ComImagem(ms.ToArray(), "image/jpeg");
        }
        catch (Exception ex)
        {
            return ConteudoMcp.Falha("Não deu para abrir a imagem: " + ex.Message);
        }
    }

    private static string Mime(string caminho) => Path.GetExtension(caminho).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    // ==================================================================

    private static ConteudoMcp LinhaDoTempo(SessaoGravacao s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {s.Titulo}");
        sb.AppendLine($"- Origem: {(s.Origem == OrigemDaSessao.Importada ? "arquivo importado" : "gravação ao vivo")}"
            + (s.ArquivoOriginal != null ? $" ({Path.GetFileName(s.ArquivoOriginal)})" : ""));
        sb.AppendLine($"- Data: {s.Inicio.LocalDateTime:dd/MM/yyyy HH:mm} · Duração: {Formato.Duracao(s.Duracao)}");
        sb.AppendLine($"- Idioma da fala: {(string.IsNullOrEmpty(s.Idioma) ? "desconhecido" : s.Idioma)}"
            + (s.IdiomaDaTraducao != null ? $" · tradução disponível em {s.IdiomaDaTraducao} (versao='traducao')" : ""));
        sb.AppendLine($"- Transcrição: {(s.Falas.Count > 0 ? $"{s.Falas.Count} trechos" : "não há")}");
        sb.AppendLine($"- Resumo: {(s.TemResumo ? "resumo.md existe" : "ainda não")}");

        var arquivos = new List<string>();
        foreach (var f in s.Arquivos.Todos) arquivos.Add(f);
        if (s.TemTranscricao) arquivos.Add("transcricao.md");
        if (s.TemTraducao) arquivos.Add("traducao.md");
        if (s.TemResumo) arquivos.Add("resumo.md");
        sb.AppendLine($"- Arquivos: {string.Join(", ", arquivos)}");

        if (s.Origem == OrigemDaSessao.Gravada)
            sb.AppendLine("- Trilhas: 'microfone' é quem gravou; 'sistema' são os outros participantes; a transcrição marca a fonte.");
        else
            sb.AppendLine("- Trilha única: não dá para separar quem falou.");

        var capitulos = s.Capitulos;
        if (capitulos.Count > 0)
        {
            sb.AppendLine().AppendLine("## Capítulos");
            foreach (var c in capitulos) sb.AppendLine($"- {c.Carimbo} {c.Titulo}");
        }

        var capturas = s.Marcas.Where(m => m.Tipo == TipoDeMarca.Captura).ToList();
        if (capturas.Count > 0)
        {
            sb.AppendLine().AppendLine($"## Imagens ({capturas.Count}) — use `quadros` e `ver_quadro`");
            foreach (var (m, i) in capturas.Select((m, i) => (m, i + 1)))
                sb.AppendLine($"- id {i}: {m.Carimbo} {m.Texto ?? "captura"}");
        }

        var marcadores = s.Marcas.Where(m => m.Tipo == TipoDeMarca.Marcador).ToList();
        if (marcadores.Count > 0)
        {
            sb.AppendLine().AppendLine("## Momentos marcados por quem gravou");
            foreach (var m in marcadores) sb.AppendLine($"- {m.Carimbo} {m.Texto ?? "marcado"}");
        }

        var trechos = s.TrechosDeVideo;
        if (trechos.Count > 0)
        {
            sb.AppendLine().AppendLine("## Vídeo (análise automática)");
            var naoPert = trechos.Where(t => t.Tipo == TipoDeTrecho.NaoPertinente).ToList();
            sb.AppendLine($"- {trechos.Count(t => t.Tipo == TipoDeTrecho.Slide)} slides, "
                + $"{Formato.Duracao(TimeSpan.FromSeconds(naoPert.Sum(t => t.Duracao.TotalSeconds)))} descartados como não pertinentes (outra janela na frente).");
            foreach (var t in naoPert) sb.AppendLine($"  - não pertinente: {Formato.Carimbo(t.DeSegundos)} → {Formato.Carimbo(t.AteSegundos)}");
        }

        var mudos = s.TrechosMudos;
        if (mudos.Count > 0)
        {
            sb.AppendLine().AppendLine("## Microfone mudo (os outros NÃO ouviram)");
            foreach (var m in mudos) sb.AppendLine($"- {Formato.Carimbo(m.DeSegundos)} → {Formato.Carimbo(m.AteSegundos)} ({m.Origem}; {(m.Confirmado ? "confirmado" : "provável")})");
        }
        return ConteudoMcp.Texto(sb.ToString());
    }

    private static ConteudoMcp Transcricao(SessaoGravacao s, JsonElement a)
    {
        var de = ServidorMcp.Num(a, "de") ?? 0;
        var ate = ServidorMcp.Num(a, "ate") ?? double.MaxValue;
        var versao = ServidorMcp.Str(a, "versao")?.ToLowerInvariant() ?? "original";

        IEnumerable<(double De, string Texto, string Fonte)> linhas;
        if (versao.StartsWith("trad") && s.TemTraducao)
            linhas = LerTraducao(s);
        else
            linhas = s.Falas.OrderBy(f => f.DeSegundos).Select(f => (f.DeSegundos, f.Texto, f.Fonte));

        var sb = new StringBuilder();
        var fontes = s.Falas.Select(f => f.Fonte).Distinct().Count() > 1;
        double? parouEm = null;
        var total = 0;
        foreach (var l in linhas)
        {
            if (l.De < de || l.De > ate) continue;
            total++;
            var linha = fontes ? $"{Formato.Carimbo(l.De)} [{l.Fonte}] {l.Texto}" : $"{Formato.Carimbo(l.De)} {l.Texto}";
            if (sb.Length + linha.Length > CaracteresPorFatia) { parouEm = l.De; break; }
            sb.AppendLine(linha);
        }
        if (total == 0) return ConteudoMcp.Texto(s.Falas.Count == 0 ? "(esta sessão não tem transcrição)" : "(nada neste intervalo)");
        if (parouEm is { } p) sb.AppendLine($"… (continua; chame de novo com de={Formato.Carimbo(p)})");
        return ConteudoMcp.Texto(sb.ToString());
    }

    private static IEnumerable<(double De, string Texto, string Fonte)> LerTraducao(SessaoGravacao s)
    {
        foreach (var linha in File.ReadLines(s.CaminhoTraducao))
        {
            if (linha.Length < 3 || linha[0] != '`') continue;
            var fim = linha.IndexOf('`', 1);
            if (fim < 0) continue;
            var t = ServidorMcp.LerTempo(linha[1..fim]);
            if (t == null) continue;
            yield return (t.Value, linha[(fim + 1)..].Trim(), "traducao");
        }
    }

    private static ConteudoMcp Buscar(SessaoGravacao s, JsonElement a)
    {
        var texto = ServidorMcp.Str(a, "texto");
        if (string.IsNullOrWhiteSpace(texto)) return ConteudoMcp.Falha("Informe o texto.");
        var maximo = (int)Math.Clamp(ServidorMcp.Num(a, "maximo") ?? 20, 1, 100);

        var sb = new StringBuilder();
        var n = 0;
        foreach (var f in s.Falas.OrderBy(f => f.DeSegundos))
        {
            if (!f.Texto.Contains(texto, StringComparison.CurrentCultureIgnoreCase)) continue;
            sb.AppendLine($"{Formato.Carimbo(f.DeSegundos)} {f.Texto}");
            if (++n >= maximo) break;
        }
        if (s.TemTraducao && n < maximo)
        {
            foreach (var (de, t, _) in LerTraducao(s))
            {
                if (!t.Contains(texto, StringComparison.CurrentCultureIgnoreCase)) continue;
                sb.AppendLine($"{Formato.Carimbo(de)} [tradução] {t}");
                if (++n >= maximo) break;
            }
        }
        return ConteudoMcp.Texto(n == 0 ? "(nenhuma ocorrência)" : sb.ToString());
    }

    private static ConteudoMcp Quadros(SessaoGravacao s, JsonElement a)
    {
        var de = ServidorMcp.Num(a, "de") ?? 0;
        var ate = ServidorMcp.Num(a, "ate") ?? double.MaxValue;
        var capturas = s.Marcas.Where(m => m.Tipo == TipoDeMarca.Captura && m.Arquivo != null).ToList();
        var sb = new StringBuilder();
        foreach (var (m, i) in capturas.Select((m, i) => (m, i + 1)))
        {
            if (m.EmSegundos < de || m.EmSegundos > ate) continue;
            sb.AppendLine($"id {i} · {m.Carimbo} · {m.Texto ?? "captura"} · {m.Arquivo}");
        }
        return ConteudoMcp.Texto(sb.Length == 0 ? "(nenhuma imagem)" : sb.ToString());
    }

    private static ConteudoMcp VerQuadro(SessaoGravacao s, JsonElement a)
    {
        var id = ServidorMcp.Str(a, "id") ?? (ServidorMcp.Num(a, "id")?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(id)) return ConteudoMcp.Falha("Informe o id.");

        var capturas = s.Marcas.Where(m => m.Tipo == TipoDeMarca.Captura && m.Arquivo != null).ToList();
        Marca? alvo = null;
        if (int.TryParse(id, out var n) && n >= 1 && n <= capturas.Count) alvo = capturas[n - 1];
        else alvo = capturas.FirstOrDefault(m => m.Arquivo!.EndsWith(id, StringComparison.OrdinalIgnoreCase)
                                              || Path.GetFileName(m.Arquivo!).Equals(id, StringComparison.OrdinalIgnoreCase));
        if (alvo == null) return ConteudoMcp.Falha($"Não há imagem com id '{id}'. Use `quadros` para ver os ids.");

        var caminho = Dentro(Path.GetFullPath(s.Pasta), alvo.Arquivo);
        if (caminho == null || !File.Exists(caminho)) return ConteudoMcp.Falha("O arquivo da imagem não existe mais.");
        return Imagem(caminho).ComTexto($"{alvo.Carimbo} · {alvo.Texto ?? "captura"}");
    }

    private static ConteudoMcp DefinirCapitulo(SessaoGravacao s, JsonElement a)
    {
        var em = ServidorMcp.Num(a, "em");
        var titulo = ServidorMcp.Str(a, "titulo");
        if (em == null || string.IsNullOrWhiteSpace(titulo)) return ConteudoMcp.Falha("Informe 'em' e 'titulo'.");
        var c = s.AdicionarCapitulo(TimeSpan.FromSeconds(em.Value), titulo);
        return ConteudoMcp.Texto($"Capítulo definido: {c.Carimbo} {c.Titulo}");
    }

    private static ConteudoMcp MarcarTrecho(SessaoGravacao s, JsonElement a)
    {
        var de = ServidorMcp.Num(a, "de");
        var ate = ServidorMcp.Num(a, "ate");
        var tipoTexto = ServidorMcp.Str(a, "tipo")?.ToLowerInvariant() ?? "";
        if (de == null || ate == null || ate <= de) return ConteudoMcp.Falha("Informe 'de' e 'ate' com ate > de.");
        var tipo = tipoTexto switch
        {
            "slide" => TipoDeTrecho.Slide,
            "video" => TipoDeTrecho.Video,
            "nao_pertinente" or "não_pertinente" or "nao pertinente" or "descartar" => TipoDeTrecho.NaoPertinente,
            _ => (TipoDeTrecho?)null,
        };
        if (tipo == null) return ConteudoMcp.Falha("tipo deve ser slide, video ou nao_pertinente.");
        s.ReclassificarTrecho(de.Value, ate.Value, tipo.Value);
        return ConteudoMcp.Texto($"Trecho {Formato.Carimbo(de.Value)} → {Formato.Carimbo(ate.Value)} marcado como {tipo}.");
    }
}
