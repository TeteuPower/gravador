using System.Text;
using System.Text.Json;
using Gravador.Core;
using Gravador.Core.Audio;
using Gravador.Core.Claude;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.Cli;

/// <summary>
/// Modo de integração: comandos JSON por linha no stdin, eventos JSON por linha no stdout.
///
/// É o mesmo contrato que o Limpador usa entre o app e o motor de varredura, e é de propósito:
/// quando estas ferramentas forem agrupadas, quem estiver por cima vai falar com todas do mesmo
/// jeito, sem aprender um protocolo por ferramenta.
///
/// Entrada:  {"id":1,"cmd":"iniciar","titulo":"Reunião de terça"}
///           {"cmd":"capturar"} | {"cmd":"marcar","texto":"orçamento"} | {"cmd":"mudo","valor":true}
///           {"id":2,"cmd":"parar"} | {"cmd":"pausar"} | {"cmd":"retomar"} | {"cmd":"exit"}
/// Saída:    {"type":"ready"} | {"type":"estado",...} | {"type":"niveis",...} | {"type":"marca",...}
///           {"type":"resultado",...} | {"type":"error","mensagem":"..."}
/// </summary>
internal static class IpcServer
{
    private static readonly object TravaDeEscrita = new();
    private static StreamWriter _saida = null!;
    private static ServicoDeGravacao _servico = null!;

    public static int Run()
    {
        _saida = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var entrada = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        var config = AppSettings.Carregar();
        _servico = new ServicoDeGravacao(config);
        Assinar();

        Emitir(w =>
        {
            w.WriteString("type", "ready");
            w.WriteNumber("pid", Environment.ProcessId);
            w.WriteString("versao", AppInfo.Versao);
            w.WriteString("pastaSaida", config.PastaSaidaEfetiva);
            w.WriteBoolean("mp3", AudioEncoder.Mp3Disponivel);
        });

        string? linha;
        while ((linha = entrada.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(linha)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(linha); }
            catch (JsonException ex) { Erro(null, $"JSON inválido: {ex.Message}"); continue; }

            using (doc)
            {
                var raiz = doc.RootElement;
                long? id = raiz.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64() : null;
                var cmd = raiz.TryGetProperty("cmd", out var cmdEl) ? cmdEl.GetString() ?? "" : "";

                if (cmd == "exit")
                {
                    Encerrar();
                    return 0;
                }
                try { Despachar(id, cmd, raiz); }
                catch (Exception ex) { Erro(id, ex.Message); }
            }
        }

        Encerrar();
        return 0;
    }

    private static void Encerrar()
    {
        // Uma gravação em andamento quando o processo morre não pode virar arquivo quebrado: os WAV
        // já estão íntegros em disco, e este fecho grava a linha do tempo por cima.
        if (_servico.EmAndamento) _servico.Motor.AbortarSalvando();
        _servico.Dispose();
    }

    private static void Assinar()
    {
        _servico.EstadoMudou += _ => EmitirEstado();
        _servico.Progrediu += _ => { };
        _servico.Aviso += a => Emitir(w => { w.WriteString("type", "aviso"); w.WriteString("mensagem", a); });
        _servico.Marcou += m => Emitir(w =>
        {
            w.WriteString("type", "marca");
            w.WriteNumber("em", m.EmSegundos);
            w.WriteString("marca", m.Tipo.ToString());
            if (m.Texto is { } t) w.WriteString("texto", t);
            if (m.Arquivo is { } a) w.WriteString("arquivo", a);
        });
        _servico.Niveis += n => Emitir(w =>
        {
            w.WriteString("type", "niveis");
            w.WriteNumber("sistema", Math.Round(n.Sistema, 4));
            w.WriteNumber("microfone", Math.Round(n.Microfone, 4));
            w.WriteBoolean("vocefalando", n.VoceFalando);
            w.WriteNumber("segundos", Math.Round(_servico.Decorrido.TotalSeconds, 2));
        });
        _servico.Mudo.Mudou += e => Emitir(w =>
        {
            w.WriteString("type", "mudo");
            w.WriteBoolean("mudo", e.Mudo);
            w.WriteString("origem", e.Origem.ToString());
            w.WriteBoolean("certeza", e.Certeza);
            w.WriteString("descricao", e.Descricao);
            if (e.AppEmChamada is { } app) w.WriteString("app", app);
        });
    }

    private static void Despachar(long? id, string cmd, JsonElement raiz)
    {
        switch (cmd)
        {
            case "iniciar":
            {
                var titulo = Texto(raiz, "titulo");
                var sessao = _servico.Iniciar(titulo);
                Emitir(w =>
                {
                    Id(w, id);
                    w.WriteString("type", "iniciada");
                    w.WriteString("pasta", sessao.Pasta);
                    w.WriteString("sessao", sessao.Id);
                });
                break;
            }

            case "parar":
            {
                // Em outra thread: a conversão para MP3 leva dezenas de segundos numa reunião longa,
                // e travar o laço de comandos deixaria o app do outro lado sem resposta nenhuma.
                var progresso = new Progress<string>(e =>
                    Emitir(w => { w.WriteString("type", "etapa"); w.WriteString("mensagem", e); }));
                Task.Run(async () =>
                {
                    try
                    {
                        var r = await _servico.PararAsync(progresso).ConfigureAwait(false);
                        Emitir(w =>
                        {
                            Id(w, id);
                            w.WriteString("type", "resultado");
                            w.WriteString("pasta", r.Sessao.Pasta);
                            w.WriteNumber("duracaoSegundos", Math.Round(r.Duracao.TotalSeconds, 2));
                            w.WriteNumber("capturas", r.Sessao.QuantidadeDeCapturas);
                            w.WritePropertyName("arquivos");
                            w.WriteStartArray();
                            foreach (var a in r.Sessao.Arquivos.Todos) w.WriteStringValue(a);
                            w.WriteEndArray();
                            w.WritePropertyName("avisos");
                            w.WriteStartArray();
                            foreach (var a in r.Avisos) w.WriteStringValue(a);
                            w.WriteEndArray();
                        });
                    }
                    catch (Exception ex) { Erro(id, ex.Message); }
                });
                break;
            }

            case "pausar": _servico.Pausar(); break;
            case "retomar": _servico.Retomar(); break;

            case "capturar":
            {
                var imagem = _servico.Capturar();
                Emitir(w =>
                {
                    Id(w, id);
                    w.WriteString("type", "capturada");
                    if (imagem == null) { w.WriteBoolean("ok", false); return; }
                    w.WriteBoolean("ok", true);
                    w.WriteString("arquivo", imagem.Caminho);
                    w.WriteNumber("largura", imagem.Largura);
                    w.WriteNumber("altura", imagem.Altura);
                    if (imagem.Janela is { } j) w.WriteString("janela", j);
                });
                break;
            }

            case "marcar":
            {
                var marca = _servico.Marcar(Texto(raiz, "texto"));
                Emitir(w =>
                {
                    Id(w, id);
                    w.WriteString("type", "marcada");
                    w.WriteBoolean("ok", marca != null);
                    if (marca != null) w.WriteNumber("em", marca.EmSegundos);
                });
                break;
            }

            case "mudo":
            {
                if (raiz.TryGetProperty("valor", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _servico.Mudo.DefinirManual(v.GetBoolean());
                else
                    _servico.Mudo.AlternarManual();
                break;
            }

            case "estado": EmitirEstado(id); break;

            case "dispositivos":
                Emitir(w =>
                {
                    Id(w, id);
                    w.WriteString("type", "dispositivos");
                    Lista(w, "reproducao", DeviceCatalog.Reproducao());
                    Lista(w, "captura", DeviceCatalog.Captura());
                });
                break;

            case "sessoes":
                Emitir(w =>
                {
                    Id(w, id);
                    w.WriteString("type", "sessoes");
                    w.WritePropertyName("sessoes");
                    w.WriteStartArray();
                    foreach (var s in SessaoGravacao.Listar(_servico.Config.PastaSaidaEfetiva))
                    {
                        w.WriteStartObject();
                        w.WriteString("id", s.Id);
                        w.WriteString("titulo", s.Titulo);
                        w.WriteString("pasta", s.Pasta);
                        w.WriteString("inicio", s.Inicio.ToString("o"));
                        w.WriteNumber("duracaoSegundos", Math.Round(s.Duracao.TotalSeconds, 2));
                        w.WriteNumber("bytes", s.BytesEmDisco());
                        w.WriteNumber("capturas", s.QuantidadeDeCapturas);
                        w.WriteBoolean("temResumo", File.Exists(s.CaminhoResumo));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
                break;

            case "resumir":
            {
                var pasta = Texto(raiz, "pasta") ?? _servico.Sessao?.Pasta;
                var sessao = pasta != null ? SessaoGravacao.Abrir(pasta) : null;
                if (sessao == null) { Erro(id, "Sessão não encontrada."); break; }
                Task.Run(async () =>
                {
                    var r = await _servico.ResumirAsync(sessao,
                        new Progress<string>(e => Emitir(w => { w.WriteString("type", "etapa"); w.WriteString("mensagem", e); })))
                        .ConfigureAwait(false);
                    Emitir(w =>
                    {
                        Id(w, id);
                        w.WriteString("type", "resumo");
                        w.WriteBoolean("ok", r.Ok);
                        if (r.Ok) { w.WriteString("texto", r.Texto); w.WriteString("arquivo", sessao.CaminhoResumo); }
                        else w.WriteString("mensagem", r.Erro ?? "");
                    });
                });
                break;
            }

            case "conta":
            {
                var e = ContaClaude.Estado(_servico.Config);
                Emitir(w =>
                {
                    Id(w, id);
                    w.WriteString("type", "conta");
                    w.WriteBoolean("conectado", e.Conectado);
                    w.WriteString("meio", e.Meio.ToString());
                    w.WriteString("descricao", e.Descricao);
                    if (e.Email is { } m) w.WriteString("email", m);
                });
                break;
            }

            case "config":
                // recarrega do disco: a janela e a linha de comando compartilham o mesmo arquivo
                _servico.AplicarConfiguracao(AppSettings.Carregar());
                EmitirEstado(id);
                break;

            default:
                Erro(id, $"Comando desconhecido: {cmd}");
                break;
        }
    }

    private static void Lista(Utf8JsonWriter w, string nome, IReadOnlyList<DispositivoAudio> itens)
    {
        w.WritePropertyName(nome);
        w.WriteStartArray();
        foreach (var d in itens)
        {
            w.WriteStartObject();
            w.WriteString("id", d.Id);
            w.WriteString("nome", d.Nome);
            w.WriteBoolean("padrao", d.EhPadrao);
            w.WriteBoolean("padraoChamadas", d.EhPadraoComunicacao);
            w.WriteNumber("taxa", d.TaxaNativa);
            w.WriteNumber("canais", d.CanaisNativos);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void EmitirEstado(long? id = null) => Emitir(w =>
    {
        Id(w, id);
        w.WriteString("type", "estado");
        w.WriteString("estado", _servico.Estado.ToString());
        w.WriteNumber("segundos", Math.Round(_servico.Decorrido.TotalSeconds, 2));
        var mudo = _servico.Mudo.Estado;
        w.WriteBoolean("mudo", mudo.Mudo);
        w.WriteString("mudoDescricao", mudo.Descricao);
        w.WriteBoolean("emChamada", mudo.EmChamada);
        if (_servico.Sessao is { } s)
        {
            w.WriteString("pasta", s.Pasta);
            w.WriteNumber("capturas", s.QuantidadeDeCapturas);
        }
    });

    private static string? Texto(JsonElement raiz, string nome) =>
        raiz.TryGetProperty(nome, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static void Id(Utf8JsonWriter w, long? id)
    {
        if (id is { } v) w.WriteNumber("id", v);
    }

    private static void Erro(long? id, string mensagem) => Emitir(w =>
    {
        Id(w, id);
        w.WriteString("type", "error");
        w.WriteString("mensagem", mensagem);
    });

    private static void Emitir(Action<Utf8JsonWriter> corpo)
    {
        var buffer = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            SkipValidation = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            corpo(w);
            w.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        lock (TravaDeEscrita) _saida.WriteLine(json);
    }
}
