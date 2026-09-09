using System.Text.Json;
using System.Text.Json.Serialization;
using Gravador.Core.Audio;
using Gravador.Core.Ferramentas;
using Gravador.Core.Session;
using Gravador.Core.Settings;
using NAudio.Wave;

namespace Gravador.Core.Importacao;

public sealed class OpcoesDeImportacao
{
    public string? Titulo { get; set; }

    /// <summary>Whisper por padrão: importar é pós-processamento, e é onde ele é o motor certo.</summary>
    public MotorTranscricao Motor { get; set; } = MotorTranscricao.Whisper;

    /// <summary>"auto" deixa o whisper descobrir. Uma apresentação em inglês sai "en".</summary>
    public string Idioma { get; set; } = "auto";

    public bool ExtrairQuadros { get; set; } = true;
    public bool Transcrever { get; set; } = true;
    public bool Traduzir { get; set; } = true;
    public bool Resumir { get; set; }
}

/// <summary>
/// Transforma um arquivo de áudio ou vídeo que já existe numa sessão do Gravador — a mesma pasta,
/// o mesmo <c>sessao.json</c>, o mesmo pós-processamento de uma gravação ao vivo.
///
/// Importar não é um pipeline novo: é outra forma de CRIAR uma sessão. Tudo o que vem depois
/// (transcrição, tradução, resumo, conversa) já existia para as gravações e funciona sem saber de
/// onde a sessão veio. O que este arquivo faz é só a entrada: sondar, extrair o áudio, amostrar o
/// vídeo, reduzir milhares de quadros a uma dezena de slides.
///
/// A ordem das etapas é a do valor: o áudio sai primeiro (14 s para uma hora) porque é ele que a
/// transcrição precisa; os quadros vêm depois (alguns minutos) e são bônus.
/// </summary>
public sealed class ImportadorDeMidia
{
    private readonly AppSettings _config;

    public ImportadorDeMidia(AppSettings config)
    {
        _config = config;
    }

    public event Action<string>? Aviso;

    public async Task<SessaoGravacao> ImportarAsync(string arquivo, OpcoesDeImportacao opcoes,
        IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        if (!File.Exists(arquivo)) throw new FileNotFoundException("Arquivo não encontrado.", arquivo);

        var progressoDownload = new Progress<ProgressoDeFerramenta>(p =>
        {
            if (p.Etapa == "baixando")
                etapa?.Report(p.Fracao is { } f
                    ? $"Baixando {p.Ferramenta} (só na primeira vez)... {f * 100:0}% de {p.BytesTotais / 1_000_000} MB"
                    : $"Baixando {p.Ferramenta} (só na primeira vez)...");
        });

        // ---- 1. o que tem dentro ----
        etapa?.Report("Lendo o arquivo...");
        var ehVideo = Ffmpeg.PareceVideo(arquivo);
        InfoDeMidia? info = null;
        if (ehVideo && opcoes.ExtrairQuadros)
        {
            try
            {
                info = await Ffmpeg.SondarAsync(arquivo, progressoDownload, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Aviso?.Invoke("Não deu para ler o vídeo com o ffprobe; sigo só com o áudio. " + ex.Message);
            }
        }

        var duracao = info?.Duracao ?? DuracaoPeloMediaFoundation(arquivo);
        if (duracao <= TimeSpan.Zero)
            throw new InvalidOperationException("Não consegui ler a duração do arquivo — ele é um áudio ou vídeo que o Windows abre?");

        // ---- 2. a sessão ----
        var sessao = SessaoGravacao.CriarImportada(_config, arquivo, opcoes.Titulo);
        sessao.Duracao = duracao;
        if (Transcription.Transcritores.CodigoCurto(opcoes.Idioma) != "auto") sessao.Idioma = opcoes.Idioma;
        sessao.Salvar();

        try
        {
            // ---- 3. o áudio ----
            etapa?.Report("Extraindo o áudio...");
            var wav = Path.Combine(sessao.Pasta, "mixado.wav");
            await ExtratorDeAudio.ExtrairAsync(arquivo, wav, _config.TaxaAmostragem, _config.Canais,
                new Progress<double>(f => etapa?.Report($"Extraindo o áudio... {f * 100:0}%")), ct).ConfigureAwait(false);

            if (_config.Formato == FormatoSaida.Mp3)
            {
                var r = AudioEncoder.ParaMp3(wav, _config.Mp3Kbps, apagarWav: !_config.ManterWav,
                    progresso: p => etapa?.Report($"Convertendo o áudio para MP3... {p * 100:0}%"), ct: ct);
                sessao.Arquivos.Mixado = Path.GetFileName(r.Caminho);
                if (!r.Ok && r.Erro != null) Aviso?.Invoke("O áudio ficou em WAV: " + r.Erro);
            }
            else
            {
                sessao.Arquivos.Mixado = Path.GetFileName(wav);
            }
            sessao.Salvar();

            // ---- 4. os quadros ----
            if (info is { TemVideo: true } && opcoes.ExtrairQuadros)
                await ExtrairQuadrosAsync(arquivo, sessao, info, etapa, ct).ConfigureAwait(false);

            sessao.Encerrar(duracao);

            // ---- 5. a cauda comum ----
            await PosProcessamento.ExecutarAsync(sessao, _config, new PosProcessamento.Opcoes
            {
                Motor = opcoes.Transcrever ? opcoes.Motor : null,
                Idioma = opcoes.Idioma,
                Traduzir = opcoes.Traduzir,
                Resumir = opcoes.Resumir,
            }, etapa, a => Aviso?.Invoke(a), ct).ConfigureAwait(false);

            etapa?.Report("Pronto.");
            return sessao;
        }
        catch (OperationCanceledException)
        {
            // pasta pela metade não é sessão: some
            try { Directory.Delete(sessao.Pasta, recursive: true); } catch { /* deixa para o usuário */ }
            throw;
        }
    }

    private static TimeSpan DuracaoPeloMediaFoundation(string arquivo)
    {
        try
        {
            using var leitor = new MediaFoundationReader(arquivo);
            return leitor.TotalTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// O funil: amostra a 1 fps, analisa, e extrai em alta resolução só o que sobrou.
    ///
    /// Os quadros finais são recortados no retângulo do SLIDE quando ele foi achado, senão no da
    /// janela da apresentação. É isso que faz o resultado ser "a apresentação copiada" e não "a tela
    /// do computador com a apresentação em algum lugar".
    /// </summary>
    private async Task ExtrairQuadrosAsync(string video, SessaoGravacao sessao, InfoDeMidia info,
        IProgress<string>? etapa, CancellationToken ct)
    {
        var fps = _config.ImportacaoQuadrosPorSegundo;
        var pastaMiniaturas = sessao.PastaMiniaturas;

        etapa?.Report("Amostrando o vídeo a 1 quadro por segundo...");
        var total = await Ffmpeg.MiniaturasAsync(video, pastaMiniaturas, fps, _config.ImportacaoLarguraMiniatura, info.Duracao,
            new Progress<double>(f => etapa?.Report($"Amostrando o vídeo... {f * 100:0}%")), ct).ConfigureAwait(false);
        if (total < 3)
        {
            Aviso?.Invoke("O vídeo é curto demais para a análise de quadros.");
            return;
        }

        etapa?.Report($"Analisando {total} quadros...");
        var analise = await Task.Run(() => AnaliseDeQuadros.Analisar(pastaMiniaturas, fps, info.Largura, info.Altura,
            new Progress<double>(f => etapa?.Report($"Analisando {total} quadros... {f * 100:0}%")), ct), ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(sessao.CaminhoAnalise, JsonSerializer.Serialize(analise, JsonDaAnalise), ct).ConfigureAwait(false);
        sessao.DefinirTrechosDeVideo(analise.Trechos);

        // ---- os slides, em alta resolução ----
        var slides = analise.Trechos.Where(t => t.Tipo == TipoDeTrecho.Slide).ToList();
        Retangulo? recorteGeral = _config.ImportacaoRecortarNoConteudo ? analise.RetanguloDoConteudo : null;

        // Apresentação sem slide (só o palestrante falando): um quadro a cada dois minutos de
        // conteúdo pertinente, para o pacote não ficar sem imagem nenhuma.
        var extras = new List<TrechoDeVideo>();
        if (slides.Count < 3)
        {
            foreach (var t in analise.Trechos.Where(t => t.Tipo == TipoDeTrecho.Video && t.Duracao.TotalSeconds >= 30))
            {
                var passo = 120.0;
                for (var s = t.DeSegundos + 5; s < t.AteSegundos; s += passo)
                    extras.Add(new TrechoDeVideo(s, Math.Min(t.AteSegundos, s + passo), TipoDeTrecho.Video, (int)Math.Round(s * fps)));
            }
        }

        var alvos = slides.Concat(extras).OrderBy(t => t.DeSegundos).ToList();
        var indice = 1;
        var trechosAtualizados = new List<TrechoDeVideo>(analise.Trechos);
        foreach (var t in alvos)
        {
            ct.ThrowIfCancellationRequested();
            var em = TimeSpan.FromSeconds((t.QuadroRepresentativo + 0.5) / fps);
            var rotulo = t.Tipo == TipoDeTrecho.Slide ? "slide" : "quadro";
            var nome = $"{indice:000}_{rotulo}_{(int)em.TotalMinutes:00}m{em.Seconds:00}s.jpg";
            var destino = Path.Combine(sessao.PastaCapturas, nome);

            etapa?.Report($"Extraindo {rotulo} {indice} de {alvos.Count} ({Formato.Carimbo(em)})...");
            try
            {
                // O recorte é o do PRÓPRIO trecho: o slide onde ele estava naquele momento. Sem recorte
                // conhecido (trecho de vídeo), a janela da apresentação inteira.
                var recorte = _config.ImportacaoRecortarNoConteudo ? (t.Recorte ?? recorteGeral) : null;
                await Ffmpeg.ExtrairQuadroAsync(video, em, destino, recorte?.Tupla, _config.ImportacaoLarguraQuadroFinal, ct: ct).ConfigureAwait(false);
                sessao.AdicionarCaptura(TimeSpan.FromSeconds(t.DeSegundos), destino,
                    t.Tipo == TipoDeTrecho.Slide ? $"Slide {indice}" : $"Palestrante ({Formato.Carimbo(em)})");
                var i = trechosAtualizados.IndexOf(t);
                if (i >= 0) trechosAtualizados[i] = t with { Arquivo = Path.GetRelativePath(sessao.Pasta, destino) };
                indice++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Aviso?.Invoke($"O quadro em {Formato.Carimbo(em)} não saiu: {ex.Message}");
            }
        }
        sessao.DefinirTrechosDeVideo(trechosAtualizados);

        var naoPertinente = TimeSpan.FromSeconds(analise.SegundosNaoPertinentes);
        etapa?.Report($"{indice - 1} imagem(ns) extraída(s); {Formato.Duracao(naoPertinente)} de vídeo descartados como não pertinentes.");

        if (!_config.ImportacaoManterMiniaturas)
        {
            try { Directory.Delete(pastaMiniaturas, recursive: true); } catch { /* fica */ }
        }
    }

    private static readonly JsonSerializerOptions JsonDaAnalise = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
