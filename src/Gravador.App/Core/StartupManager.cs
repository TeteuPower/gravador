using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Gravador.App.Core;

/// <summary>
/// Liga e desliga o "iniciar com o Windows".
///
/// Pela chave Run do usuário, e não por uma tarefa agendada: a chave Run aparece na aba
/// Inicializar do Gerenciador de Tarefas, onde a pessoa procura quando quer desligar alguma coisa.
/// Tarefa agendada some dessa lista, e um programa que grava áudio começando sozinho sem constar em
/// lugar nenhum é exatamente o tipo de coisa que não se deve fazer.
/// </summary>
public static class StartupManager
{
    private const string Chave = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Nome = "Gravador";

    public static bool Ativo
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Chave);
                return k?.GetValue(Nome) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Aplicar(bool ligar)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Chave, writable: true);
            if (k == null) return;

            if (!ligar)
            {
                k.DeleteValue(Nome, throwOnMissingValue: false);
                return;
            }

            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe)) return;

            // --minimizado: subir com o Windows e abrir a janela na cara da pessoa toda manhã
            // seria o contrário do que ela pediu ao marcar a opção.
            k.SetValue(Nome, $"\"{exe}\" --minimizado");
        }
        catch
        {
            // política de grupo pode bloquear a chave Run; a opção simplesmente não pega
        }
    }
}
