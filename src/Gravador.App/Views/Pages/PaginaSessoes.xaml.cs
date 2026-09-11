using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Gravador.Core;
using Gravador.Core.Importacao;
using Gravador.Core.Session;
using Gravador.Core.Settings;

namespace Gravador.App.Views.Pages;

public partial class PaginaSessoes : UserControl
{
    private readonly ObservableCollection<ItemDeSessao> _itens = new();
    private bool _importando;

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

    private void AoAbrirSessao(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string pasta) return;
        var sessao = SessaoGravacao.Abrir(pasta);
        if (sessao == null) return;
        AbrirJanela(sessao);
    }

    private void AbrirJanela(SessaoGravacao sessao)
    {
        var janela = new JanelaSessao(sessao) { Owner = Window.GetWindow(this) };
        janela.Show();
    }

    private static void Abrir(string caminho)
    {
        try
        {
            if (!File.Exists(caminho)) Directory.CreateDirectory(caminho);
            Process.Start(new ProcessStartInfo(caminho) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não deu para abrir: " + ex.Message, AppInfo.Nome);
        }
    }

    // ==================================================================

    private static readonly string[] ExtensoesAceitas =
        [".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".m4v", ".mp3", ".m4a", ".wav", ".aac", ".ogg", ".flac", ".wma"];

    private void AoImportar(object sender, RoutedEventArgs e)
    {
        var dialogo = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar uma gravação",
            Filter = "Áudio e vídeo|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.wmv;*.m4v;*.mp3;*.m4a;*.wav;*.aac;*.ogg;*.flac;*.wma|Todos os arquivos|*.*",
            Multiselect = true,
        };
        if (dialogo.ShowDialog() == true) _ = ImportarVariosAsync(dialogo.FileNames);
    }

    private void AoArrastar(object sender, DragEventArgs e)
    {
        var ok = !_importando && e.Data.GetDataPresent(DataFormats.FileDrop)
                 && e.Data.GetData(DataFormats.FileDrop) is string[] arquivos
                 && arquivos.Any(a => ExtensoesAceitas.Contains(Path.GetExtension(a).ToLowerInvariant()));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void AoSoltar(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] arquivos) return;
        var aceitos = arquivos.Where(a => ExtensoesAceitas.Contains(Path.GetExtension(a).ToLowerInvariant())).ToArray();
        if (aceitos.Length > 0) _ = ImportarVariosAsync(aceitos);
    }

    private async Task ImportarVariosAsync(IEnumerable<string> arquivos)
    {
        if (_importando) return;
        var lista = arquivos.ToList();
        if (lista.Count == 0) return;

        _importando = true;
        CartaoImportacao.Visibility = Visibility.Visible;
        TxtImportacaoAviso.Visibility = Visibility.Collapsed;
        TxtImportacaoAviso.Text = "";

        var prontas = new List<SessaoGravacao>();
        var recados = new List<string>();

        try
        {
            for (var i = 0; i < lista.Count; i++)
            {
                var arquivo = lista[i];
                var posicao = lista.Count > 1 ? $" ({i + 1} de {lista.Count})" : "";
                TxtImportacaoTitulo.Text = $"Importando {Path.GetFileName(arquivo)}{posicao}";
                TxtImportacaoEtapa.Text = "Começando...";

                var importador = new ImportadorDeMidia(App.Config);
                importador.Aviso += a => Dispatcher.Invoke(() => Recadar(recados, a));

                // Whisper local por padrão, idioma detectado, tradução quando o idioma for outro. É o
                // caminho que funciona sem chave nenhuma; a primeira vez baixa ferramenta e modelo.
                var opcoes = new OpcoesDeImportacao
                {
                    Motor = App.Config.Transcricao is MotorTranscricao.Remoto ? MotorTranscricao.Remoto : MotorTranscricao.Whisper,
                    Idioma = "auto",
                    Traduzir = App.Config.TraduzirQuandoIdiomaDiferente,
                    Resumir = App.Config.ResumirAoFinal,
                };

                try
                {
                    prontas.Add(await importador.ImportarAsync(arquivo, opcoes,
                        new Progress<string>(t => TxtImportacaoEtapa.Text = t)));
                    Recarregar();
                }
                catch (Exception ex)
                {
                    Recadar(recados, $"{Path.GetFileName(arquivo)}: {ex.Message}");
                }
            }
        }
        finally
        {
            _importando = false;

            // O cartão só some quando deu tudo certo.
            //
            // Antes ele sumia SEMPRE, e junto com ele a mensagem do que falhou — que ficava quatro
            // segundos na tela e desaparecia. Foi assim que cinco arquivos não importaram e a única
            // pista que sobrou foi eles não estarem na lista.
            if (recados.Count == 0)
            {
                CartaoImportacao.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtImportacaoTitulo.Text = prontas.Count > 0
                    ? $"{prontas.Count} importada(s), {recados.Count} com problema"
                    : $"Não importou: {lista.Count} arquivo(s)";
                TxtImportacaoEtapa.Text = "Isto fica aqui até a próxima importação.";
            }

            // Uma janela por arquivo abriria cinco de uma vez. Com vários, a lista já mostra o que
            // entrou e quem quiser abre a que interessa.
            if (prontas.Count == 1) AbrirJanela(prontas[0]);
        }
    }

    /// <summary>Acumula avisos e falhas no cartão, em vez de uma sobrescrever a outra.</summary>
    private void Recadar(List<string> recados, string texto)
    {
        recados.Add(texto);
        TxtImportacaoAviso.Text = string.Join("\n", recados);
        TxtImportacaoAviso.Visibility = Visibility.Visible;
    }

    // ==================================================================

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
            + Formato.Tamanho(_sessao.BytesEmDisco())
            + (_sessao.Origem == OrigemDaSessao.Importada ? " · importada" : "")
            + (string.IsNullOrEmpty(_sessao.Idioma) ? "" : $" · {_sessao.Idioma}");

        public string Conteudo
        {
            get
            {
                var partes = new List<string>();
                foreach (var a in _sessao.Arquivos.Todos) partes.Add(a);
                if (_sessao.QuantidadeDeCapturas > 0) partes.Add($"{_sessao.QuantidadeDeCapturas} imagem(ns)");
                if (_sessao.QuantidadeDeMarcadores > 0) partes.Add($"{_sessao.QuantidadeDeMarcadores} marcador(es)");
                if (_sessao.Falas.Count > 0) partes.Add("transcrição");
                if (_sessao.TemTraducao) partes.Add("tradução");
                if (_sessao.Capitulos.Count > 0) partes.Add($"{_sessao.Capitulos.Count} capítulo(s)");
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
