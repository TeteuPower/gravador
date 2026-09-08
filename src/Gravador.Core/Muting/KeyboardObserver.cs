using System.Diagnostics;
using System.Runtime.InteropServices;
using Gravador.Core.Settings;

namespace Gravador.Core.Muting;

/// <summary>Uma combinação reconhecida, com o programa que estava na frente na hora.</summary>
public sealed record TeclaObservada(Hotkey Combinacao, string ProcessoEmFoco);

/// <summary>
/// Vigia combinações de teclas sem tomá-las de ninguém.
///
/// A diferença para o <c>RegisterHotKey</c> (que o app usa nos atalhos dele) é essa: aqui o objetivo
/// é SABER que você apertou Ctrl+Shift+M no Teams, e o Teams precisa continuar recebendo essa tecla
/// — do contrário a ferramenta anotaria "mudo" e você seguiria falando para a reunião inteira. Por
/// isso é um gancho de baixo nível que devolve tudo para a fila com <c>CallNextHookEx</c>, sempre,
/// inclusive quando reconhece a combinação.
///
/// O gancho precisa de uma fila de mensagens na própria thread, então este objeto sobe uma thread só
/// para isso. O retorno de chamada roda nessa thread e é o caminho de TODA tecla do sistema: o que
/// ele faz tem que ser curto. Aqui ele só compara inteiros e enfileira um evento.
/// </summary>
public sealed class KeyboardObserver : IDisposable
{
    private readonly List<Hotkey> _vigiadas = new();
    private readonly object _trava = new();
    private Thread? _thread;
    private IntPtr _gancho;
    private uint _threadId;
    private volatile bool _parando;

    // O delegate precisa de uma referência viva no lado gerenciado: se o coletor o levar, o
    // Windows chama um ponteiro morto e o processo cai sem rastro.
    private readonly LowLevelKeyboardProc _proc;

    public KeyboardObserver()
    {
        _proc = AoTeclar;
    }

    /// <summary>Disparado na thread do gancho. Não bloqueie aqui.</summary>
    public event Action<TeclaObservada>? Reconhecida;

    /// <summary>Define o conjunto de combinações a vigiar. Pode ser trocado com o gancho no ar.</summary>
    public void Vigiar(IEnumerable<Hotkey> combinacoes)
    {
        lock (_trava)
        {
            _vigiadas.Clear();
            foreach (var h in combinacoes)
                if (h.IsValid && !_vigiadas.Contains(h))
                    _vigiadas.Add(h);
        }
    }

    public bool Ativo => _gancho != IntPtr.Zero;

    public void Iniciar()
    {
        if (_thread != null) return;
        _parando = false;
        _thread = new Thread(Bombear)
        {
            IsBackground = true,
            Name = "Gravador.Teclado",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Bombear()
    {
        _threadId = GetCurrentThreadId();
        // WH_KEYBOARD_LL é global e não precisa de DLL injetada: o hMod pode ser o do próprio
        // módulo, e o Windows entrega as teclas de todos os processos nesta fila.
        _gancho = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_gancho == IntPtr.Zero) return;

        while (!_parando && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_gancho);
        _gancho = IntPtr.Zero;
    }

    private IntPtr AoTeclar(int codigo, IntPtr wParam, IntPtr lParam)
    {
        if (codigo >= 0)
        {
            var mensagem = (int)wParam;
            if (mensagem is WmKeydown or WmSyskeydown)
            {
                try
                {
                    var vk = (uint)Marshal.ReadInt32(lParam);
                    Avaliar(vk);
                }
                catch
                {
                    // nada aqui pode escapar: uma exceção neste retorno derruba o gancho
                }
            }
        }
        return CallNextHookEx(_gancho, codigo, wParam, lParam);
    }

    private void Avaliar(uint vk)
    {
        // as próprias teclas modificadoras não abrem combinação nenhuma
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return;

        Hotkey? achada = null;
        lock (_trava)
        {
            if (_vigiadas.Count == 0) return;
            var mods = ModificadoresAgora();
            if (mods == HotkeyModifiers.None) return;
            foreach (var h in _vigiadas)
            {
                if (h.VirtualKey == vk && h.Modifiers == mods) { achada = h; break; }
            }
        }
        if (achada is not { } combinacao) return;

        var evento = Reconhecida;
        if (evento == null) return;
        evento(new TeclaObservada(combinacao, ProcessoEmPrimeiroPlano()));
    }

    private static HotkeyModifiers ModificadoresAgora()
    {
        var m = HotkeyModifiers.None;
        if (Pressionada(0x11)) m |= HotkeyModifiers.Control;
        if (Pressionada(0x12)) m |= HotkeyModifiers.Alt;
        if (Pressionada(0x10)) m |= HotkeyModifiers.Shift;
        if (Pressionada(0x5B) || Pressionada(0x5C)) m |= HotkeyModifiers.Windows;
        return m;
    }

    private static bool Pressionada(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Nome do executável da janela em primeiro plano, sem extensão. Vazio se não der.</summary>
    public static string ProcessoEmPrimeiroPlano()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        _parando = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1500);
        _thread = null;
    }

    // ------------------------------------------------------------------

    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const uint WmQuit = 0x0012;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
