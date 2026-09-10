using System.Text;
using System.Text.Json;
using Gravador.Core.Ferramentas;
using Gravador.Core.Settings;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Gravador.Core.Legendas;

/// <summary>
/// Marian (opus-mt inglês→línguas românicas) rodando nesta máquina, pelo ONNX Runtime.
///
/// É o padrão da legenda, e o motivo é o que ele NÃO faz: não precisa de chave, não tem cota para
/// estourar no meio de uma apresentação, e não manda para servidor nenhum o que foi dito na sua
/// reunião. Custa ~115 MB baixados uma vez.
///
/// Medido nesta máquina, com os grafos quantizados e 2 threads: **~800 ms por frase**. Não são os
/// ~100 ms que se imagina de um modelo pequeno, porque a decodificação é palavra por palavra — cada
/// palavra gerada é uma passada inteira do decodificador. O tempo cresce com o tamanho da SAÍDA,
/// não com o da entrada, e é por isso que a legenda traduz pedaços curtos.
///
/// A fraqueza conhecida é jargão: ele traduziu "fifteen trillion tokens" como "quinze trilhões de
/// fichas". Quem precisa de termo técnico certo troca para o DeepL nas configurações.
/// </summary>
public sealed class TradutorMarian : ITradutorAoVivo
{
    private const int Camadas = 6, Cabecas = 8, Dimensao = 64;

    /// <summary>Id com que a decodificação começa. No Marian é o mesmo do preenchimento.</summary>
    private const int TokenInicial = 65000;

    /// <summary>"&lt;/s&gt;". Também é o que encerra a frase gerada.</summary>
    private const int TokenFinal = 0;

    /// <summary>"&lt;unk&gt;", para a peça que não estiver no vocabulário.</summary>
    private const int TokenDesconhecido = 1;

    /// <summary>Teto de segurança: sem ele, um laço que não converge geraria para sempre.</summary>
    private const int MaximoDePalavras = 256;

    private readonly int _threads;
    private readonly object _trava = new();

    private SentencePieceTokenizer? _pecas;
    private Dictionary<string, int>? _vocabulario;
    private string[]? _reverso;
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private bool _carregado;

    public TradutorMarian(AppSettings config)
    {
        _threads = config.LegendaThreads > 0
            ? Math.Max(1, config.LegendaThreads / 2)
            : Math.Max(1, Environment.ProcessorCount / 8);

        var faltando = Ferramentas.Ferramentas.Marian.Where(f => !f.Disponivel).ToList();
        if (faltando.Count > 0)
            Motivo = $"O tradutor local ainda não foi baixado ({faltando.Sum(f => f.TamanhoAproximadoMb)} MB). "
                   + "Baixe pelas configurações ou por `gravador-cli ferramentas --baixar`.";
    }

    public string Nome => "Marian local (opus-mt)";
    public bool Disponivel => Motivo == null;
    public string? Motivo { get; private set; }

    // ==================================================================

    public Task<string> TraduzirAsync(string texto, string de, string para, CancellationToken ct)
    {
        if (!Disponivel || string.IsNullOrWhiteSpace(texto)) return Task.FromResult(texto);
        try
        {
            lock (_trava)
            {
                Carregar();
                return Task.FromResult(Traduzir(texto, TokenDeIdioma(para), ct));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Task.FromResult(texto);
        }
    }

    /// <summary>
    /// "pt-BR" vira "&gt;&gt;pt_BR&lt;&lt;". O modelo atende a várias línguas românicas e escolhe qual pela
    /// primeira ficha da entrada — sem ela, ele decide sozinho e costuma responder em francês.
    /// </summary>
    private string TokenDeIdioma(string idioma)
    {
        var limpo = (idioma ?? "").Trim().Replace('-', '_');
        if (limpo.Length == 0) return ">>pt_BR<<";

        // "pt_BR" antes de "pt": o modelo distingue os dois, e o brasileiro é o que se quer aqui.
        foreach (var tentativa in new[] { $">>{limpo}<<", $">>{limpo.Split('_')[0]}<<" })
            if (_vocabulario!.ContainsKey(tentativa)) return tentativa;
        return ">>pt<<";
    }

    private void Carregar()
    {
        if (_carregado) return;

        var encoder = Ferramentas.Ferramentas.ArquivoMarian("encoder_model_quantized.onnx", 53).Localizar();
        var decoder = Ferramentas.Ferramentas.ArquivoMarian("decoder_model_merged_quantized.onnx", 60).Localizar();
        var vocab = Ferramentas.Ferramentas.ArquivoMarian("vocab.json", 2).Localizar();
        var spm = Ferramentas.Ferramentas.ArquivoMarian("source.spm", 1).Localizar();
        if (encoder == null || decoder == null || vocab == null || spm == null)
            throw new InvalidOperationException("Faltam arquivos do tradutor local.");

        using (var fs = File.OpenRead(spm)) _pecas = SentencePieceTokenizer.Create(fs);

        _vocabulario = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocab))
            ?? throw new InvalidOperationException("vocab.json do tradutor veio vazio.");
        _reverso = new string[_vocabulario.Values.Max() + 1];
        foreach (var (peca, id) in _vocabulario) _reverso[id] = peca;

        // Poucas threads de propósito: o whisper já está comendo CPU do outro lado da legenda, e os
        // dois disputando deixariam a reunião travada — que é o que ninguém pode pagar.
        var opcoes = new SessionOptions { IntraOpNumThreads = _threads, InterOpNumThreads = 1 };
        _encoder = new InferenceSession(encoder, opcoes);
        _decoder = new InferenceSession(decoder, opcoes);
        _carregado = true;
    }

    private string Traduzir(string texto, string tokenDeIdioma, CancellationToken ct)
    {
        // ---- entrada: ficha do idioma alvo, as peças, e o fim ----
        var ids = new List<long>();
        if (_vocabulario!.TryGetValue(tokenDeIdioma, out var idAlvo)) ids.Add(idAlvo);
        foreach (var t in _pecas!.EncodeToTokens(texto, out _))
        {
            // O tokenizer põe um <s> na frente que o Marian não usa e que estraga a tradução.
            if (t.Value == "<s>") continue;
            ids.Add(_vocabulario.TryGetValue(t.Value, out var id) ? id : TokenDesconhecido);
        }
        ids.Add(TokenFinal);

        var n = ids.Count;
        var entrada = new DenseTensor<long>(ids.ToArray(), new[] { 1, n });
        var mascara = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), new[] { 1, n });

        using var saidaEncoder = _encoder!.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", entrada),
            NamedOnnxValue.CreateFromTensor("attention_mask", mascara),
        ]);
        var oculto = saidaEncoder.First(x => x.Name == "last_hidden_state").AsTensor<float>();
        var ocultoFixo = new DenseTensor<float>(oculto.ToArray(), oculto.Dimensions.ToArray());

        // ---- decodificação gananciosa, com cache ----
        //
        // O grafo é o "merged": ele traz os dois caminhos (com e sem cache) e escolhe pelo
        // use_cache_branch. Na primeira passada o cache vai vazio — comprimento zero, não ausente —
        // e é isso que a forma [1, cabeças, 0, dimensão] diz.
        var vazio = new DenseTensor<float>(Array.Empty<float>(), new[] { 1, Cabecas, 0, Dimensao });
        var passadoDecoder = new DenseTensor<float>[Camadas * 2];
        var passadoEncoder = new DenseTensor<float>[Camadas * 2];
        for (var i = 0; i < Camadas * 2; i++) { passadoDecoder[i] = vazio; passadoEncoder[i] = vazio; }

        var geradas = new List<int>();
        var atual = TokenInicial;
        var usarCache = false;

        for (var passo = 0; passo < MaximoDePalavras; passo++)
        {
            ct.ThrowIfCancellationRequested();

            var entradas = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mascara),
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(new[] { (long)atual }, new[] { 1, 1 })),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", ocultoFixo),
                NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { usarCache }, new[] { 1 })),
            };
            for (var c = 0; c < Camadas; c++)
            {
                entradas.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{c}.decoder.key", passadoDecoder[c * 2]));
                entradas.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{c}.decoder.value", passadoDecoder[c * 2 + 1]));
                entradas.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{c}.encoder.key", passadoEncoder[c * 2]));
                entradas.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{c}.encoder.value", passadoEncoder[c * 2 + 1]));
            }

            using var saida = _decoder!.Run(entradas);
            var logits = saida.First(x => x.Name == "logits").AsTensor<float>();

            var ultimo = logits.Dimensions[1] - 1;
            var melhor = 0;
            var melhorValor = float.NegativeInfinity;
            for (var v = 0; v < logits.Dimensions[2]; v++)
            {
                var s = logits[0, ultimo, v];
                if (s > melhorValor) { melhorValor = s; melhor = v; }
            }
            if (melhor == TokenFinal) break;

            geradas.Add(melhor);
            atual = melhor;

            foreach (var s in saida)
            {
                if (!s.Name.StartsWith("present.", StringComparison.Ordinal)) continue;
                var partes = s.Name.Split('.');
                var camada = int.Parse(partes[1]);
                var indice = camada * 2 + (partes[3] == "key" ? 0 : 1);
                var t = s.AsTensor<float>();

                if (partes[2] == "decoder")
                    passadoDecoder[indice] = new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());
                else if (!usarCache)
                    // A atenção sobre o encoder olha para um texto que não muda: essas chaves são
                    // calculadas uma vez e reaproveitadas. Recopiá-las a cada passo seria jogar
                    // fora metade do ganho do cache.
                    passadoEncoder[indice] = new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());
            }
            usarCache = true;
        }

        return Remontar(geradas);
    }

    /// <summary>Peças de volta a texto. O "▁" do SentencePiece é onde havia espaço.</summary>
    private string Remontar(List<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            var peca = id >= 0 && id < _reverso!.Length ? _reverso[id] : null;
            if (peca == null) continue;
            sb.Append(peca.StartsWith('▁') ? " " + peca[1..] : peca);
        }
        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        lock (_trava)
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
            _encoder = null;
            _decoder = null;
            _carregado = false;
        }
    }
}
