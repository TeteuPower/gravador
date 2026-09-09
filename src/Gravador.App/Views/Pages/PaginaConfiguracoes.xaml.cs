using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Gravador.Core;
using Gravador.Core.Audio;
using Gravador.Core.Muting;
using Gravador.Core.Settings;

namespace Gravador.App.Views.Pages;

public partial class PaginaConfiguracoes : UserControl
{
    private bool _carregando;

    /// <summary>Rótulo legível para um valor de enumeração, para a lista não mostrar nome de código.</summary>
    private sealed record Opcao<T>(T Valor, string Texto)
    {
        public override string ToString() => Texto;
    }

    public PaginaConfiguracoes()
    {
        InitializeComponent();
        TxtArquivoConfig.Text = "Tudo isto fica em " + Path.Combine(AppInfo.PastaDados, "config.json");

        MontarListas();

        SldGanhoSistema.ValueChanged += (_, _) => TxtGanhoSistema.Text = $"{SldGanhoSistema.Value:0.0}×";
        SldGanhoMicrofone.ValueChanged += (_, _) => TxtGanhoMicrofone.Text = $"{SldGanhoMicrofone.Value:0.0}×";
        SldLimiar.ValueChanged += (_, _) => TxtLimiar.Text = $"{SldLimiar.Value:0} dB";
        CmbFormato.SelectionChanged += (_, _) => AtualizarEstimativa();
        CmbKbps.SelectionChanged += (_, _) => AtualizarEstimativa();
        CmbTrilhas.SelectionChanged += (_, _) => AtualizarEstimativa();
        CmbTranscricao.SelectionChanged += (_, _) => AtualizarTranscricao();
        CmbWhisperModelo.SelectionChanged += (_, _) => AtualizarTranscricao();
        ChkDetectarApps.Checked += (_, _) => AtualizarAppsVigiados();
        ChkDetectarApps.Unchecked += (_, _) => AtualizarAppsVigiados();

        Recarregar();
    }

    private void MontarListas()
    {
        CmbTrilhas.ItemsSource = new[]
        {
            new Opcao<ModoTrilhas>(ModoTrilhas.Ambas, "Separadas e misturada (recomendado)"),
            new Opcao<ModoTrilhas>(ModoTrilhas.Separadas, "Só separadas — computador e microfone"),
            new Opcao<ModoTrilhas>(ModoTrilhas.Mixada, "Só a misturada — um arquivo"),
        };

        CmbFormato.ItemsSource = new[]
        {
            new Opcao<FormatoSaida>(FormatoSaida.Mp3, "MP3 — pequeno, aceito por qualquer IA"),
            new Opcao<FormatoSaida>(FormatoSaida.Wav, "WAV — sem perdas, enorme"),
        };

        CmbKbps.ItemsSource = new[] { 32, 48, 64, 96, 128 };

        CmbQuandoMudo.ItemsSource = new[]
        {
            new Opcao<AcaoQuandoMudo>(AcaoQuandoMudo.SilenciarNaMixagem, "Guardar na sua trilha, silenciar na mistura"),
            new Opcao<AcaoQuandoMudo>(AcaoQuandoMudo.SomenteMarcar, "Gravar tudo e só anotar na linha do tempo"),
            new Opcao<AcaoQuandoMudo>(AcaoQuandoMudo.SilenciarEmTudo, "Não gravar nada do que eu disser mudo"),
        };

        CmbAlvoCaptura.ItemsSource = new[]
        {
            new Opcao<AlvoCaptura>(AlvoCaptura.JanelaAtiva, "A janela que está na frente"),
            new Opcao<AlvoCaptura>(AlvoCaptura.MonitorAtivo, "O monitor onde ela está"),
            new Opcao<AlvoCaptura>(AlvoCaptura.TelaToda, "Todos os monitores"),
        };

        CmbFormatoImagem.ItemsSource = new[]
        {
            new Opcao<FormatoImagem>(FormatoImagem.Jpeg, "JPEG — leve"),
            new Opcao<FormatoImagem>(FormatoImagem.Png, "PNG — texto mais nítido, arquivo maior"),
        };

        CmbLarguraMaxima.ItemsSource = new[]
        {
            new Opcao<int>(1280, "1280 px"),
            new Opcao<int>(1920, "1920 px"),
            new Opcao<int>(2560, "2560 px"),
            new Opcao<int>(0, "tamanho original"),
        };

        CmbAutoCaptura.ItemsSource = new[]
        {
            new Opcao<int>(0, "nunca"),
            new Opcao<int>(30, "30 segundos"),
            new Opcao<int>(60, "1 minuto"),
            new Opcao<int>(300, "5 minutos"),
        };

        CmbTranscricao.ItemsSource = new[]
        {
            new Opcao<MotorTranscricao>(MotorTranscricao.Nenhum, "Nenhuma — só o áudio e as imagens"),
            new Opcao<MotorTranscricao>(MotorTranscricao.Windows, "Reconhecimento de fala do Windows"),
            new Opcao<MotorTranscricao>(MotorTranscricao.Remoto, "Serviço remoto (compatível com a API da OpenAI)"),
            new Opcao<MotorTranscricao>(MotorTranscricao.Whisper, "whisper.cpp local — offline, de graça, baixa o modelo uma vez"),
        };

        CmbWhisperModelo.ItemsSource = new[]
        {
            new Opcao<string>("base", "base — 148 MB, razoável, rápido (7× tempo real na CPU)"),
            new Opcao<string>("small", "small — 488 MB, bom"),
            new Opcao<string>("medium", "medium — 1,5 GB, o melhor que cabe numa CPU"),
        };

        CmbIdiomaDestino.ItemsSource = new[] { "pt-BR", "pt-PT", "en-US", "es-ES" };

        CmbIdioma.ItemsSource = new[] { "pt-BR", "pt-PT", "en-US", "es-ES" };
    }

    // ==================================================================

    public void Recarregar()
    {
        _carregando = true;
        var c = App.Config;

        CmbSistema.ItemsSource = DeviceCatalog.Reproducao();
        CmbMicrofone.ItemsSource = DeviceCatalog.Captura();
        SelecionarDispositivo(CmbSistema, c.DispositivoSistema);
        SelecionarDispositivo(CmbMicrofone, c.DispositivoMicrofone);

        ChkSistema.IsChecked = c.GravarSistema;
        ChkMicrofone.IsChecked = c.GravarMicrofone;
        SldGanhoSistema.Value = c.GanhoSistema;
        SldGanhoMicrofone.Value = c.GanhoMicrofone;

        Selecionar(CmbTrilhas, c.Trilhas);
        Selecionar(CmbFormato, c.Formato);
        CmbKbps.SelectedItem = ((int[])CmbKbps.ItemsSource).Contains(c.Mp3Kbps) ? c.Mp3Kbps : 64;
        ChkManterWav.IsChecked = c.ManterWav;
        TxtPastaSaida.Text = c.PastaSaida;

        Selecionar(CmbQuandoMudo, c.QuandoMudo);
        ChkDetectarApps.IsChecked = c.DetectarMudoDeApps;
        SldLimiar.Value = c.LimiarVozDb;

        Selecionar(CmbAlvoCaptura, c.AlvoDaCaptura);
        Selecionar(CmbFormatoImagem, c.FormatoDaCaptura);
        Selecionar(CmbLarguraMaxima, c.LarguraMaximaCaptura);
        Selecionar(CmbAutoCaptura, c.CapturaAutomaticaSegundos);

        TxtAtalhoGravar.Text = c.AtalhoGravar;
        TxtAtalhoPausar.Text = c.AtalhoPausar;
        TxtAtalhoCapturar.Text = c.AtalhoCapturar;
        TxtAtalhoMarcar.Text = c.AtalhoMarcar;

        Selecionar(CmbTranscricao, c.Transcricao);
        Selecionar(CmbWhisperModelo, c.WhisperModelo);
        CmbIdioma.Text = c.IdiomaTranscricao;
        CmbIdiomaDestino.Text = c.IdiomaDestino;
        ChkTraduzir.IsChecked = c.TraduzirQuandoIdiomaDiferente;
        ChkRecortar.IsChecked = c.ImportacaoRecortarNoConteudo;
        ChkManterMiniaturas.IsChecked = c.ImportacaoManterMiniaturas;
        ChkAoVivo.IsChecked = c.TranscreverAoVivo;
        TxtRemotoUrl.Text = c.RemotoUrl;
        TxtRemotoModelo.Text = c.RemotoModelo;
        TxtRemotoChave.Text = c.RemotoChaveEnv;

        ChkIniciarComWindows.IsChecked = StartupManagerAtivo();
        ChkFecharParaBandeja.IsChecked = c.FecharParaBandeja;
        ChkComecarMinimizado.IsChecked = c.ComecarMinimizado;

        _carregando = false;

        TxtGanhoSistema.Text = $"{c.GanhoSistema:0.0}×";
        TxtGanhoMicrofone.Text = $"{c.GanhoMicrofone:0.0}×";
        TxtLimiar.Text = $"{c.LimiarVozDb:0} dB";
        AtualizarEstimativa();
        AtualizarTranscricao();
        AtualizarAppsVigiados();
        AtualizarAtalhosRecusados();
        TxtSalvo.Text = "";
    }

    private static bool StartupManagerAtivo() => Core.StartupManager.Ativo;

    private static void Selecionar<T>(ComboBox caixa, T valor)
    {
        foreach (var item in caixa.ItemsSource)
            if (item is Opcao<T> o && EqualityComparer<T>.Default.Equals(o.Valor, valor))
            {
                caixa.SelectedItem = item;
                return;
            }
        caixa.SelectedIndex = 0;
    }

    private static T Valor<T>(ComboBox caixa, T padrao) =>
        caixa.SelectedItem is Opcao<T> o ? o.Valor : padrao;

    /// <summary>
    /// Um dispositivo que foi desconectado desde a última vez continua escolhido no arquivo, mas não
    /// existe na lista. Cair no padrão do Windows aqui evita a interface mostrar "nada selecionado"
    /// e, no salvar, apagar a escolha de quem só desplugou o fone por um minuto.
    /// </summary>
    private static void SelecionarDispositivo(ComboBox caixa, string id)
    {
        var lista = (IReadOnlyList<DispositivoAudio>)caixa.ItemsSource;
        caixa.SelectedItem = lista.FirstOrDefault(d => d.Id == id)
            ?? lista.FirstOrDefault(d => d.EhPadrao)
            ?? lista.FirstOrDefault();
    }

    private void AtualizarEstimativa()
    {
        if (CmbFormato.SelectedItem == null) return;

        var formato = Valor(CmbFormato, FormatoSaida.Mp3);
        var kbps = CmbKbps.SelectedItem is int k ? k : 64;
        var trilhas = Valor(CmbTrilhas, ModoTrilhas.Ambas);
        var quantidade = trilhas switch
        {
            ModoTrilhas.Ambas => 3,
            ModoTrilhas.Separadas => 2,
            _ => 1,
        };

        var porHora = formato == FormatoSaida.Wav
            ? 48000L * 2 * 3600
            : kbps * 1000L / 8 * 3600;

        CmbKbps.IsEnabled = formato == FormatoSaida.Mp3;
        ChkManterWav.IsEnabled = formato == FormatoSaida.Mp3;
        TxtEstimativa.Text = $"Cerca de {Formato.Tamanho(porHora * quantidade)} por hora de reunião "
                           + $"({quantidade} arquivo(s)).";
    }

    private void AtualizarTranscricao()
    {
        if (CmbTranscricao.SelectedItem == null) return;
        var motor = Valor(CmbTranscricao, MotorTranscricao.Nenhum);

        PainelRemoto.Visibility = motor == MotorTranscricao.Remoto ? Visibility.Visible : Visibility.Collapsed;
        PainelWhisper.Visibility = motor == MotorTranscricao.Whisper ? Visibility.Visible : Visibility.Collapsed;
        ChkAoVivo.IsEnabled = motor == MotorTranscricao.Windows;
        if (motor == MotorTranscricao.Whisper)
        {
            var modelo = Valor(CmbWhisperModelo, "base");
            var cli = Gravador.Core.Ferramentas.Ferramentas.WhisperCli.Disponivel;
            var mod = Gravador.Core.Ferramentas.Ferramentas.ModeloWhisper(modelo).Disponivel;
            TxtWhisperEstado.Text = cli && mod ? "whisper.cpp e o modelo já estão nesta máquina."
                : $"Ainda não baixado: {(cli ? "" : "whisper.cpp (9 MB)")}{(cli || mod ? "" : " e ")}{(mod ? "" : $"modelo {modelo}")}. Baixa sozinho na primeira transcrição.";
        }

        TxtTranscricaoDica.Text = motor switch
        {
            MotorTranscricao.Windows =>
                "Offline e de graça, mas feito para comando de voz: erra bastante em conversa corrida, "
                + "e só entende UMA voz — vale para o seu microfone, não para a reunião inteira. "
                + "Exige o pacote de fala do idioma instalado no Windows.",
            MotorTranscricao.Remoto =>
                "A melhor qualidade em português e a mais leve para a máquina: quem faz a conta é o "
                + "servidor. Roda quando a gravação termina, em pedaços de dez minutos. Custa por minuto de áudio.",
            MotorTranscricao.Whisper =>
                "Roda nesta máquina depois da gravação, sem chave e sem custo. Excelente em inglês, bom em "
                + "português. Medido: 48 min transcritos em 6m45s com o modelo base numa CPU comum.",
            _ => "A pasta da sessão fica pronta para você arrastar inteira para dentro de uma IA.",
        };

        if (motor == MotorTranscricao.Remoto)
        {
            var nome = TxtRemotoChave.Text.Trim();
            var definida = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(nome));
            TxtChaveEstado.Text = definida
                ? $"{nome} está definida nesta máquina."
                : $"{nome} NÃO está definida. Defina a variável de ambiente com a chave do serviço e "
                  + "reabra o Gravador — a chave nunca é guardada no arquivo de configuração.";
        }
    }

    private void AtualizarAppsVigiados()
    {
        TxtAppsVigiados.Text = ChkDetectarApps.IsChecked == true
            ? "Vigiados: " + string.Join(", ", MeetingApps.Conhecidos.Select(a => $"{a.Nome} ({a.Atalho})"))
              + ". Se você trocou o atalho no aplicativo, o palpite vai errar — corrija pelo botão na aba Gravar."
            : "Sem isto, só o mudo do próprio Windows é detectado. O botão de mudo do Teams, do Meet e do "
              + "Zoom passa despercebido.";
    }

    private void AtualizarAtalhosRecusados()
    {
        var recusados = App.AtalhosRecusados;
        TxtAtalhosRecusados.Visibility = recusados.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (recusados.Count > 0)
            TxtAtalhosRecusados.Text = "O Windows recusou: " + string.Join(", ", recusados)
                + ". Outro programa já usa essas combinações — escolha outras.";
    }

    // ==================================================================

    /// <summary>
    /// Captura a combinação apertada no campo.
    ///
    /// Digitar "Ctrl+Alt+P" à mão erraria o nome da tecla na primeira tentativa; apertar a
    /// combinação não tem como errar. As teclas modificadoras sozinhas são ignoradas — elas são o
    /// caminho até a combinação, não a combinação.
    /// </summary>
    private void AoTeclarAtalho(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox campo) return;

        var tecla = e.Key == Key.System ? e.SystemKey : e.Key;
        if (tecla is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        if (tecla == Key.Escape) { campo.Text = ""; return; }

        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Windows;

        if (mods == HotkeyModifiers.None)
        {
            campo.Text = "(use pelo menos Ctrl, Alt ou Shift)";
            return;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(tecla);
        campo.Text = new Hotkey(mods, vk).ToString();
    }

    private void AoFocarAtalho(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox campo) campo.SelectAll();
    }

    private void AoEscolherPasta(object sender, RoutedEventArgs e)
    {
        var dialogo = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Onde guardar as gravações",
            InitialDirectory = App.Config.PastaSaidaEfetiva,
        };
        if (dialogo.ShowDialog() == true) TxtPastaSaida.Text = dialogo.FolderName;
    }

    private void AoDesfazer(object sender, RoutedEventArgs e) => Recarregar();

    private void AoSalvar(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;
        var c = App.Config;

        c.DispositivoSistema = (CmbSistema.SelectedItem as DispositivoAudio)?.Id ?? "";
        c.DispositivoMicrofone = (CmbMicrofone.SelectedItem as DispositivoAudio)?.Id ?? "";
        c.GravarSistema = ChkSistema.IsChecked == true;
        c.GravarMicrofone = ChkMicrofone.IsChecked == true;
        c.GanhoSistema = SldGanhoSistema.Value;
        c.GanhoMicrofone = SldGanhoMicrofone.Value;

        c.Trilhas = Valor(CmbTrilhas, ModoTrilhas.Ambas);
        c.Formato = Valor(CmbFormato, FormatoSaida.Mp3);
        if (CmbKbps.SelectedItem is int kbps) c.Mp3Kbps = kbps;
        c.ManterWav = ChkManterWav.IsChecked == true;
        c.PastaSaida = TxtPastaSaida.Text.Trim();

        c.QuandoMudo = Valor(CmbQuandoMudo, AcaoQuandoMudo.SilenciarNaMixagem);
        c.DetectarMudoDeApps = ChkDetectarApps.IsChecked == true;
        c.LimiarVozDb = SldLimiar.Value;

        c.AlvoDaCaptura = Valor(CmbAlvoCaptura, AlvoCaptura.JanelaAtiva);
        c.FormatoDaCaptura = Valor(CmbFormatoImagem, FormatoImagem.Jpeg);
        c.LarguraMaximaCaptura = Valor(CmbLarguraMaxima, 1920);
        c.CapturaAutomaticaSegundos = Valor(CmbAutoCaptura, 0);

        c.AtalhoGravar = TxtAtalhoGravar.Text;
        c.AtalhoPausar = TxtAtalhoPausar.Text;
        c.AtalhoCapturar = TxtAtalhoCapturar.Text;
        c.AtalhoMarcar = TxtAtalhoMarcar.Text;

        c.Transcricao = Valor(CmbTranscricao, MotorTranscricao.Nenhum);
        c.WhisperModelo = Valor(CmbWhisperModelo, "base");
        c.IdiomaDestino = string.IsNullOrWhiteSpace(CmbIdiomaDestino.Text) ? "pt-BR" : CmbIdiomaDestino.Text.Trim();
        c.TraduzirQuandoIdiomaDiferente = ChkTraduzir.IsChecked == true;
        c.ImportacaoRecortarNoConteudo = ChkRecortar.IsChecked == true;
        c.ImportacaoManterMiniaturas = ChkManterMiniaturas.IsChecked == true;
        c.IdiomaTranscricao = string.IsNullOrWhiteSpace(CmbIdioma.Text) ? "pt-BR" : CmbIdioma.Text.Trim();
        c.TranscreverAoVivo = ChkAoVivo.IsChecked == true;
        c.RemotoUrl = TxtRemotoUrl.Text.Trim();
        c.RemotoModelo = TxtRemotoModelo.Text.Trim();
        c.RemotoChaveEnv = TxtRemotoChave.Text.Trim();

        c.IniciarComWindows = ChkIniciarComWindows.IsChecked == true;
        c.FecharParaBandeja = ChkFecharParaBandeja.IsChecked == true;
        c.ComecarMinimizado = ChkComecarMinimizado.IsChecked == true;

        App.SalvarConfiguracao();

        AtualizarAtalhosRecusados();
        AtualizarTranscricao();
        TxtSalvo.Text = App.Servico.EmAndamento
            ? "Salvo. Trilhas e dispositivos só mudam na próxima gravação."
            : "Salvo.";
    }
}
