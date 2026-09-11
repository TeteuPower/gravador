using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace Gravador.App.Views;

/// <summary>O que fazer com vários arquivos escolhidos de uma vez.</summary>
public enum EscolhaDaImportacao
{
    Cancelar,

    /// <summary>Uma sessão por arquivo, como sempre foi.</summary>
    Separados,

    /// <summary>Uma sessão só, com os áudios colados na ordem mostrada.</summary>
    EmSequencia,
}

/// <summary>
/// Pergunta, antes de importar vários arquivos, se eles são partes de uma mesma gravação.
///
/// A pergunta existe porque o programa não tem como saber: cinco áudios de WhatsApp soltos numa
/// pasta podem ser uma conversa partida em pedaços ou cinco recados de assuntos diferentes, e os
/// arquivos são idênticos nos dois casos. Chutar errado é caro nos dois sentidos — juntar o que era
/// separado mistura assuntos no resumo; separar o que era junto impede o Claude de responder
/// qualquer coisa que atravesse a fronteira entre dois áudios.
///
/// A ORDEM é mostrada, e não só prometida. Ela vem do nome do arquivo, pela mesma comparação que o
/// Explorador de Arquivos usa, então "10" vem depois de "9" e não antes. Ver <see cref="Ordenar"/>.
/// </summary>
public partial class JanelaSequencia : Window
{
    private JanelaSequencia(IReadOnlyList<string> arquivos)
    {
        InitializeComponent();
        Arquivos = arquivos;
        TxtTitulo.Text = $"{arquivos.Count} arquivos";
        Lista.ItemsSource = arquivos
            .Select((a, i) => new { Numero = $"{i + 1}.", Nome = Path.GetFileName(a) })
            .ToList();
    }

    public IReadOnlyList<string> Arquivos { get; }

    public EscolhaDaImportacao Escolha { get; private set; } = EscolhaDaImportacao.Cancelar;

    /// <summary>Também gravar um .md por parte. Só vale quando a escolha é a sequência.</summary>
    public bool TranscricoesPorParte => ChkPorParte.IsChecked == true;

    /// <summary>
    /// Mostra a pergunta e devolve a decisão. Com um arquivo só não pergunta nada — não há
    /// sequência possível — e responde "separados", que é o caminho de sempre.
    /// </summary>
    public static (EscolhaDaImportacao Escolha, IReadOnlyList<string> Arquivos, bool PorParte) Perguntar(
        Window? dono, IReadOnlyList<string> arquivos)
    {
        if (arquivos.Count <= 1) return (EscolhaDaImportacao.Separados, arquivos, false);

        var ordenados = Ordenar(arquivos);
        var janela = new JanelaSequencia(ordenados) { Owner = dono };
        janela.ShowDialog();
        return (janela.Escolha, ordenados, janela.TranscricoesPorParte);
    }

    /// <summary>
    /// Ordena pelo nome, do jeito que o Windows ordena.
    ///
    /// <c>StrCmpLogicalW</c> é a comparação do Explorador de Arquivos: ela enxerga os números como
    /// números, então "parte 2" vem antes de "parte 10". A comparação de texto comum faria o
    /// contrário, e a sequência sairia embaralhada exatamente nos casos em que alguém numerou os
    /// arquivos à mão para deixar a ordem clara.
    /// </summary>
    private static IReadOnlyList<string> Ordenar(IReadOnlyList<string> arquivos) =>
        arquivos.OrderBy(Path.GetFileName, Comparer<string?>.Create(
            (a, b) => StrCmpLogicalW(a ?? "", b ?? ""))).ToList();

    /// <summary>Monta a janela sem mostrar, para o modo --render conferir que ela desenha.</summary>
    internal static JanelaSequencia ParaDesenhar(IReadOnlyList<string> arquivos) => new(Ordenar(arquivos));

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    private void AoSequencia(object sender, RoutedEventArgs e)
    {
        Escolha = EscolhaDaImportacao.EmSequencia;
        Close();
    }

    private void AoSeparados(object sender, RoutedEventArgs e)
    {
        Escolha = EscolhaDaImportacao.Separados;
        Close();
    }

    private void AoCancelar(object sender, RoutedEventArgs e)
    {
        Escolha = EscolhaDaImportacao.Cancelar;
        Close();
    }
}
