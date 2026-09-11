using System.Text;
using Gravador.Core.Audio;
using Gravador.Core.Session;
using Gravador.Core.Settings;
using Gravador.Core.Transcription;
using NAudio.Wave;

namespace Gravador.Core.Importacao;

public sealed partial class ImportadorDeMidia
{
    /// <summary>
    /// Importa vários arquivos como UMA sessão, na ordem em que vieram.
    ///
    /// É o caso de quem manda cinco áudios de WhatsApp da mesma conversa: eles não são cinco
    /// assuntos, são um só partido em pedaços pelo aplicativo. Tratados como cinco sessões, o
    /// resumo não enxerga a conversa inteira e o Claude não consegue responder nada que atravesse
    /// a fronteira entre dois áudios.
    ///
    /// O que sai é uma sessão comum: um áudio contínuo, uma linha do tempo com um capítulo por
    /// parte, e um <c>transcricao.md</c> corrido em que os carimbos seguem somando de uma parte
    /// para a outra. Tudo o que já existia — tradução, resumo, conversa, as ferramentas MCP — passa
    /// a valer para o conjunto sem saber que ele veio de cinco arquivos.
    ///
    /// A transcrição é feita PARTE A PARTE e depois deslocada, em vez de uma passada sobre o áudio
    /// colado. Custa quase o mesmo (a carga do modelo é de centenas de milissegundos) e paga três
    /// coisas: uma parte que falha não leva as outras junto, cada parte pode estar num idioma
    /// diferente, e sobra um .md por parte para quem quiser mandar um áudio específico adiante.
    /// </summary>
    public async Task<SessaoGravacao> ImportarSequenciaAsync(IReadOnlyList<string> arquivos,
        OpcoesDeImportacao opcoes, IProgress<string>? etapa = null, CancellationToken ct = default)
    {
        if (arquivos.Count == 0) throw new ArgumentException("Nenhum arquivo para importar.", nameof(arquivos));

        // Um arquivo só não é sequência: cai no caminho normal, que sabe lidar com vídeo.
        if (arquivos.Count == 1)
            return await ImportarAsync(arquivos[0], opcoes, etapa, ct).ConfigureAwait(false);

        foreach (var a in arquivos)
            if (!File.Exists(a)) throw new FileNotFoundException("Arquivo não encontrado.", a);

        var sessao = SessaoGravacao.CriarImportada(_config, arquivos[0],
            opcoes.Titulo ?? $"{Path.GetFileNameWithoutExtension(arquivos[0])} (+{arquivos.Count - 1})");
        var pastaPartes = Path.Combine(sessao.Pasta, "partes");
        Directory.CreateDirectory(pastaPartes);
        sessao.Salvar();

        try
        {
            // ---- 1. um áudio só, na ordem ----
            var partes = new List<ParteImportada>();
            var wavsDasPartes = new List<string>();
            var combinado = Path.Combine(sessao.Pasta, "mixado.wav");

            using (var saida = new WavWriter(combinado, _config.TaxaAmostragem, _config.Canais))
            {
                for (var i = 0; i < arquivos.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    etapa?.Report($"Parte {i + 1} de {arquivos.Count}: extraindo o áudio de {Path.GetFileName(arquivos[i])}...");

                    var parteWav = Path.Combine(pastaPartes, $"{i + 1:00}.wav");
                    await ExtratorDeAudio.ExtrairAsync(arquivos[i], parteWav,
                        _config.TaxaAmostragem, _config.Canais, null, ct).ConfigureAwait(false);

                    var em = saida.Duracao;
                    var duracao = Anexar(parteWav, saida);
                    partes.Add(new ParteImportada(arquivos[i], em, duracao));
                    wavsDasPartes.Add(parteWav);
                }
            }

            var total = partes.Count > 0 ? partes[^1].Em + partes[^1].Duracao : TimeSpan.Zero;
            if (total <= TimeSpan.Zero)
                throw new InvalidOperationException("Nenhum dos arquivos tinha áudio legível.");
            sessao.Duracao = total;

            // ---- 2. o MP3 ----
            if (_config.Formato == FormatoSaida.Mp3)
            {
                var r = AudioEncoder.ParaMp3(combinado, _config.Mp3Kbps, apagarWav: !_config.ManterWav,
                    progresso: p => etapa?.Report($"Convertendo o áudio para MP3... {p * 100:0}%"), ct: ct);
                sessao.Arquivos.Mixado = Path.GetFileName(r.Caminho);
                if (!r.Ok && r.Erro != null) Aviso?.Invoke("O áudio ficou em WAV: " + r.Erro);
            }
            else
            {
                sessao.Arquivos.Mixado = Path.GetFileName(combinado);
            }

            // ---- 3. um capítulo por parte ----
            // É o que faz a linha do tempo dizer onde um áudio acaba e o outro começa — sem isso, a
            // sessão vira um bloco só e ninguém acha o trecho que veio do terceiro arquivo.
            for (var i = 0; i < partes.Count; i++)
                sessao.AdicionarCapitulo(partes[i].Em, $"{i + 1}. {partes[i].Nome}");
            sessao.Salvar();

            // ---- 4. a transcrição, parte a parte ----
            if (opcoes.Transcrever && opcoes.Motor != MotorTranscricao.Nenhum)
                await TranscreverPartesAsync(sessao, partes, wavsDasPartes, opcoes, etapa, ct).ConfigureAwait(false);

            // Os WAV das partes já serviram: o áudio está no combinado e o texto, na sessão.
            foreach (var w in wavsDasPartes)
                try { File.Delete(w); } catch { /* fica no disco, não é fatal */ }
            try { if (Directory.GetFileSystemEntries(pastaPartes).Length == 0) Directory.Delete(pastaPartes); }
            catch { /* tem os .md dentro */ }

            sessao.Encerrar(total);

            // ---- 5. a cauda comum ----
            // O motor vai junto de propósito: se a transcrição por parte não produziu nada, o
            // PosProcessamento tenta uma passada sobre o áudio inteiro. Quando produziu, ele pula
            // sozinho, porque só transcreve com a sessão sem falas.
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
            try { Directory.Delete(sessao.Pasta, recursive: true); } catch { /* deixa para o usuário */ }
            throw;
        }
    }

    /// <summary>
    /// Transcreve cada parte no seu próprio WAV e desloca os carimbos para a linha do tempo comum.
    /// </summary>
    private async Task TranscreverPartesAsync(SessaoGravacao sessao, List<ParteImportada> partes,
        List<string> wavs, OpcoesDeImportacao opcoes, IProgress<string>? etapa, CancellationToken ct)
    {
        using var transcritor = Transcritores.DeArquivo(_config, opcoes.Motor);
        if (transcritor == null) { Aviso?.Invoke("Motor de transcrição desconhecido."); return; }
        if (!transcritor.Disponivel)
        {
            Aviso?.Invoke(transcritor.Motivo ?? "A transcrição não está configurada.");
            return;
        }

        transcritor.IdiomaPedido = string.IsNullOrWhiteSpace(opcoes.Idioma) ? "auto" : opcoes.Idioma;
        var todas = new List<TrechoFalado>();

        for (var i = 0; i < partes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            etapa?.Report($"Transcrevendo a parte {i + 1} de {partes.Count} ({Formato.Duracao(partes[i].Duracao)})...");

            try
            {
                // A fonte é a MESMA em todas as partes, de propósito. Ela é o que separa "você" de
                // "a reunião" numa gravação de duas trilhas; usar "parte 1", "parte 2"... fazia o
                // transcricao.md achar que eram cinco interlocutores e imprimir a legenda de
                // microfone/reunião sem sentido nenhum. Quem separa as partes são os capítulos.
                var trechos = await transcritor
                    .TranscreverAsync(wavs[i], "apresentação", etapa, ct).ConfigureAwait(false);

                // O deslocamento é o que costura as partes numa linha do tempo só. Sem ele, todas
                // começariam no zero e a transcrição corrida ficaria embaralhada.
                var desloque = partes[i].Em.TotalSeconds;
                var ajustados = trechos
                    .Select(t => t with { DeSegundos = t.DeSegundos + desloque, AteSegundos = t.AteSegundos + desloque })
                    .ToList();

                todas.AddRange(ajustados);

                if (string.IsNullOrEmpty(sessao.Idioma) && !string.IsNullOrWhiteSpace(transcritor.IdiomaDetectado))
                    sessao.Idioma = transcritor.IdiomaDetectado;

                if (opcoes.TranscricoesPorParte)
                    GravarMdDaParte(sessao, i + 1, partes[i], trechos, transcritor.IdiomaDetectado);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Uma parte que falha não pode levar as outras: é justamente por isso que a
                // transcrição é por parte.
                Aviso?.Invoke($"A parte {i + 1} ({partes[i].Nome}) não transcreveu: {ex.Message}");
            }
        }

        if (todas.Count == 0) return;
        sessao.AdicionarFalas(todas.OrderBy(t => t.DeSegundos));
        sessao.Salvar();
        etapa?.Report($"Transcrição: {todas.Count} trechos em {partes.Count} partes"
            + (string.IsNullOrEmpty(sessao.Idioma) ? "" : $", em {Claude.Tradutor.NomeDoIdioma(sessao.Idioma)}") + ".");
    }

    /// <summary>Um .md por parte, com os carimbos da PRÓPRIA parte — é como ela seria sozinha.</summary>
    private static void GravarMdDaParte(SessaoGravacao sessao, int numero, ParteImportada parte,
        IReadOnlyList<TrechoFalado> trechos, string? idioma)
    {
        try
        {
            if (trechos.Count == 0) return;
            var pasta = Path.Combine(sessao.Pasta, "partes");
            Directory.CreateDirectory(pasta);

            var sb = new StringBuilder();
            sb.Append($"# Parte {numero} — {parte.Nome}\n\n");
            sb.Append($"*{Formato.Duracao(parte.Duracao)}");
            if (!string.IsNullOrWhiteSpace(idioma)) sb.Append($" · fala em {Claude.Tradutor.NomeDoIdioma(idioma)}");
            sb.Append($" · começa em {Formato.Carimbo(parte.Em.TotalSeconds)} da sessão*\n\n");

            foreach (var t in trechos)
                sb.Append($"`{Formato.Carimbo(t.DeSegundos)}` {t.Texto}\n");

            var nome = $"{numero:00} - {SemCaracteresProibidos(parte.Nome)}.md";
            File.WriteAllText(Path.Combine(pasta, nome), sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // o texto continua no transcricao.md e no sessao.json
        }
    }

    private static string SemCaracteresProibidos(string nome)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        var limpo = new string(nome.Select(c => invalidos.Contains(c) ? '_' : c).ToArray()).Trim();
        return limpo.Length > 60 ? limpo[..60] : limpo;
    }

    /// <summary>Copia um WAV para o fim de outro. Devolve quanto foi anexado.</summary>
    private static TimeSpan Anexar(string wav, WavWriter saida)
    {
        using var leitor = new WaveFileReader(wav);
        var amostras = leitor.ToSampleProvider();
        var buffer = new float[16384];
        int n;
        while ((n = amostras.Read(buffer, 0, buffer.Length)) > 0) saida.Escrever(buffer, n);
        return leitor.TotalTime;
    }
}
