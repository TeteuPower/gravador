using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Gravador.App.Core;
using Gravador.App.Views;
using Gravador.Core;
using Gravador.Core.Atualizacao;
using Gravador.Core.Audio;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.App;

public partial class App : Application
{
    private Mutex? _instanciaUnica;
    private TrayIcon? _bandeja;
    private HotkeyManager? _atalhos;

    /// <summary>O serviço vive na aplicação, não na janela: fechar a janela não pode parar a gravação.</summary>
    public static ServicoDeGravacao Servico { get; private set; } = null!;

    public static AppSettings Config { get; private set; } = null!;

    /// <summary>Um só para o processo inteiro: ele guarda a hora da última consulta ao GitHub.</summary>
    public static Atualizador Atualizacoes { get; } = new();

    /// <summary>Avisa as telas que a configuração mudou (dispositivos, atalhos, pastas).</summary>
    public static event Action? ConfiguracaoMudou;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Duas instâncias brigariam pelos atalhos globais e pelos dispositivos de áudio; a segunda
        // apenas traz a primeira para a frente.
        //
        // O modo --render fica de fora: ele não registra atalho nem abre dispositivo, só desenha
        // num PNG e sai. Passar pelo mutex fazia a verificação encerrar com código 0 sem ter
        // desenhado nada sempre que houvesse um Gravador aberto — um "passou" que não provava nada,
        // que é o pior defeito possível numa verificação.
        var modoRender = e.Args.Contains("--render");
        if (!modoRender)
        {
            _instanciaUnica = new Mutex(true, @"Local\GravadorTeteuPower", out var primeira);
            if (!primeira)
            {
                JanelaPrincipal.TrazerInstanciaExistenteParaFrente();
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Algo quebrou:\n\n{args.Exception.Message}", AppInfo.Nome,
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppInfo.GarantirPastas();
        Config = AppSettings.Carregar();

        // Verificação da interface: desenha em PNG e sai. Ver Core/Smoke.cs.
        //
        // Vem ANTES de montar o serviço, a bandeja e os atalhos de propósito: renderizar uma sessão
        // (que já existe em disco) não precisa do motor de áudio nem do vigia de mudo, e a thread
        // desse vigia é o que fazia o modo --render travar sem sair. Só o modo --render --gravando
        // precisa do serviço, e ele o cria por conta própria.
        var render = Array.IndexOf(e.Args, "--render");
        if (render >= 0)
        {
            var destino = render + 1 < e.Args.Length ? e.Args[render + 1] : ".";
            var sessaoArg = Array.IndexOf(e.Args, "--sessao");
            int codigo;
            if (sessaoArg >= 0 && sessaoArg + 1 < e.Args.Length)
            {
                // desenhar uma sessão que já existe não precisa do motor de áudio
                codigo = Smoke.RenderizarSessao(e.Args[sessaoArg + 1], destino);
            }
            else
            {
                // as telas da janela principal usam App.Servico (a aba Gravar liga nos eventos dele)
                Servico = new ServicoDeGravacao(Config);
                codigo = e.Args.Contains("--gravando") ? Smoke.RenderizarGravando(destino) : Smoke.Renderizar(destino);
                Servico.Dispose();
            }
            Shutdown(codigo);
            return;
        }

        Servico = new ServicoDeGravacao(Config);

        _bandeja = new TrayIcon(Servico);
        _bandeja.AbrirPedido += MostrarJanela;
        _bandeja.SairPedido += () => Encerrar();
        _bandeja.AtualizarPedido += AbrirAtualizacoes;

        _atalhos = new HotkeyManager();
        RegistrarAtalhos();

        // A janela só é construída quando alguém vai vê-la.
        //
        // Abrir na bandeja é o modo de quem deixa o Gravador ligado o dia inteiro, e nesse caso a
        // janela nunca aparece: construí-la assim mesmo carregaria a árvore visual e as páginas
        // para nada. O motor de gravação e a bandeja não dependem dela.
        var naBandeja = Config.ComecarMinimizado || e.Args.Contains("--minimizado");
        if (!naBandeja) MostrarJanela();

        Servico.EstadoMudou += estado => Dispatcher.Invoke(() => _bandeja?.AtualizarEstado(estado));

        _ = ProcurarAtualizacaoAoAbrir();
    }

    /// <summary>
    /// Procura versão nova alguns segundos depois de abrir, em segundo plano.
    ///
    /// Depois e não durante: a abertura já disputa CPU com o motor de áudio e os dispositivos, e
    /// uma consulta HTTP na frente disso atrasaria justamente o que a pessoa está esperando. E o
    /// resultado só vira anúncio — nada se instala sozinho.
    /// </summary>
    private async Task ProcurarAtualizacaoAoAbrir()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (!Config.VerificarAtualizacoes) return;

            var nova = await Atualizacoes.ProcurarAsync(Config);
            if (nova == null) return;

            // Avisa uma vez por versão: quem já viu e não quis não é incomodado de novo até sair
            // uma posterior.
            var jaAvisado = string.Equals(nova.Versao, Config.VersaoJaAnunciada, StringComparison.OrdinalIgnoreCase);
            Config.VersaoJaAnunciada = nova.Versao;
            Config.Salvar();

            Dispatcher.Invoke(() =>
            {
                // O item do menu fica sempre: é onde a pessoa volta a achar isto depois.
                _bandeja?.AnunciarAtualizacao(nova.Versao);

                // O balão, não. Um pop-up no canto da tela no meio de uma reunião gravada aparece
                // na gravação de tela de quem estiver compartilhando.
                if (!jaAvisado && !Servico.EmAndamento)
                    _bandeja?.Avisar(AppInfo.Nome,
                        $"Versão {nova.Versao} disponível. Clique aqui para atualizar.");
            });
        }
        catch
        {
            // sem rede, GitHub fora do ar, app fechando no meio: atualização é acessório
        }
    }

    /// <summary>Abre a janela na aba de configurações, rolada até o cartão de atualização.</summary>
    private static void AbrirAtualizacoes()
    {
        MostrarJanela();
        if (Current.MainWindow is JanelaPrincipal janela) janela.IrParaAtualizacoes();
    }

    /// <summary>
    /// (Re)registra os atalhos globais. Chamado no início e sempre que as configurações mudam.
    ///
    /// Uma combinação recusada pelo Windows (já tomada por outro programa) não impede as outras:
    /// o que falhou é anotado e a tela de configurações mostra quais são.
    /// </summary>
    public static void RegistrarAtalhos()
    {
        var app = (App)Current;
        var a = app._atalhos;
        if (a == null) return;

        a.UnregisterAll();
        a.Register(Config.HotkeyGravar, () => app.AlternarGravacao());
        a.Register(Config.HotkeyPausar, () => Servico.AlternarPausa());
        a.Register(Config.HotkeyCapturar, () => Servico.Capturar());
        a.Register(Config.HotkeyMarcar, () => Servico.Marcar());
    }

    public static System.Collections.Generic.IReadOnlyList<string> AtalhosRecusados =>
        ((App)Current)._atalhos?.Falhas ?? Array.Empty<string>();

    /// <summary>Grava a configuração e reaplica tudo o que depende dela.</summary>
    public static void SalvarConfiguracao()
    {
        Config.Sanear();
        Config.Salvar();
        Servico.AplicarConfiguracao(Config);
        RegistrarAtalhos();
        StartupManager.Aplicar(Config.IniciarComWindows);
        ConfiguracaoMudou?.Invoke();
    }

    private async void AlternarGravacao()
    {
        try
        {
            if (Servico.PodeIniciar) Servico.Iniciar();
            else if (Servico.EmAndamento) await Servico.PararAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppInfo.Nome, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void MostrarJanela()
    {
        var app = (App)Current;
        if (app.MainWindow is not JanelaPrincipal janela)
        {
            janela = new JanelaPrincipal();
            app.MainWindow = janela;
        }
        janela.Show();
        if (janela.WindowState == WindowState.Minimized) janela.WindowState = WindowState.Normal;
        janela.Activate();
    }

    /// <summary>
    /// Sai de verdade. Uma gravação em andamento é fechada primeiro — os WAV já estão íntegros em
    /// disco, mas a linha do tempo e a conversão precisam do fecho.
    /// </summary>
    public static void Encerrar()
    {
        var app = (App)Current;
        if (Servico.EmAndamento)
        {
            var r = MessageBox.Show(
                "Tem uma gravação em andamento. Encerrar agora salva o que já foi gravado (sem converter para MP3).\n\nSair mesmo assim?",
                AppInfo.Nome, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            Servico.Motor.AbortarSalvando();
        }

        app._atalhos?.Dispose();
        app._bandeja?.Dispose();
        Servico.Dispose();
        app.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanciaUnica?.Dispose();
        base.OnExit(e);
    }
}
