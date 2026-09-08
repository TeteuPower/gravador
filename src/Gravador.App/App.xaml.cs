using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Gravador.App.Core;
using Gravador.App.Views;
using Gravador.Core;
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

    /// <summary>Avisa as telas que a configuração mudou (dispositivos, atalhos, pastas).</summary>
    public static event Action? ConfiguracaoMudou;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Duas instâncias brigariam pelos atalhos globais e pelos dispositivos de áudio; a segunda
        // apenas traz a primeira para a frente.
        _instanciaUnica = new Mutex(true, @"Local\GravadorTeteuPower", out var primeira);
        if (!primeira)
        {
            JanelaPrincipal.TrazerInstanciaExistenteParaFrente();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Algo quebrou:\n\n{args.Exception.Message}", AppInfo.Nome,
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppInfo.GarantirPastas();
        Config = AppSettings.Carregar();
        Servico = new ServicoDeGravacao(Config);

        _bandeja = new TrayIcon(Servico);
        _bandeja.AbrirPedido += MostrarJanela;
        _bandeja.SairPedido += () => Encerrar();

        _atalhos = new HotkeyManager();
        RegistrarAtalhos();

        // A janela só é construída quando alguém vai vê-la.
        //
        // Abrir na bandeja é o modo de quem deixa o Gravador ligado o dia inteiro, e nesse caso a
        // janela nunca aparece: construí-la assim mesmo carregaria a árvore visual e as quatro
        // páginas para nada. O motor de gravação e a bandeja não dependem dela — é o que faz o
        // modo bandeja custar uma fração do modo com janela.
        // Verificação da interface: desenha as abas em PNG e sai. Ver Core/Smoke.cs.
        var render = Array.IndexOf(e.Args, "--render");
        if (render >= 0)
        {
            var destino = render + 1 < e.Args.Length ? e.Args[render + 1] : ".";
            var codigo = e.Args.Contains("--gravando")
                ? Smoke.RenderizarGravando(destino)
                : Smoke.Renderizar(destino);
            _atalhos?.Dispose();
            _bandeja?.Dispose();
            Servico.Dispose();
            Shutdown(codigo);
            return;
        }

        var naBandeja = Config.ComecarMinimizado || e.Args.Contains("--minimizado");
        if (!naBandeja) MostrarJanela();

        Servico.EstadoMudou += estado => Dispatcher.Invoke(() => _bandeja?.AtualizarEstado(estado));
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
