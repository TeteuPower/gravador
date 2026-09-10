using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Gravador.Core;
using Gravador.Core.Audio;
using Gravador.Core.Session;

namespace Gravador.App.Core;

/// <summary>
/// O ícone na bandeja.
///
/// Não é enfeite: é o estado principal da ferramenta. Durante uma reunião a janela fica fechada, e o
/// ícone é a única coisa que responde "isto está gravando mesmo?" — pergunta que, respondida errado,
/// custa a reunião inteira. Por isso ele MUDA de desenho enquanto grava, em vez de só mudar a dica
/// de texto: cor a gente vê de canto de olho, texto exige parar e ler.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icone;
    private readonly ServicoDeGravacao _servico;
    private readonly ToolStripMenuItem _itemGravar;
    private readonly ToolStripMenuItem _itemPausar;
    private readonly ToolStripMenuItem _itemAtualizar;
    private readonly ToolStripSeparator _separadorAtualizar;
    private readonly System.Windows.Forms.Timer _relogio;
    private Icon? _iconeParado;
    private Icon? _iconeGravando;
    private Icon? _iconePausado;

    public event Action? AbrirPedido;
    public event Action? SairPedido;

    /// <summary>Alguém quer ver a atualização que foi anunciada (pelo balão ou pelo menu).</summary>
    public event Action? AtualizarPedido;

    public TrayIcon(ServicoDeGravacao servico)
    {
        _servico = servico;

        var menu = new ContextMenuStrip { ShowImageMargin = false };

        // Fica escondido até existir versão nova. No topo, e em negrito, porque é a única entrada
        // do menu que não é uma ação de gravação: se aparecer no meio das outras, some.
        _itemAtualizar = new ToolStripMenuItem("Atualização disponível", null, (_, _) => AtualizarPedido?.Invoke())
        {
            Visible = false,
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        _separadorAtualizar = new ToolStripSeparator { Visible = false };
        menu.Items.Add(_itemAtualizar);
        menu.Items.Add(_separadorAtualizar);

        _itemGravar = new ToolStripMenuItem("Gravar", null, (_, _) => AlternarGravacao());
        _itemPausar = new ToolStripMenuItem("Pausar", null, (_, _) => _servico.AlternarPausa()) { Enabled = false };
        menu.Items.Add(_itemGravar);
        menu.Items.Add(_itemPausar);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Capturar a tela", null, (_, _) => _servico.Capturar()));
        menu.Items.Add(new ToolStripMenuItem("Marcar este momento", null, (_, _) => _servico.Marcar()));
        menu.Items.Add(new ToolStripMenuItem("Estou mudo / voltei", null, (_, _) => _servico.AlternarMudoManual()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Abrir o Gravador", null, (_, _) => AbrirPedido?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Sair", null, (_, _) => SairPedido?.Invoke()));

        _icone = new NotifyIcon
        {
            Icon = IconeDoEstado(EstadoGravacao.Parada),
            Text = AppInfo.Nome,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icone.DoubleClick += (_, _) => AbrirPedido?.Invoke();

        // Clicar no balão leva ao lugar onde o balão manda ir. Um aviso que não é clicável obriga
        // a pessoa a procurar sozinha onde fica o botão de atualizar.
        _icone.BalloonTipClicked += (_, _) =>
        {
            if (_itemAtualizar.Visible) AtualizarPedido?.Invoke();
            else AbrirPedido?.Invoke();
        };

        // Um segundo: a dica de texto mostra o tempo gravado, e mais frequência do que isso não
        // muda nada para quem olha.
        _relogio = new System.Windows.Forms.Timer { Interval = 1000 };
        _relogio.Tick += (_, _) => AtualizarDica();
        _relogio.Start();
    }

    private async void AlternarGravacao()
    {
        try
        {
            if (_servico.PodeIniciar) _servico.Iniciar();
            else if (_servico.EmAndamento) await _servico.PararAsync();
        }
        catch (Exception ex)
        {
            Avisar("Não deu para gravar", ex.Message, ToolTipIcon.Warning);
        }
    }

    public void AtualizarEstado(EstadoGravacao estado)
    {
        _icone.Icon = IconeDoEstado(estado);
        _itemGravar.Text = estado == EstadoGravacao.Parada ? "Gravar" : "Parar e salvar";
        _itemGravar.Enabled = estado != EstadoGravacao.Finalizando;
        _itemPausar.Enabled = estado is EstadoGravacao.Gravando or EstadoGravacao.Pausada;
        _itemPausar.Text = estado == EstadoGravacao.Pausada ? "Retomar" : "Pausar";
        AtualizarDica();
    }

    private void AtualizarDica()
    {
        var estado = _servico.Estado;
        var texto = estado switch
        {
            EstadoGravacao.Gravando => $"{AppInfo.Nome} — gravando {Formato.Cronometro(_servico.Decorrido)}",
            EstadoGravacao.Pausada => $"{AppInfo.Nome} — pausado em {Formato.Cronometro(_servico.Decorrido)}",
            EstadoGravacao.Finalizando => $"{AppInfo.Nome} — salvando...",
            _ => $"{AppInfo.Nome} {AppInfo.Versao}",
        };
        if (_servico.Mudo.Mudo && estado == EstadoGravacao.Gravando)
            texto += $" · {_servico.Mudo.Estado.Descricao}";

        // O Windows corta a dica em 63 caracteres e engole o resto sem avisar.
        _icone.Text = texto.Length > 62 ? texto[..62] : texto;
    }

    /// <summary>Deixa a atualização à mão no menu da bandeja, que é onde a janela fechada ainda responde.</summary>
    public void AnunciarAtualizacao(string versao)
    {
        _itemAtualizar.Text = $"Atualizar para a versão {versao}";
        _itemAtualizar.Visible = true;
        _separadorAtualizar.Visible = true;
    }

    public void Avisar(string titulo, string mensagem, ToolTipIcon tipo = ToolTipIcon.Info)
    {
        _icone.BalloonTipTitle = titulo;
        _icone.BalloonTipText = mensagem;
        _icone.BalloonTipIcon = tipo;
        _icone.ShowBalloonTip(4000);
    }

    // ------------------------------------------------------------------

    private Icon IconeDoEstado(EstadoGravacao estado) => estado switch
    {
        EstadoGravacao.Gravando => _iconeGravando ??= Desenhar(Color.FromArgb(226, 74, 63), cheio: true),
        EstadoGravacao.Pausada => _iconePausado ??= Desenhar(Color.FromArgb(251, 191, 36), cheio: true),
        EstadoGravacao.Finalizando => _iconePausado ??= Desenhar(Color.FromArgb(251, 191, 36), cheio: true),
        _ => _iconeParado ??= Desenhar(Color.FromArgb(139, 147, 167), cheio: false),
    };

    /// <summary>
    /// Desenha o ícone em 32×32 e o converte para <see cref="Icon"/>.
    ///
    /// Desenhado em vez de embutido como arquivo porque são três estados e eles só diferem na cor e
    /// no preenchimento — três .ico no projeto seria três arquivos para manter em sincronia à mão.
    /// </summary>
    private static Icon Desenhar(Color cor, bool cheio)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var caneta = new Pen(cor, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(caneta, 6, 11, 6, 21);
            g.DrawLine(caneta, 26, 11, 26, 21);

            if (cheio)
            {
                using var pincel = new SolidBrush(cor);
                g.FillEllipse(pincel, 10, 10, 12, 12);
            }
            else
            {
                g.DrawEllipse(caneta, 11, 11, 10, 10);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _relogio.Stop();
        _relogio.Dispose();
        _icone.Visible = false;
        _icone.Dispose();
        _iconeParado?.Dispose();
        _iconeGravando?.Dispose();
        _iconePausado?.Dispose();
    }
}
