using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Gravador.Core.Settings;

namespace Gravador.Core.Screens;

/// <summary>Uma imagem guardada.</summary>
public sealed record ImagemCapturada(string Caminho, int Largura, int Altura, long Bytes, string? Janela);

/// <summary>
/// O que saiu da tentativa de capturar.
///
/// Tem <see cref="Erro"/> em vez de só devolver nulo porque as causas de falha são distinguíveis e
/// acionáveis: tela bloqueada é diferente de disco cheio, e quem apertou o atalho no meio de uma
/// reunião merece saber qual das duas foi.
/// </summary>
public sealed record ResultadoDaCaptura(ImagemCapturada? Imagem, string? Erro)
{
    public bool Ok => Imagem != null;
}

/// <summary>
/// Tira a foto da tela que vai junto com o áudio no pacote final.
///
/// É <c>CopyFromScreen</c> (BitBlt), e não a API de captura do Windows 10+ (Windows.Graphics.Capture).
/// A segunda é mais moderna e pega janelas aceleradas por GPU que o BitBlt entrega em preto, mas
/// exige WinRT, cria uma sessão de captura e, em algumas máquinas, faz o Windows piscar a borda
/// amarela de "esta janela está sendo capturada" — no meio de uma apresentação isso é inaceitável.
/// O BitBlt é instantâneo, silencioso e custa quase nada de CPU, que é o que importa aqui.
///
/// O caso em que o BitBlt falha (janela com composição própria em tela cheia exclusiva) não é o caso
/// desta ferramenta: reunião e apresentação rodam em janela.
/// </summary>
public static class ScreenCapture
{
    /// <summary>
    /// Captura conforme a configuração e grava na pasta indicada.
    /// O nome do arquivo é sequencial e carrega o instante da gravação, para a ordem no Explorer
    /// ser a ordem da reunião.
    /// </summary>
    public static ResultadoDaCaptura Capturar(AppSettings config, string pasta, TimeSpan momento, int indice)
    {
        if (AreaDeTrabalhoIndisponivel() is { } impedimento)
            return new ResultadoDaCaptura(null, impedimento);

        var alvo = ResolverArea(config.AlvoDaCaptura, out var titulo);
        if (alvo.Width <= 0 || alvo.Height <= 0)
            return new ResultadoDaCaptura(null, "Não consegui descobrir qual área da tela capturar.");

        using var bruta = new Bitmap(alvo.Width, alvo.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bruta))
        {
            try
            {
                g.CopyFromScreen(alvo.Left, alvo.Top, 0, 0, alvo.Size, CopyPixelOperation.SourceCopy);
            }
            catch (Exception ex)
            {
                return new ResultadoDaCaptura(null, "A tela não pôde ser lida: " + ex.Message);
            }
        }

        using var imagem = Redimensionar(bruta, config.LarguraMaximaCaptura);

        Directory.CreateDirectory(pasta);
        var extensao = config.FormatoDaCaptura == FormatoImagem.Png ? "png" : "jpg";
        var carimbo = $"{(int)momento.TotalMinutes:00}m{momento.Seconds:00}s";
        var caminho = Path.Combine(pasta, $"{indice:000}_{carimbo}.{extensao}");

        try
        {
            if (config.FormatoDaCaptura == FormatoImagem.Png)
            {
                imagem.Save(caminho, ImageFormat.Png);
            }
            else
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var parametros = new EncoderParameters(1);
                parametros.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)config.QualidadeJpeg);
                imagem.Save(caminho, codec, parametros);
            }
        }
        catch (Exception ex)
        {
            return new ResultadoDaCaptura(null, "A imagem não pôde ser gravada: " + ex.Message);
        }

        long bytes;
        try { bytes = new FileInfo(caminho).Length; } catch { bytes = 0; }
        return new ResultadoDaCaptura(new ImagemCapturada(caminho, imagem.Width, imagem.Height, bytes, titulo), null);
    }

    /// <summary>
    /// Diz por que a tela não pode ser capturada agora, ou null quando pode.
    ///
    /// Isto existe porque a falha, sem explicação, é indistinguível de defeito. Com a estação
    /// bloqueada ou o protetor de tela ligado, a área de trabalho que está na frente não é a sua: o
    /// <c>BitBlt</c> devolve "identificador inválido" com todos os identificadores válidos, o que não
    /// ajuda ninguém. Perguntar antes qual área de trabalho está recebendo a entrada dá a resposta
    /// de verdade.
    /// </summary>
    public static string? AreaDeTrabalhoIndisponivel()
    {
        var entrada = IntPtr.Zero;
        try
        {
            entrada = OpenInputDesktop(0, false, DesktopReadObjects);
            if (entrada == IntPtr.Zero)
                return "A tela está bloqueada — o Windows não deixa capturar a área de trabalho de outra sessão.";

            var nome = NomeDoObjeto(entrada);
            if (nome.Equals("Screen-saver", StringComparison.OrdinalIgnoreCase))
                return "O protetor de tela está ligado. Mexa no mouse e tente de novo.";
            if (nome.Equals("Winlogon", StringComparison.OrdinalIgnoreCase))
                return "A tela está bloqueada. Desbloqueie e tente de novo.";
            return null;
        }
        catch
        {
            return null; // na dúvida, deixa tentar: o erro real aparece na captura
        }
        finally
        {
            if (entrada != IntPtr.Zero) CloseDesktop(entrada);
        }
    }

    private static string NomeDoObjeto(IntPtr handle)
    {
        var sb = new StringBuilder(256);
        return GetUserObjectInformation(handle, UoiName, sb, sb.Capacity * 2, out _) ? sb.ToString() : "";
    }

    /// <summary>
    /// Que retângulo capturar.
    ///
    /// Com "janela ativa", a janela do próprio Gravador é recusada: quem aperta o atalho com a
    /// ferramenta na frente quer a apresentação atrás dela, não um retrato da ferramenta. Nesse caso
    /// cai para o monitor inteiro, que é a resposta útil.
    /// </summary>
    private static Rectangle ResolverArea(AlvoCaptura alvo, out string? titulo)
    {
        titulo = null;
        var hwnd = GetForegroundWindow();

        if (alvo == AlvoCaptura.JanelaAtiva && hwnd != IntPtr.Zero && !EhJanelaDesteProcesso(hwnd))
        {
            var r = LimitesDaJanela(hwnd);
            if (r.Width > 0 && r.Height > 0)
            {
                titulo = TituloDaJanela(hwnd);
                // Uma janela pode estar parcialmente fora da área visível; capturar fora dá preto.
                var visivel = Rectangle.Intersect(r, SystemInformation.VirtualScreen);
                if (visivel.Width > 0 && visivel.Height > 0) return visivel;
            }
        }

        if (alvo == AlvoCaptura.TelaToda) return SystemInformation.VirtualScreen;

        // "monitor ativo" e a saída de emergência da janela ativa
        var tela = hwnd != IntPtr.Zero ? Screen.FromHandle(hwnd) : Screen.PrimaryScreen;
        titulo ??= hwnd != IntPtr.Zero ? TituloDaJanela(hwnd) : null;
        return tela?.Bounds ?? SystemInformation.VirtualScreen;
    }

    private static Bitmap Redimensionar(Bitmap origem, int larguraMaxima)
    {
        if (larguraMaxima <= 0) return (Bitmap)origem.Clone();
        var maior = Math.Max(origem.Width, origem.Height);
        if (maior <= larguraMaxima) return (Bitmap)origem.Clone();

        var escala = (double)larguraMaxima / maior;
        var largura = Math.Max(1, (int)Math.Round(origem.Width * escala));
        var altura = Math.Max(1, (int)Math.Round(origem.Height * escala));

        var destino = new Bitmap(largura, altura, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(destino);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(origem, 0, 0, largura, altura);
        return destino;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Limites reais da janela.
    ///
    /// <c>GetWindowRect</c> devolve, no Windows 10 e 11, alguns pixels a mais de cada lado: é a
    /// borda invisível que o gerenciador de janelas usa para o redimensionamento com o mouse.
    /// Capturar por ela põe uma faixa do que está atrás na imagem. O atributo estendido do DWM dá o
    /// retângulo que a pessoa enxerga.
    /// </summary>
    private static Rectangle LimitesDaJanela(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var r, Marshal.SizeOf<Rect>()) == 0)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return GetWindowRect(hwnd, out var w)
            ? Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom)
            : Rectangle.Empty;
    }

    private static string? TituloDaJanela(IntPtr hwnd)
    {
        try
        {
            var n = GetWindowTextLength(hwnd);
            if (n <= 0) return null;
            var sb = new StringBuilder(n + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var texto = sb.ToString().Trim();
            return texto.Length == 0 ? null : texto;
        }
        catch
        {
            return null;
        }
    }

    private static bool EhJanelaDesteProcesso(IntPtr hwnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    private const int DwmwaExtendedFrameBounds = 9;
    private const int DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(int flags, bool inherit, int access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, int length, out int needed);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
}
