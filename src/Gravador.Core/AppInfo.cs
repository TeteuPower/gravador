using System.Reflection;

namespace Gravador.Core;

/// <summary>Nome, versão e as pastas que a ferramenta usa. Um lugar só, para o resto não chutar.</summary>
public static class AppInfo
{
    public const string Nome = "Gravador";

    public static string Versao =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Configuração e credenciais: %APPDATA%\Gravador.</summary>
    public static string PastaDados { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Nome);

    /// <summary>
    /// Onde as gravações caem por padrão: Documentos\Gravador.
    ///
    /// Não é %LOCALAPPDATA% de propósito. O produto desta ferramenta é um arquivo que a pessoa vai
    /// pegar e mandar para uma IA — precisa estar num lugar que ela ache sozinha, e o backup do
    /// OneDrive/Documentos já cobre. %LOCALAPPDATA% é para dado de máquina, não para entregável.
    /// </summary>
    public static string PastaSessoesPadrao { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Nome);

    /// <summary>
    /// Ferramentas baixadas sob demanda (ffmpeg, whisper, modelos): %LOCALAPPDATA%\Gravador\ferramentas.
    ///
    /// LOCAL e não Roaming porque são centenas de megabytes de binário de máquina — não fazem
    /// sentido num perfil que viaja, e o OneDrive não tem por que sincronizá-los.
    /// </summary>
    public static string PastaFerramentas { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Nome, "ferramentas");

    public static void GarantirPastas()
    {
        Directory.CreateDirectory(PastaDados);
    }
}
