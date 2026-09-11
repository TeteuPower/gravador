using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gravador.App.Views;

namespace Gravador.App.Core;

/// <summary>
/// Modo de verificação: monta a janela, desenha cada aba num PNG e sai.
///
/// `Gravador.exe --render C:\pasta` grava uma imagem por aba.
///
/// Existe porque a interface precisa ser conferida sem alguém olhando para a tela — em máquina de
/// esteira, em sessão remota, ou simplesmente com o protetor de tela ligado, que é quando o
/// <c>BitBlt</c> não captura nada. O WPF desenha por software num <see cref="RenderTargetBitmap"/>,
/// sem depender da área de trabalho, então isto prova o que uma captura de tela não conseguiria:
/// que o XAML monta, mede, posiciona e pinta sem estourar.
///
/// Não substitui olhar: prova que desenha, não que ficou bom.
/// </summary>
internal static class Smoke
{
    private const int Largura = 1060;
    private const int Altura = 780;

    public static int Renderizar(string pasta)
    {
        Directory.CreateDirectory(pasta);

        var janela = new JanelaPrincipal
        {
            Width = Largura,
            Height = Altura,
            // Fora da área visível: a janela precisa existir de verdade para o WPF montar a árvore,
            // mas não deve piscar na frente de quem estiver usando a máquina.
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
        };
        janela.Show();

        var abas = new (string Arquivo, System.Windows.Controls.RadioButton Botao)[]
        {
            ("1-gravar.png", (System.Windows.Controls.RadioButton)janela.FindName("AbaGravar")),
            ("2-gravacoes.png", (System.Windows.Controls.RadioButton)janela.FindName("AbaSessoes")),
            ("3-configuracoes.png", (System.Windows.Controls.RadioButton)janela.FindName("AbaConfig")),
            ("4-conta.png", (System.Windows.Controls.RadioButton)janela.FindName("AbaConta")),
        };

        foreach (var (arquivo, botao) in abas)
        {
            botao.IsChecked = true;
            // Duas voltas: a primeira aplica a troca de aba, a segunda deixa o conteúdo novo medido
            // e posicionado antes de ser pintado.
            Bombear();
            janela.UpdateLayout();
            Bombear();
            Gravar(janela, Path.Combine(pasta, arquivo));
        }

        // A tela de configurações é bem mais alta do que a janela, e o que fica abaixo da dobra
        // nunca é medido nem pintado — logo, nunca é verificado. O cartão de atualização mora no
        // fim dela. Rolar até lá pelo mesmo caminho que o menu da bandeja usa confere as duas
        // coisas de uma vez: que o XAML do fim da página monta, e que o atalho até ele funciona.
        janela.IrParaAtualizacoes();
        Bombear();
        janela.UpdateLayout();
        Bombear();
        Gravar(janela, Path.Combine(pasta, "5-atualizacao.png"));

        // A pergunta de "vieram juntos?" é um diálogo modal: ela nunca aparece numa troca de aba,
        // então sem desenhá-la aqui ela seria a única tela do programa sem verificação nenhuma.
        // Fora de ordem e com um "10" no meio de proposito: a imagem prova que a ordenacao e a do
        // Explorador de Arquivos (1, 2, 10) e nao a de texto (1, 10, 2).
        var sequencia = JanelaSequencia.ParaDesenhar(
        [
            @"C:\audios\parte 2.ogg", @"C:\audios\parte 10.ogg", @"C:\audios\parte 1.ogg",
        ]);
        sequencia.Left = -32000;
        sequencia.Top = -32000;
        sequencia.WindowStartupLocation = WindowStartupLocation.Manual;
        sequencia.Show();
        Bombear();
        sequencia.UpdateLayout();
        Bombear();
        Gravar(sequencia, Path.Combine(pasta, "6-sequencia.png"));
        sequencia.Close();

        Console.WriteLine($"{abas.Length + 2} imagem(ns) em {pasta}");
        janela.Close();
        return 0;
    }

    /// <summary>
    /// Grava de verdade por alguns segundos e desenha a aba Gravar no meio disso.
    ///
    /// É a única forma de conferir a tela que mais importa — a que tem medidores se mexendo,
    /// cronômetro andando e a linha do tempo enchendo — sem alguém olhando para o monitor. A
    /// gravação é encerrada e a pasta apagada no fim: isto é verificação, não uma sessão de verdade.
    /// </summary>
    public static int RenderizarGravando(string pasta)
    {
        Directory.CreateDirectory(pasta);

        var janela = new JanelaPrincipal
        {
            Width = Largura,
            Height = Altura,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
        };
        janela.Show();
        Bombear();

        var sessao = App.Servico.Iniciar("Verificação da interface");
        try
        {
            Esperar(TimeSpan.FromSeconds(3));
            App.Servico.Marcar("exemplo de marcador");
            App.Servico.Mudo.DefinirManual(true);
            Esperar(TimeSpan.FromSeconds(2));
            App.Servico.Mudo.DefinirManual(false);
            Esperar(TimeSpan.FromSeconds(1));

            janela.UpdateLayout();
            Bombear();
            Gravar(janela, Path.Combine(pasta, "5-gravando.png"));
            Console.WriteLine($"gravando.png em {pasta}");
        }
        finally
        {
            App.Servico.Motor.AbortarSalvando();
            janela.Close();
            try { Directory.Delete(sessao.Pasta, recursive: true); } catch { /* deixa para o usuário */ }
        }
        return 0;
    }

    /// <summary>Espera deixando o WPF respirar — os medidores só andam se a fila for processada.</summary>
    private static void Esperar(TimeSpan quanto)
    {
        var ate = DateTime.UtcNow + quanto;
        while (DateTime.UtcNow < ate)
        {
            Bombear();
            System.Threading.Thread.Sleep(50);
        }
    }

    private static void Gravar(Window janela, string caminho)
    {
        var largura = (int)Math.Ceiling(janela.ActualWidth);
        var altura = (int)Math.Ceiling(janela.ActualHeight);
        if (largura <= 0 || altura <= 0) { largura = Largura; altura = Altura; }

        var alvo = new RenderTargetBitmap(largura, altura, 96, 96, PixelFormats.Pbgra32);
        alvo.Render(janela);

        var codificador = new PngBitmapEncoder();
        codificador.Frames.Add(BitmapFrame.Create(alvo));
        using var fs = File.Create(caminho);
        codificador.Save(fs);
    }

    /// <summary>
    /// Desenha a janela de detalhe de uma sessão — slides, linha do tempo, textos, o painel da
    /// conversa. Recebe a pasta de uma sessão já pronta.
    /// </summary>
    public static int RenderizarSessao(string pastaDaSessao, string destino)
    {
        var sessao = Gravador.Core.Session.SessaoGravacao.Abrir(pastaDaSessao);
        if (sessao == null) { Console.Error.WriteLine("Não achei uma sessão em " + pastaDaSessao); return 2; }
        Directory.CreateDirectory(destino);

        var janela = new JanelaSessao(sessao)
        {
            Width = 1240,
            Height = 800,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
        };
        janela.Show();
        Bombear();
        janela.UpdateLayout();
        Bombear();
        Gravar(janela, Path.Combine(destino, "sessao.png"));

        // De novo na aba com texto: é o único jeito de ver o botão de copiar LIGADO. Na aba vazia
        // ele fica apagado de propósito — copiar a frase "ainda não há resumo" seria uma pegadinha —
        // e uma imagem só do estado apagado não prova que o outro existe.
        if (janela.FindName("AbaTranscricao") is System.Windows.Controls.RadioButton aba)
        {
            aba.IsChecked = true;
            Bombear();
            janela.UpdateLayout();
            Bombear();
            Gravar(janela, Path.Combine(destino, "sessao-transcricao.png"));
        }

        // A janela de tradução precisa de uma sessão de verdade para ter o que contar (quantos
        // trechos, quantas chamadas), então ela é desenhada aqui e não na volta das abas.
        var traducao = new JanelaTraducao(sessao)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
        };
        traducao.Show();
        Bombear();
        traducao.UpdateLayout();
        Bombear();
        Gravar(traducao, Path.Combine(destino, "traducao.png"));
        traducao.Close();

        Console.WriteLine("sessao.png e traducao.png em " + destino);
        janela.Close();
        return 0;
    }

    /// <summary>Deixa o WPF processar o que está na fila (layout, binding, disparos de eventos).</summary>
    private static void Bombear()
    {
        var quadro = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => quadro.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(quadro);
    }
}
