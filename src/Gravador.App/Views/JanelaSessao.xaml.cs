using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gravador.Core;
using Gravador.Core.Claude;
using Gravador.Core.Importacao;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.App.Views;

/// <summary>
/// Uma sessão aberta: slides, linha do tempo, textos e a conversa com o Claude.
///
/// É janela própria, e não uma aba, para poder ficar aberta enquanto outra reunião é gravada — a
/// revisão da apresentação de terça acontece durante a chamada de quarta.
/// </summary>
public partial class JanelaSessao : Window
{
    private readonly SessaoGravacao _sessao;
    private readonly Conversa _conversa;
    private readonly ObservableCollection<ItemDeQuadro> _quadros = new();
    private CancellationTokenSource? _cancelamento;
    private TextBlock? _respostaEmCurso;

    public JanelaSessao(SessaoGravacao sessao)
    {
        InitializeComponent();
        _sessao = sessao;
        _conversa = new Conversa(sessao, App.Config);
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        ListaQuadros.ItemsSource = _quadros;
        Recarregar();
        MostrarHistorico();
    }

    private void Recarregar()
    {
        Title = _sessao.Titulo;
        TxtTitulo.Text = _sessao.Titulo;

        var partes = new List<string>
        {
            $"{_sessao.Inicio.LocalDateTime:dd/MM/yyyy 'às' HH:mm}",
            Formato.Duracao(_sessao.Duracao),
            _sessao.Origem == OrigemDaSessao.Importada ? "importada" + (_sessao.ArquivoOriginal != null ? $" de {Path.GetFileName(_sessao.ArquivoOriginal)}" : "") : "gravada ao vivo",
        };
        if (!string.IsNullOrEmpty(_sessao.Idioma)) partes.Add("fala em " + Tradutor.NomeDoIdioma(_sessao.Idioma));
        if (_sessao.Falas.Count > 0) partes.Add($"{_sessao.Falas.Count} trechos transcritos");
        TxtMeta.Text = string.Join(" · ", partes);

        BtnTranscrever.Content = _sessao.Falas.Count > 0 ? "Transcrever de novo" : "Transcrever";
        BtnTraduzir.IsEnabled = _sessao.Falas.Count > 0;
        BtnTraduzir.Content = _sessao.TemTraducao ? "Traduzir de novo" : "Traduzir";
        BtnResumir.Content = _sessao.TemResumo ? "Resumir de novo" : "Resumir com o Claude";

        _quadros.Clear();
        foreach (var m in _sessao.Marcas.Where(m => m.Tipo == TipoDeMarca.Captura && m.Arquivo != null))
        {
            var caminho = Path.Combine(_sessao.Pasta, m.Arquivo!);
            if (File.Exists(caminho)) _quadros.Add(new ItemDeQuadro(caminho, m.Carimbo, m.Texto ?? "captura"));
        }
        TxtSemQuadros.Visibility = _quadros.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var linha = new List<Marca>();
        foreach (var c in _sessao.Capitulos) linha.Add(new Marca(c.EmSegundos, TipoDeMarca.Marcador, "§ " + c.Titulo));
        linha.AddRange(_sessao.Marcas.Where(m => m.Tipo is TipoDeMarca.Marcador or TipoDeMarca.Mudo or TipoDeMarca.Aberto or TipoDeMarca.Pausa or TipoDeMarca.Retomada));
        foreach (var t in _sessao.TrechosDeVideo.Where(t => t.Tipo == TipoDeTrecho.NaoPertinente))
            linha.Add(new Marca(t.DeSegundos, TipoDeMarca.Marcador, $"descartado até {Formato.Carimbo(t.AteSegundos)} (outra janela na frente)"));
        ListaLinhaDoTempo.ItemsSource = linha.OrderBy(m => m.EmSegundos).ToList();

        MostrarTexto();
    }

    // ==================================================================

    private void AoTrocarTexto(object sender, RoutedEventArgs e) => MostrarTexto();

    private void MostrarTexto()
    {
        if (TxtCorpo == null) return;
        var aba = new[] { AbaResumo, AbaTraducao, AbaTranscricao, AbaConversaMd }.FirstOrDefault(a => a.IsChecked == true)?.Tag as string ?? "resumo";
        var caminho = aba switch
        {
            "traducao" => _sessao.CaminhoTraducao,
            "transcricao" => _sessao.CaminhoTranscricao,
            "conversa" => _sessao.CaminhoConversa,
            _ => _sessao.CaminhoResumo,
        };
        var vazio = aba switch
        {
            "traducao" => _sessao.Falas.Count == 0 ? "Sem transcrição ainda — traduzir vem depois dela." : "Ainda não traduzida. Use o botão Traduzir.",
            "transcricao" => "Ainda não transcrita. Use o botão Transcrever (whisper local, sem chave; a primeira vez baixa o modelo).",
            "conversa" => "A conversa com o Claude fica registrada aqui, e no arquivo conversa.md da pasta.",
            _ => "Ainda não há resumo. Use o botão \"Resumir com o Claude\" — ele lê a transcrição e os slides pelas ferramentas do Gravador.",
        };
        try
        {
            TxtCorpo.Text = File.Exists(caminho) ? File.ReadAllText(caminho) : vazio;
        }
        catch (Exception ex)
        {
            TxtCorpo.Text = "Não deu para ler: " + ex.Message;
        }
    }

    private void AoClicarQuadro(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string caminho) Abrir(caminho);
    }

    private void AoAbrirPasta(object sender, RoutedEventArgs e) => Abrir(_sessao.Pasta);

    private static void Abrir(string caminho)
    {
        try { Process.Start(new ProcessStartInfo(caminho) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Não deu para abrir: " + ex.Message, AppInfo.Nome); }
    }

    // ==================================================================

    private async void AoTranscrever(object sender, RoutedEventArgs e)
    {
        if (_sessao.Falas.Count > 0)
        {
            var r = MessageBox.Show("Já existe transcrição. Refazer com o whisper local?", AppInfo.Nome, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _sessao.SubstituirFalas(Array.Empty<TrechoFalado>());
        }
        await Executar(async (etapa, ct) =>
        {
            await PosProcessamento.ExecutarAsync(_sessao, App.Config, new PosProcessamento.Opcoes
            {
                Motor = App.Config.Transcricao is MotorTranscricao.Remoto ? MotorTranscricao.Remoto : MotorTranscricao.Whisper,
                Idioma = string.IsNullOrEmpty(_sessao.Idioma) ? "auto" : _sessao.Idioma,
                Traduzir = App.Config.TraduzirQuandoIdiomaDiferente,
                Resumir = false,
            }, etapa, a => Dispatcher.Invoke(() => TxtEstado.Text = "Aviso: " + a), ct);
        });
        AbaTranscricao.IsChecked = true;
    }

    private async void AoTraduzir(object sender, RoutedEventArgs e)
    {
        await Executar(async (etapa, ct) =>
        {
            var conta = ContaClaude.Estado(App.Config);
            if (!conta.Conectado) throw new InvalidOperationException(conta.Descricao);
            var r = await Tradutor.TraduzirAsync(_sessao, App.Config, PosProcessamento.TokenDe(conta), etapa, ct);
            if (!r.Ok) throw new InvalidOperationException(r.Erro);
            etapa.Report($"Tradução pronta ({r.TokensEntrada:N0} tokens de entrada, {r.TokensSaida:N0} de saída).");
        });
        AbaTraducao.IsChecked = true;
    }

    private async void AoResumir(object sender, RoutedEventArgs e)
    {
        await Executar(async (etapa, ct) =>
        {
            var r = await new Analista().ResumirAsync(_sessao, App.Config, etapa, ct);
            if (!r.Ok) throw new InvalidOperationException(r.Erro);
            etapa.Report($"Resumo pronto em {Formato.Duracao(r.Duracao)} — {r.TokensEntrada:N0} tokens de entrada, {r.Turnos} turno(s).");
        });
        AbaResumo.IsChecked = true;
    }

    /// <summary>Roda uma tarefa longa com estado no rodapé, botões travados e cancelamento.</summary>
    private async Task Executar(Func<IProgress<string>, CancellationToken, Task> tarefa)
    {
        if (_cancelamento != null) return;
        _cancelamento = new CancellationTokenSource();
        foreach (var b in new[] { BtnTranscrever, BtnTraduzir, BtnResumir }) b.IsEnabled = false;
        BtnCancelar.Visibility = Visibility.Visible;
        var progresso = new Progress<string>(t => TxtEstado.Text = t);
        try
        {
            await tarefa(progresso, _cancelamento.Token);
        }
        catch (OperationCanceledException)
        {
            TxtEstado.Text = "Cancelado.";
        }
        catch (Exception ex)
        {
            TxtEstado.Text = "Não deu: " + ex.Message;
        }
        finally
        {
            _cancelamento.Dispose();
            _cancelamento = null;
            BtnCancelar.Visibility = Visibility.Collapsed;
            foreach (var b in new[] { BtnTranscrever, BtnTraduzir, BtnResumir }) b.IsEnabled = true;
            Recarregar();
        }
    }

    private void AoCancelar(object sender, RoutedEventArgs e) => _cancelamento?.Cancel();

    // ==================================================================

    private void MostrarHistorico()
    {
        foreach (var m in _conversa.Historico) AdicionarBalao(m.Papel, m.Texto);
    }

    private TextBlock AdicionarBalao(string papel, string texto)
    {
        TxtConversaVazia.Visibility = Visibility.Collapsed;
        var voce = papel == "você";
        var borda = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(voce ? 40 : 0, 0, voce ? 0 : 24, 10),
            Background = (Brush)FindResource(voce ? "SuperficieAlta" : "Superficie"),
            BorderBrush = (Brush)FindResource(voce ? "Acento" : "Borda"),
            BorderThickness = new Thickness(1),
        };
        var bloco = new TextBlock
        {
            Text = texto,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
        };
        borda.Child = bloco;
        PainelConversa.Children.Add(borda);
        RolagemConversa.ScrollToEnd();
        return bloco;
    }

    private void AoTeclarNaPergunta(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            AoEnviar(sender, e);
        }
    }

    private async void AoEnviar(object sender, RoutedEventArgs e)
    {
        var pergunta = TxtPergunta.Text.Trim();
        if (pergunta.Length == 0 || _conversa.Ocupada) return;

        TxtPergunta.Text = "";
        BtnEnviar.IsEnabled = false;
        AdicionarBalao("você", pergunta);
        _respostaEmCurso = AdicionarBalao("claude", "");
        TxtAtividade.Visibility = Visibility.Visible;
        TxtAtividade.Text = "pensando...";

        var r = await _conversa.PerguntarAsync(pergunta,
            aoReceberTexto: t => Dispatcher.Invoke(() =>
            {
                if (_respostaEmCurso == null) return;
                _respostaEmCurso.Text += t;
                RolagemConversa.ScrollToEnd();
            }),
            aoChamarFerramenta: f => Dispatcher.Invoke(() => TxtAtividade.Text = "lendo: " + f.Replace("mcp__gravador__", "")));

        if (!r.Ok && _respostaEmCurso != null)
            _respostaEmCurso.Text = "Não deu: " + r.Erro;
        else if (_respostaEmCurso != null && string.IsNullOrWhiteSpace(_respostaEmCurso.Text))
            _respostaEmCurso.Text = r.Texto;

        TxtAtividade.Text = r.Ok
            ? $"{r.TokensEntrada:N0} tokens de entrada · {r.TokensCacheLidos:N0} do cache · {r.TokensSaida:N0} de saída · {Formato.Duracao(r.Duracao)}"
            : "";
        _respostaEmCurso = null;
        BtnEnviar.IsEnabled = true;
        TxtPergunta.Focus();
        if (AbaConversaMd.IsChecked == true) MostrarTexto();
    }

    private void AoRecomecarConversa(object sender, RoutedEventArgs e)
    {
        _conversa.Reiniciar();
        PainelConversa.Children.Clear();
        PainelConversa.Children.Add(TxtConversaVazia);
        TxtConversaVazia.Visibility = Visibility.Visible;
        TxtAtividade.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancelamento?.Cancel();
        base.OnClosed(e);
    }

    /// <summary>Uma imagem da lista, decodificada pequena: 50 slides a 2560 px inteiros em memória seria meio giga.</summary>
    private sealed class ItemDeQuadro
    {
        public ItemDeQuadro(string caminho, string carimbo, string rotulo)
        {
            Caminho = caminho;
            Carimbo = carimbo;
            Rotulo = rotulo;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(caminho);
                bmp.DecodePixelWidth = 280;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Imagem = bmp;
            }
            catch
            {
                Imagem = null;
            }
        }

        public string Caminho { get; }
        public string Carimbo { get; }
        public string Rotulo { get; }
        public ImageSource? Imagem { get; }
    }
}
