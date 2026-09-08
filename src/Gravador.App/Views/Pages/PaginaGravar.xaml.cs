using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gravador.Core;
using Gravador.Core.Audio;
using Gravador.Core.Muting;
using Gravador.Core.Session;

namespace Gravador.App.Views.Pages;

public partial class PaginaGravar : UserControl
{
    private readonly ObservableCollection<Marca> _marcas = new();
    private readonly ObservableCollection<string> _avisos = new();
    private bool _ocupado;

    public PaginaGravar()
    {
        InitializeComponent();
        ListaMarcas.ItemsSource = _marcas;
        ListaAvisos.ItemsSource = _avisos;

        var s = App.Servico;
        s.EstadoMudou += e => Dispatcher.Invoke(() => AplicarEstado(e));
        s.Niveis += n => Dispatcher.Invoke(() => AplicarNiveis(n));
        s.Progrediu += t => Dispatcher.Invoke(() => TxtCronometro.Text = Formato.Cronometro(t));
        s.Marcou += m => Dispatcher.Invoke(() => AdicionarMarca(m));
        s.Aviso += a => Dispatcher.Invoke(() => AdicionarAviso(a));
        s.Mudo.Mudou += e => Dispatcher.Invoke(() => AplicarMudo(e));
        s.LegendaParcial += t => Dispatcher.Invoke(() =>
        {
            TxtLegenda.Text = "… " + t;
            TxtLegenda.Visibility = string.IsNullOrWhiteSpace(t) ? Visibility.Collapsed : Visibility.Visible;
        });

        App.ConfiguracaoMudou += AplicarConfiguracao;
        AplicarConfiguracao();
        AplicarEstado(s.Estado);
        AplicarMudo(s.Mudo.Estado);
    }

    private void AplicarConfiguracao()
    {
        var c = App.Config;
        TxtPasta.Text = "As gravações vão para " + c.PastaSaidaEfetiva;
        TxtAtalhos.Text = $"Atalhos: {c.AtalhoGravar} gravar · {c.AtalhoPausar} pausar · "
                        + $"{c.AtalhoCapturar} capturar a tela · {c.AtalhoMarcar} marcar";

        TxtDispSistema.Text = NomeDoDispositivo(DeviceCatalog.Reproducao(), c.DispositivoSistema, c.GravarSistema);
        TxtDispMicrofone.Text = NomeDoDispositivo(DeviceCatalog.Captura(), c.DispositivoMicrofone, c.GravarMicrofone);

        foreach (var recusado in App.AtalhosRecusados)
            AdicionarAviso($"O Windows recusou o atalho {recusado} — outro programa já o usa. Troque em Configurações.");
    }

    private static string NomeDoDispositivo(System.Collections.Generic.IReadOnlyList<DispositivoAudio> lista,
        string id, bool ligado)
    {
        if (!ligado) return "desligado nas configurações";
        var achado = string.IsNullOrWhiteSpace(id)
            ? lista.FirstOrDefault(d => d.EhPadrao) ?? lista.FirstOrDefault(d => d.EhPadraoComunicacao) ?? lista.FirstOrDefault()
            : lista.FirstOrDefault(d => d.Id == id) ?? lista.FirstOrDefault(d => d.EhPadrao);
        return achado?.Nome ?? "nenhum dispositivo";
    }

    // ==================================================================

    private async void AoClicarGravar(object sender, RoutedEventArgs e)
    {
        if (_ocupado) return;
        _ocupado = true;
        try
        {
            if (App.Servico.PodeIniciar)
            {
                _marcas.Clear();
                _avisos.Clear();
                CartaoAviso.Visibility = Visibility.Collapsed;
                App.Servico.Iniciar();
            }
            else if (App.Servico.EmAndamento)
            {
                var resultado = await App.Servico.PararAsync(
                    new Progress<string>(etapa => TxtSubestado.Text = etapa));

                foreach (var aviso in resultado.Avisos) AdicionarAviso(aviso);
                TxtSubestado.Text = $"salvo em {resultado.Sessao.Pasta}";
            }
        }
        catch (Exception ex)
        {
            AdicionarAviso(ex.Message);
            MessageBox.Show(ex.Message, AppInfo.Nome, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _ocupado = false;
        }
    }

    private void AoClicarPausar(object sender, RoutedEventArgs e) => App.Servico.AlternarPausa();

    private void AoClicarCapturar(object sender, RoutedEventArgs e)
    {
        var imagem = App.Servico.Capturar();
        // Fora de gravação a captura não vira marca; sem este aviso ela parece não ter acontecido.
        if (imagem != null && !App.Servico.EmAndamento)
            TxtSubestado.Text = "captura avulsa salva em " + imagem.Caminho;
    }

    private void AoClicarMarcar(object sender, RoutedEventArgs e) => App.Servico.Marcar();

    private void AoClicarMudo(object sender, RoutedEventArgs e) => App.Servico.AlternarMudoManual();

    private void AoClicarAbrirPasta(object sender, RoutedEventArgs e)
    {
        var pasta = App.Servico.Sessao?.Pasta ?? App.Config.PastaSaidaEfetiva;
        try
        {
            System.IO.Directory.CreateDirectory(pasta);
            Process.Start(new ProcessStartInfo(pasta) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AdicionarAviso("Não deu para abrir a pasta: " + ex.Message);
        }
    }

    // ==================================================================

    private void AplicarEstado(EstadoGravacao estado)
    {
        var gravando = estado == EstadoGravacao.Gravando;
        var emAndamento = estado is EstadoGravacao.Gravando or EstadoGravacao.Pausada;

        BtnGravar.Content = estado switch
        {
            EstadoGravacao.Parada => "Gravar",
            EstadoGravacao.Finalizando => "Salvando...",
            _ => "Parar e salvar",
        };
        BtnGravar.IsEnabled = estado != EstadoGravacao.Finalizando;
        BtnGravar.Background = (Brush)FindResource(emAndamento ? "SuperficieAlta" : "Gravando");
        BtnGravar.BorderBrush = (Brush)FindResource(emAndamento ? "Borda" : "Gravando");
        BtnGravar.Foreground = (Brush)FindResource(emAndamento ? "Texto" : "Texto");

        BtnPausar.IsEnabled = emAndamento;
        BtnPausar.Content = estado == EstadoGravacao.Pausada ? "Retomar" : "Pausar";
        BtnMarcar.IsEnabled = emAndamento;

        if (estado == EstadoGravacao.Parada) TxtCronometro.Text = Formato.Cronometro(TimeSpan.Zero);

        TxtSubestado.Text = estado switch
        {
            EstadoGravacao.Gravando => "gravando " + (App.Servico.Sessao?.Titulo ?? ""),
            EstadoGravacao.Pausada => "pausado — o tempo parado não entra no arquivo",
            EstadoGravacao.Finalizando => "fechando os arquivos...",
            _ => "pronto para gravar",
        };

        if (!gravando)
        {
            NivelSistema.Width = 0;
            NivelMicrofone.Width = 0;
        }
    }

    private void AplicarNiveis(NiveisAudio n)
    {
        // A largura é calculada aqui, e não por conversor de binding: são cinco atualizações por
        // segundo durante horas, e um conversor por quadro é trabalho que não precisa existir.
        NivelSistema.Width = TrilhoSistema.ActualWidth * Math.Clamp(n.Sistema, 0, 1);
        NivelMicrofone.Width = TrilhoMicrofone.ActualWidth * Math.Clamp(n.Microfone, 0, 1);

        NivelMicrofone.Background = (Brush)FindResource(
            App.Servico.Mudo.Mudo ? "TextoFraco" : n.VoceFalando ? "Bom" : "Acento");
    }

    private void AplicarMudo(EstadoDoMudo e)
    {
        TxtMudo.Text = e.Mudo ? "Microfone mudo" : "Microfone aberto";
        LuzMudo.Fill = (Brush)FindResource(e.Mudo ? (e.Certeza ? "Gravando" : "Aviso") : "Bom");
        BtnMudo.Content = e.Mudo ? "Voltei a falar" : "Estou mudo";

        // A distinção entre "o Windows confirma" e "a ferramenta achou" é o ponto todo: quem for
        // usar a gravação depois precisa saber o quanto pode confiar na marcação.
        var confianca = !e.Mudo ? ""
            : e.Certeza ? " O Windows confirma: ninguém está te ouvindo."
            : " Isto é um palpite pelo atalho do aplicativo — corrija no botão se estiver errado.";

        var chamada = e.EmChamada && e.AppEmChamada is { } app
            ? $"{app} está com o microfone aberto."
            : "Nenhum aplicativo de reunião usando o microfone agora.";

        TxtMudoDetalhe.Text = (e.Mudo ? char.ToUpper(e.Descricao[0]) + e.Descricao[1..] + "." + confianca + " " : "")
                            + chamada;

        CartaoMudo.BorderBrush = (Brush)FindResource(e.Mudo ? (e.Certeza ? "Gravando" : "Aviso") : "Borda");
    }

    private void AdicionarMarca(Marca m)
    {
        if (m.Tipo is TipoDeMarca.Inicio or TipoDeMarca.Fim) return;
        _marcas.Insert(0, m);
        // Cem linhas cobrem qualquer reunião real; a lista inteira está no sessao.json de qualquer jeito.
        while (_marcas.Count > 100) _marcas.RemoveAt(_marcas.Count - 1);

        TxtSemMarcas.Visibility = Visibility.Collapsed;
        var s = App.Servico.Sessao;
        TxtContagem.Text = s == null ? "" :
            $"{s.QuantidadeDeCapturas} captura(s) · {s.QuantidadeDeMarcadores} marcador(es)";
    }

    private void AdicionarAviso(string texto)
    {
        if (_avisos.Contains(texto)) return;
        _avisos.Add(texto);
        CartaoAviso.Visibility = Visibility.Visible;
    }
}
