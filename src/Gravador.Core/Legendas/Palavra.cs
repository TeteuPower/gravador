using System.Text;

namespace Gravador.Core.Legendas;

/// <summary>Uma palavra reconhecida, com o intervalo que ela ocupa no áudio.</summary>
public readonly record struct Palavra(double De, double Ate, string Texto)
{
    /// <summary>
    /// Forma comparável: sem pontuação, sem acento de caixa, sem espaço.
    ///
    /// É por ela que duas passadas do whisper "concordam". Comparar o texto cru faria a legenda
    /// recomeçar por causa de uma vírgula que apareceu na segunda passada — e vírgula é exatamente
    /// o que o whisper mais muda de ideia a respeito.
    /// </summary>
    public string Chave
    {
        get
        {
            var sb = new StringBuilder(Texto.Length);
            foreach (var c in Texto)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
