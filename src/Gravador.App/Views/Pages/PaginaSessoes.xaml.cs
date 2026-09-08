using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Gravador.Core;
using Gravador.Core.Session;

namespace Gravador.App.Views.Pages;

public partial class PaginaSessoes : UserControl
{
    private readonly ObservableCollection<ItemDeSessao> _itens = new();

    public PaginaSessoes()
    {
        InitializeComponent();
        Lista.ItemsSource = _itens;
    }

    public void Recarregar()
    {
        var raiz = App.Config.PastaSaidaEfetiva;
        var sessoes = SessaoGravacao.Listar(raiz);

        _itens.Clear();
        foreach (var s in sessoes) _itens.Add(new ItemDeSessao(s));

        var bytes = sessoes.Sum(s => s.BytesEmDisco());
        TxtResumoPasta.Text = sessoes.Count == 0
            ? raiz
            : $"{sessoes.Count} gravação(ões) · {Formato.Tamanho(bytes)} em {raiz}";
        TxtVazio.Visibility = sessoes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AoAtualizar(object sender, RoutedEventArgs e) => Recarregar();

    private void AoAbrirPastaRaiz(object sender, RoutedEventArgs e) => Abrir(App.Config.PastaSaidaEfetiva);

    private void AoAbrirPasta(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string pasta) Abrir(pasta);
    }

    private static void Abrir(string caminho)
    {
        try
        {
            Directory.CreateDirectory(caminho);
            Process.Start(new ProcessStartInfo(caminho) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não deu para abrir: " + ex.Message, AppInfo.Nome);
        }
    }

    /// <summary>
    /// Gera o resumo desta sessão (ou abre o que já existe).
    ///
    /// Refazer é permitido de propósito: quem transcreveu depois da primeira tentativa, ou trocou o
    /// texto do pedido nas configurações, quer o resumo de novo com o material novo.
    /// </summary>
    private async void AoResumir(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string pasta) return;
        var item = _itens.FirstOrDefault(i => i.Pasta == pasta);
        if (item == null || item.Ocupado) return;

        var sessao = SessaoGravacao.Abrir(pasta);
        if (sessao == null) return;

        if (File.Exists(sessao.CaminhoResumo) && !item.Refazendo)
        {
            Abrir(sessao.CaminhoResumo);
            return;
        }

        item.Ocupado = true;
        item.DefinirEstado("Preparando...");
        try
        {
            var resposta = await App.Servico.ResumirAsync(sessao, new Progress<string>(item.DefinirEstado));
            if (resposta.Ok)
            {
                item.DefinirEstado($"Resumo pronto ({Formato.Duracao(resposta.Duracao)}). Abrindo...");
                item.MarcarComResumo();
                Abrir(sessao.CaminhoResumo);
            }
            else
            {
                item.DefinirEstado("Não deu: " + resposta.Erro);
            }
        }
        catch (Exception ex)
        {
            item.DefinirEstado("Não deu: " + ex.Message);
        }
        finally
        {
            item.Ocupado = false;
        }
    }

    /// <summary>Uma linha da lista. Notifica mudanças porque o resumo altera o cartão em execução.</summary>
    private sealed class ItemDeSessao : INotifyPropertyChanged
    {
        private readonly SessaoGravacao _sessao;
        private string _estado = "";
        private bool _temResumo;

        public ItemDeSessao(SessaoGravacao sessao)
        {
            _sessao = sessao;
            _temResumo = File.Exists(sessao.CaminhoResumo);
        }

        public string Titulo => _sessao.Titulo;
        public string Pasta => _sessao.Pasta;
        public bool Ocupado { get; set; }
        public bool Refazendo => _temResumo && Ocupado;

        public string Detalhe =>
            $"{_sessao.Inicio.LocalDateTime:dd/MM/yyyy 'às' HH:mm} · {Formato.Duracao(_sessao.Duracao)} · "
            + Formato.Tamanho(_sessao.BytesEmDisco());

        public string Conteudo
        {
            get
            {
                var partes = new List<string>();
                foreach (var a in _sessao.Arquivos.Todos) partes.Add(a);
                if (_sessao.QuantidadeDeCapturas > 0) partes.Add($"{_sessao.QuantidadeDeCapturas} captura(s) de tela");
                if (_sessao.QuantidadeDeMarcadores > 0) partes.Add($"{_sessao.QuantidadeDeMarcadores} marcador(es)");
                if (_sessao.Falas.Count > 0) partes.Add("transcrição");
                if (_sessao.TrechosMudos.Count > 0)
                {
                    var total = TimeSpan.FromSeconds(_sessao.TrechosMudos.Sum(t => t.Duracao.TotalSeconds));
                    partes.Add($"{Formato.Duracao(total)} com o microfone mudo");
                }
                return partes.Count == 0 ? "pasta vazia" : string.Join(" · ", partes);
            }
        }

        public string TextoDoBotaoResumo => _temResumo ? "Ver o resumo" : "Resumir com o Claude";
        public string Estado => _estado;
        public Visibility VisibilidadeDoEstado => string.IsNullOrEmpty(_estado) ? Visibility.Collapsed : Visibility.Visible;

        public void DefinirEstado(string texto)
        {
            _estado = texto;
            Notificar(nameof(Estado));
            Notificar(nameof(VisibilidadeDoEstado));
        }

        public void MarcarComResumo()
        {
            _temResumo = true;
            Notificar(nameof(TextoDoBotaoResumo));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notificar(string nome) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nome));
    }
}
