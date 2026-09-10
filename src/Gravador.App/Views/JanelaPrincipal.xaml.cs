using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gravador.Core;
using Gravador.Core.Audio;

namespace Gravador.App.Views;

public partial class JanelaPrincipal : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _relogio;

    public JanelaPrincipal()
    {
        InitializeComponent();
        TxtVersao.Text = "versão " + AppInfo.Versao;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));

        // Meio segundo é suficiente para o cronômetro da lateral: quem quer precisão está olhando
        // a página de gravar, que se atualiza pelos eventos do motor.
        _relogio = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _relogio.Tick += (_, _) => AtualizarLateral();
        _relogio.Start();

        App.Servico.EstadoMudou += _ => Dispatcher.Invoke(AtualizarLateral);
        AtualizarLateral();
    }

    private void AoTrocarAba(object sender, RoutedEventArgs e)
    {
        if (PgGravar == null) return; // ainda montando

        var aba = (sender as FrameworkElement)?.Tag as string;
        PgGravar.Visibility = aba == "gravar" ? Visibility.Visible : Visibility.Collapsed;
        PgSessoes.Visibility = aba == "sessoes" ? Visibility.Visible : Visibility.Collapsed;
        PgConfig.Visibility = aba == "config" ? Visibility.Visible : Visibility.Collapsed;
        PgConta.Visibility = aba == "conta" ? Visibility.Visible : Visibility.Collapsed;

        // As listas só custam disco quando a aba aparece: abrir o programa não deve varrer a pasta
        // de gravações nem consultar a conta.
        if (aba == "sessoes") PgSessoes.Recarregar();
        if (aba == "config") PgConfig.Recarregar();
        if (aba == "conta") PgConta.Recarregar();
    }

    /// <summary>
    /// Abre a aba de configurações já rolada até o cartão de atualização.
    ///
    /// O menu da bandeja anuncia a versão nova, mas quem instala é um botão que fica no fim de uma
    /// página longa: sem levar a pessoa até ele, o anúncio vira uma caça ao tesouro.
    /// </summary>
    public void IrParaAtualizacoes()
    {
        AbaConfig.IsChecked = true;
        PgConfig.Recarregar();
        PgConfig.MostrarAtualizacoes();
    }

    private void AtualizarLateral()
    {
        var estado = App.Servico.Estado;
        TxtCronometroLateral.Text = Formato.Cronometro(App.Servico.Decorrido);

        var (texto, cor) = estado switch
        {
            EstadoGravacao.Gravando => ("Gravando", "Gravando"),
            EstadoGravacao.Pausada => ("Pausado", "Aviso"),
            EstadoGravacao.Finalizando => ("Salvando", "Aviso"),
            _ => ("Parado", "TextoFraco"),
        };
        TxtEstadoCurto.Text = texto;
        LuzEstado.Fill = (Brush)FindResource(cor);
    }

    /// <summary>
    /// Fechar no X manda para a bandeja em vez de encerrar.
    ///
    /// Sem isto, um clique errado no meio de uma reunião perderia a gravação — e a pessoa só
    /// descobriria no fim. Quem quer sair de verdade usa o menu da bandeja, que confirma.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Config.FecharParaBandeja)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (App.Servico.EmAndamento)
        {
            e.Cancel = true;
            App.Encerrar();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Segunda instância: traz a janela da primeira para a frente em vez de abrir outra.</summary>
    public static void TrazerInstanciaExistenteParaFrente()
    {
        try
        {
            var atual = Process.GetCurrentProcess();
            var outra = Process.GetProcessesByName(atual.ProcessName)
                .FirstOrDefault(p => p.Id != atual.Id && p.MainWindowHandle != IntPtr.Zero);
            if (outra == null) return;
            ShowWindow(outra.MainWindowHandle, SwRestore);
            SetForegroundWindow(outra.MainWindowHandle);
        }
        catch
        {
            // a outra instância pode estar só na bandeja, sem janela: nada a trazer
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
