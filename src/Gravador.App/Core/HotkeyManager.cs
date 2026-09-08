using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Gravador.Core.Settings;

namespace Gravador.App.Core;

/// <summary>
/// Atalhos que funcionam com qualquer programa na frente.
///
/// É o que faz esta ferramenta ser usável: no meio de uma reunião não dá para alternar até a janela
/// do Gravador para tirar um print do slide. O Windows entrega a tecla mesmo com o Teams em tela
/// cheia.
///
/// Uma janela invisível recebe as mensagens, e não a janela principal: a principal pode estar
/// fechada na bandeja, e os atalhos precisam continuar valendo. Mesma estrutura do claude-indicator.
///
/// Note a diferença para o gancho de teclado do núcleo: ali o objetivo é OBSERVAR sem tomar a tecla
/// de ninguém; aqui é o contrário — a combinação é registrada como nossa, e o Windows a tira de
/// quem estiver na frente. As duas coisas coexistem porque servem a propósitos opostos.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, Action> _acoes = new();
    private readonly List<string> _falhas = new();
    private HwndSource? _janela;
    private int _proximoId = 1;

    /// <summary>Combinações que o Windows recusou, normalmente por já estarem em uso.</summary>
    public IReadOnlyList<string> Falhas => _falhas;

    public bool Register(Hotkey atalho, Action acao)
    {
        if (!atalho.IsValid) return false;

        GarantirJanela();
        if (_janela == null) return false;

        var id = _proximoId++;
        if (!RegisterHotKey(_janela.Handle, id, atalho.NativeModifiers, atalho.VirtualKey))
        {
            _falhas.Add(atalho.ToString());
            return false;
        }

        _acoes[id] = acao;
        return true;
    }

    public void UnregisterAll()
    {
        if (_janela != null)
            foreach (var id in _acoes.Keys) UnregisterHotKey(_janela.Handle, id);
        _acoes.Clear();
        _falhas.Clear();
    }

    private void GarantirJanela()
    {
        if (_janela != null) return;
        var parametros = new HwndSourceParameters("GravadorAtalhos")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WsExToolWindow,
        };
        _janela = new HwndSource(parametros);
        _janela.AddHook(Processar);
    }

    private IntPtr Processar(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool tratado)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        if (_acoes.TryGetValue(wParam.ToInt32(), out var acao))
        {
            tratado = true;
            try
            {
                acao();
            }
            catch
            {
                // um atalho com defeito não pode derrubar a bomba de mensagens
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _janela?.Dispose();
        _janela = null;
    }

    private const int WmHotkey = 0x0312;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
