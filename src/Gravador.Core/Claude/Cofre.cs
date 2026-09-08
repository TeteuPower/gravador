using System.Security.Cryptography;
using System.Text;

namespace Gravador.Core.Claude;

/// <summary>
/// Guarda segredo em disco com o DPAPI do usuário do Windows.
///
/// O token da conta Claude não pode ficar em texto puro em %APPDATA%: qualquer programa rodando com
/// o seu usuário leria a pasta e sairia com acesso à conta. Com o DPAPI no escopo do usuário, o
/// arquivo só se abre nesta máquina e com este login — copiá-lo para outro computador não serve de
/// nada.
///
/// Um cabeçalho de um byte diz se o conteúdo está protegido. Ele existe para o caso raro de o DPAPI
/// falhar (perfil temporário, política de grupo): aí o arquivo é gravado em claro e RECONHECIDO como
/// tal na leitura, em vez de o programa achar que o arquivo está corrompido e mandar você entrar de
/// novo toda vez.
/// </summary>
internal static class Cofre
{
    private const byte Protegido = 1;
    private const byte EmClaro = 0;

    public static byte[] Proteger(string texto)
    {
        var bytes = Encoding.UTF8.GetBytes(texto);
        try
        {
            var cifrado = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Concatenar(Protegido, cifrado);
        }
        catch (Exception)
        {
            return Concatenar(EmClaro, bytes);
        }
    }

    public static string Desproteger(byte[] dados)
    {
        if (dados.Length == 0) return "";
        var corpo = dados[1..];
        if (dados[0] == EmClaro) return Encoding.UTF8.GetString(corpo);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(corpo, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            // arquivo de outra máquina ou de outro usuário
            return "";
        }
    }

    private static byte[] Concatenar(byte marca, byte[] corpo)
    {
        var saida = new byte[corpo.Length + 1];
        saida[0] = marca;
        corpo.CopyTo(saida, 1);
        return saida;
    }
}
