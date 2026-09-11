using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using Gravador.Core;
using Gravador.Core.Claude;
using Gravador.Core.Session;

namespace Gravador.App.Views;

/// <summary>
/// Traduzir: primeiro a pergunta, depois o progresso — na mesma janela.
///
/// Antes, o botão "Traduzir" saía chamando o Claude na hora, com o idioma de destino das
/// configurações e sem dizer qual era. Dois problemas: ninguém confirmava o que ia acontecer, e o
/// único sinal de vida era uma linha de texto no rodapé da janela de trás, que numa transcrição
/// longa fica parada por minutos parecendo travada.
///
/// Uma janela e não duas: a escolha vira progresso no mesmo lugar, sem um modal fechar para outro
/// abrir. O que muda é o miolo; o título e os botões continuam onde estavam, então nada pisca.
///
/// A barra é de progresso REAL. O tradutor fatia a transcrição em pedaços de ~6 mil caracteres e
/// avisa a cada pedaço concluído — dá para mostrar 3 de 8 porque são 8 de verdade, não porque uma
/// animação precisa de um número.
/// </summary>
public partial class JanelaTraducao : Window
{
    private static readonly (string Codigo, string Nome)[] Idiomas =
    [
        ("pt-BR", "português do Brasil"),
        ("pt-PT", "português de Portugal"),
        ("en-US", "inglês"),
        ("es-ES", "espanhol"),
        ("fr-FR", "francês"),
        ("de-DE", "alemão"),
        ("it-IT", "italiano"),
        ("ja-JP", "japonês"),
        ("zh-CN", "chinês"),
    ];

    private readonly SessaoGravacao _sessao;
    private CancellationTokenSource? _cancelamento;
    private bool _traduzindo;

    public JanelaTraducao(SessaoGravacao sessao)
    {
        InitializeComponent();
        _sessao = sessao;

        var itens = Idiomas.Select(i => new Opcao(i.Codigo, i.Nome)).ToList();
        CmbOrigem.ItemsSource = itens;
        CmbDestino.ItemsSource = itens.ToList();

        CmbOrigem.Text = Rotulo(_sessao.Idioma);
        CmbDestino.Text = Rotulo(App.Config.IdiomaDestino);

        var pedacos = PedacosEstimados();
        TxtResumoDaSessao.Text = $"{_sessao.Falas.Count} trechos, {Formato.Duracao(_sessao.Duracao)} de áudio — "
            + (pedacos == 1 ? "uma chamada ao Claude." : $"cerca de {pedacos} chamadas ao Claude.");

        if (_sessao.TemTraducao)
            Avisar("Já existe uma tradução nesta sessão. Traduzir de novo substitui o arquivo traducao.md.");
    }

    /// <summary>Verdadeiro quando a tradução foi concluída — a janela de trás recarrega por isso.</summary>
    public bool Traduziu { get; private set; }

    private sealed record Opcao(string Codigo, string Nome)
    {
        public override string ToString() => $"{Nome} ({Codigo})";
    }

    private static string Rotulo(string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo)) return "";
        var conhecido = Idiomas.FirstOrDefault(i => i.Codigo.Equals(codigo, StringComparison.OrdinalIgnoreCase));
        if (conhecido.Codigo != null) return $"{conhecido.Nome} ({conhecido.Codigo})";

        // O whisper devolve o codigo curto ("pt", "en"), que nao bate com nenhum da lista. Mostrar
        // so "pt" na caixa nao diz nada a quem le; o nome resolve, e o codigo continua sendo o que
        // esta gravado na sessao.
        return $"{Tradutor.NomeDoIdioma(codigo)} ({codigo})";
    }

    /// <summary>
    /// Tira o código de volta do que está escrito na caixa.
    ///
    /// A caixa é editável — dá para digitar um idioma que não está na lista — então o texto pode ser
    /// tanto "inglês (en-US)" quanto "sv-SE" batido à mão. O que vale é o que estiver entre
    /// parênteses; sem parênteses, o texto inteiro.
    /// </summary>
    private static string Codigo(System.Windows.Controls.ComboBox caixa)
    {
        if (caixa.SelectedItem is Opcao o) return o.Codigo;
        var texto = (caixa.Text ?? "").Trim();
        var abre = texto.LastIndexOf('(');
        var fecha = texto.LastIndexOf(')');
        if (abre >= 0 && fecha > abre) return texto[(abre + 1)..fecha].Trim();
        return texto;
    }

    /// <summary>Mesmo corte do tradutor (~6 mil caracteres por pedaço), só para dizer quantas chamadas serão.</summary>
    private int PedacosEstimados()
    {
        var total = _sessao.Falas.Sum(f => f.Texto.Length + 12);
        return Math.Max(1, (int)Math.Ceiling(total / 6000.0));
    }

    private void Avisar(string texto)
    {
        TxtAviso.Text = texto;
        TxtAviso.Visibility = Visibility.Visible;
    }

    // ==================================================================

    private async void AoTraduzir(object sender, RoutedEventArgs e)
    {
        var destino = Codigo(CmbDestino);
        if (destino.Length == 0) { Avisar("Escolha o idioma de destino."); return; }

        var conta = ContaClaude.Estado(App.Config);
        if (!conta.Conectado) { Avisar(conta.Descricao); return; }

        // A correção do idioma de origem entra no cabeçalho da tradução, que diz de onde ela veio.
        var origem = Codigo(CmbOrigem);
        if (origem.Length > 0 && origem != _sessao.Idioma)
        {
            _sessao.Idioma = origem;
            _sessao.Salvar();
        }

        _traduzindo = true;
        _cancelamento = new CancellationTokenSource();
        PainelEscolha.Visibility = Visibility.Collapsed;
        PainelProgresso.Visibility = Visibility.Visible;
        BtnTraduzir.Visibility = Visibility.Collapsed;
        BtnFechar.Content = "Cancelar";
        TxtTitulo.Text = "Traduzindo para " + Tradutor.NomeDoIdioma(destino);

        var etapa = new Progress<string>(t => TxtEtapa.Text = t);
        var passos = new Progress<(int Feito, int Total)>(p =>
        {
            Barra.Value = p.Total > 0 ? (double)p.Feito / p.Total : 0;
            TxtContagem.Text = p.Total > 0 ? $"parte {p.Feito} de {p.Total}" : "";
        });

        try
        {
            var r = await Tradutor.TraduzirAsync(_sessao, App.Config, PosProcessamento.TokenDe(conta),
                etapa, _cancelamento.Token, destino, passos);

            if (!r.Ok)
            {
                VoltarParaEscolha();
                Avisar("Não deu: " + r.Erro);
                return;
            }

            Traduziu = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            VoltarParaEscolha();
            Avisar("Cancelado. O que já tinha sido traduzido foi descartado — a tradução é gravada só no fim.");
        }
        catch (Exception ex)
        {
            VoltarParaEscolha();
            Avisar("Não deu: " + ex.Message);
        }
        finally
        {
            _traduzindo = false;
            _cancelamento?.Dispose();
            _cancelamento = null;
        }
    }

    private void VoltarParaEscolha()
    {
        PainelProgresso.Visibility = Visibility.Collapsed;
        PainelEscolha.Visibility = Visibility.Visible;
        BtnTraduzir.Visibility = Visibility.Visible;
        BtnFechar.Content = "Cancelar";
        TxtTitulo.Text = "Traduzir a transcrição";
    }

    private void AoCancelar(object sender, RoutedEventArgs e)
    {
        if (_traduzindo) { _cancelamento?.Cancel(); return; }
        Close();
    }

    /// <summary>Fechar no X no meio da tradução cancela, em vez de deixar a chamada correndo solta.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_traduzindo)
        {
            e.Cancel = true;
            _cancelamento?.Cancel();
            return;
        }
        base.OnClosing(e);
    }
}
