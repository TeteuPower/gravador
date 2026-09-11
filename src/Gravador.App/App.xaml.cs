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
            // No modo --render, quebrar tem que FALHAR, não abrir uma caixa de diálogo.
            //
            // A caixa espera alguém clicar em OK. Numa verificação — na esteira ou aqui na máquina —
            // não há ninguém, então o processo fica pendurado até o tempo acabar, e o pouco que se
            // aprende é "travou". Pior: a caixa aparece na tela de quem estiver usando o computador,
            // vinda de um processo que deveria ser invisível. Foi assim que um StaticResource
            // faltando virou um pop-up no meio do trabalho de outra pessoa.
            if (e.Args.Contains("--render"))
            {
                Console.Error.WriteLine("O --render quebrou: " + args.Exception);
                Environment.Exit(3);
            }

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
        //
        // Voltar de uma atualização é a exceção, e ela vence até o "abrir minimizado": quem
        // atualizou acabou de apertar um botão DENTRO da janela, e o programa reaparecer escondido
        // na bandeja faz parecer que ele não voltou.
        // Duas formas de saber que acabamos de ser atualizados.
        //
        // O argumento é o sinal do instalador, e ele vale já na próxima atualização: quem roda é o
        // instalador NOVO, baixado, que traz a linha `[Run]` nova.
        //
        // A comparação de versões cobre o que o argumento não alcança — quem baixou o .exe da
        // release e rodou na mão, e qualquer instalador antigo que ainda mande `--minimizado`.
        var versaoAnterior = Config.UltimaVersaoExecutada;
        var trocouDeVersao = versaoAnterior.Length > 0 && versaoAnterior != AppInfo.Versao;
        if (versaoAnterior != AppInfo.Versao)
        {
            Config.UltimaVersaoExecutada = AppInfo.Versao;
            Config.Salvar();
        }

        var voltandoDeAtualizacao = e.Args.Contains("--apos-atualizar") || trocouDeVersao;
        var naBandeja = !voltandoDeAtualizacao
                        && (Config.ComecarMinimizado || e.Args.Contains("--minimizado"));
        if (!naBandeja) MostrarJanela();

        Servico.EstadoMudou += estado => Dispatcher.Invoke(() => _bandeja?.AtualizarEstado(estado));

        if (voltandoDeAtualizacao) AvisarQueAtualizou(versaoAnterior);
        _ = ProcurarAtualizacaoAoAbrir();
    }

    /// <summary>
    /// Confirma, na volta, que a troca deu certo.
    ///
    /// A janela reaparecendo já diz que o programa voltou, mas não diz em qual versão — e é
    /// exatamente isso que quem apertou "instalar" quer saber. A versão anunciada aqui é lida do
    /// executável que está rodando, então ela é a prova, e não a promessa.
    ///
    /// Também zera a "versão já anunciada": sem isso, o balão da próxima atualização seria
    /// engolido, porque a anotação ainda apontaria para a versão que acabou de ser instalada.
    /// </summary>
    private void AvisarQueAtualizou(string versaoAnterior)
    {
        Config.VersaoJaAnunciada = "";
        Config.Salvar();

        var de = versaoAnterior.Length > 0 ? $" (era a {versaoAnterior})" : "";
        _bandeja?.Avisar(AppInfo.Nome, $"Atualizado para a versão {AppInfo.Versao}{de}.");
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
