using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gravador.Core;
using Gravador.Core.Claude;

namespace Gravador.App.Views.Pages;

public partial class PaginaConta : UserControl
{
    public PaginaConta()
    {
        InitializeComponent();

        CmbModelo.ItemsSource = new[] { "claude-sonnet-5", "claude-opus-5", "claude-haiku-4-5-20251001" };

        // O login pelo ouvinte local termina sozinho, numa thread de rede: a tela precisa saber.
        ClaudeLogin.Concluiu += (credenciais, erro) => Dispatcher.Invoke(() =>
        {
            PainelCodigo.Visibility = Visibility.Collapsed;
            Mensagem(erro == null
                ? $"Conectado{(credenciais?.Email is { Length: > 0 } e ? " como " + e : "")}."
                : "Não deu: " + erro);
            Recarregar();
        });

        Recarregar();
    }

    public void Recarregar()
    {
        var c = App.Config;
        CmbModelo.Text = c.ModeloClaude;
        ChkResumirAoFinal.IsChecked = c.ResumirAoFinal;
        ChkEnviarCapturas.IsChecked = c.EnviarCapturasNoResumo;
        TxtPrompt.Text = string.IsNullOrWhiteSpace(c.PromptResumo) ? Analista.PromptPadrao : c.PromptResumo;

        ClaudeCli.Reprocurar();
        var estado = ContaClaude.Estado(c);

        TxtEstadoConta.Text = estado.Conectado
            ? estado.Meio switch
            {
                MeioDeAcesso.ClaudeCode => "Conectado pelo Claude Code desta máquina",
                MeioDeAcesso.LoginNoGravador => "Conectado pelo login no Gravador",
                MeioDeAcesso.ChaveDeApi => "Conectado por chave de API",
                _ => "Conectado",
            }
            : "Sem conta conectada";

        LuzConta.Fill = (Brush)FindResource(estado.Conectado ? "Bom" : "TextoFraco");
        TxtDescricaoConta.Text = estado.Descricao;

        BtnSair.Visibility = ClaudeLogin.Conectado ? Visibility.Visible : Visibility.Collapsed;
        BtnEntrar.Content = ClaudeLogin.Conectado ? "Entrar com outra conta" : "Entrar com a conta Claude";
    }

    // ==================================================================

    private void AoEntrar(object sender, RoutedEventArgs e) => Entrar(manual: false);

    private void AoEntrarManual(object sender, RoutedEventArgs e) => Entrar(manual: true);

    private void Entrar(bool manual)
    {
        try
        {
            var (url, ehManual) = ClaudeLogin.Iniciar(manual);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            PainelCodigo.Visibility = ehManual ? Visibility.Visible : Visibility.Collapsed;
            Mensagem(ehManual
                ? "Autorize no navegador e cole o código aqui."
                : "Autorize no navegador — assim que você aceitar, esta tela se atualiza sozinha.");
            if (ehManual) TxtCodigo.Focus();
        }
        catch (Exception ex)
        {
            Mensagem("Não deu para abrir o navegador: " + ex.Message);
        }
    }

    private async void AoConcluirManual(object sender, RoutedEventArgs e)
    {
        var colado = TxtCodigo.Text.Trim();
        if (colado.Length == 0) return;

        Mensagem("Trocando o código pelo token...");
        try
        {
            var credenciais = await ClaudeLogin.ConcluirManualAsync(colado);
            TxtCodigo.Text = "";
            PainelCodigo.Visibility = Visibility.Collapsed;
            Mensagem($"Conectado{(credenciais.Email is { Length: > 0 } m ? " como " + m : "")}.");
            Recarregar();
        }
        catch (Exception ex)
        {
            Mensagem("Não deu: " + ex.Message);
        }
    }

    private void AoSair(object sender, RoutedEventArgs e)
    {
        ClaudeLogin.Sair();
        Mensagem("Login removido desta máquina.");
        Recarregar();
    }

    private void AoRestaurarPrompt(object sender, RoutedEventArgs e) => TxtPrompt.Text = Analista.PromptPadrao;

    private void AoSalvar(object sender, RoutedEventArgs e)
    {
        var c = App.Config;
        c.ModeloClaude = string.IsNullOrWhiteSpace(CmbModelo.Text) ? "claude-sonnet-5" : CmbModelo.Text.Trim();
        c.ResumirAoFinal = ChkResumirAoFinal.IsChecked == true;
        c.EnviarCapturasNoResumo = ChkEnviarCapturas.IsChecked == true;

        // Guardar o texto de fábrica como se fosse personalizado congelaria a versão de hoje: se ele
        // melhorar numa atualização, quem nunca mexeu no campo continuaria com o antigo.
        var prompt = TxtPrompt.Text.Trim();
        c.PromptResumo = prompt == Analista.PromptPadrao.Trim() ? "" : prompt;

        App.SalvarConfiguracao();
        TxtSalvo.Text = "Salvo.";
    }

    private void Mensagem(string texto)
    {
        TxtMensagemConta.Text = texto;
        TxtMensagemConta.Visibility = Visibility.Visible;
    }
}
