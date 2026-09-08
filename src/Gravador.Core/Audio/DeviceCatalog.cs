using NAudio.CoreAudioApi;

namespace Gravador.Core.Audio;

/// <summary>Um endpoint de áudio do Windows, do jeito que a interface precisa mostrar.</summary>
public sealed record DispositivoAudio(
    string Id,
    string Nome,
    bool EhPadrao,
    bool EhPadraoComunicacao,
    int TaxaNativa,
    int CanaisNativos)
{
    /// <summary>Rótulo da lista: o nome, com a marca de padrão quando for o caso.</summary>
    public string Rotulo => EhPadrao ? $"{Nome}  (padrão)" : EhPadraoComunicacao ? $"{Nome}  (padrão de chamadas)" : Nome;

    public override string ToString() => Rotulo;
}

/// <summary>
/// Lista os endpoints de reprodução e de captura.
///
/// A reprodução aparece aqui porque é dela que sai o áudio "do computador": o WASAPI grava em
/// loopback o que UM endpoint está tocando. Quem tem fone e caixa de som separados precisa escolher
/// para qual dos dois a reunião está indo — é por isso que a lista de reprodução é oferecida em vez
/// de fixar o padrão do sistema.
/// </summary>
public static class DeviceCatalog
{
    public static IReadOnlyList<DispositivoAudio> Reproducao() => Listar(DataFlow.Render);

    public static IReadOnlyList<DispositivoAudio> Captura() => Listar(DataFlow.Capture);

    private static List<DispositivoAudio> Listar(DataFlow fluxo)
    {
        var lista = new List<DispositivoAudio>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var padrao = IdPadrao(en, fluxo, Role.Multimedia);
            var padraoCom = IdPadrao(en, fluxo, Role.Communications);

            foreach (var d in en.EnumerateAudioEndPoints(fluxo, DeviceState.Active))
            {
                try
                {
                    var fmt = d.AudioClient.MixFormat;
                    lista.Add(new DispositivoAudio(
                        d.ID,
                        d.FriendlyName,
                        d.ID == padrao,
                        d.ID == padraoCom && d.ID != padrao,
                        fmt.SampleRate,
                        fmt.Channels));
                }
                catch
                {
                    // endpoint que some no meio da enumeração (fone desconectado): ignora
                }
            }
        }
        catch
        {
            // sem serviço de áudio: devolve lista vazia e a interface avisa
        }

        lista.Sort((a, b) => (b.EhPadrao ? 1 : 0).CompareTo(a.EhPadrao ? 1 : 0) is var c && c != 0
            ? c
            : string.Compare(a.Nome, b.Nome, StringComparison.CurrentCulture));
        return lista;
    }

    private static string? IdPadrao(MMDeviceEnumerator en, DataFlow fluxo, Role papel)
    {
        try
        {
            return en.HasDefaultAudioEndpoint(fluxo, papel) ? en.GetDefaultAudioEndpoint(fluxo, papel).ID : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve um ID guardado na configuração para um dispositivo vivo. ID vazio, ou de um aparelho
    /// que foi desconectado desde a última vez, cai no padrão do Windows — a gravação não pode
    /// deixar de acontecer porque o fone de ontem não está mais no USB.
    /// </summary>
    public static MMDevice? Resolver(string? id, DataFlow fluxo, Role papelPadrao)
    {
        var en = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                var d = en.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
            }
            catch
            {
                // não existe mais
            }
        }
        try
        {
            return en.HasDefaultAudioEndpoint(fluxo, papelPadrao) ? en.GetDefaultAudioEndpoint(fluxo, papelPadrao) : null;
        }
        catch
        {
            return null;
        }
    }

    public static MMDevice? ResolverReproducao(string? id) => Resolver(id, DataFlow.Render, Role.Multimedia);

    /// <summary>
    /// O microfone usa <c>Role.Communications</c>, e não <c>Multimedia</c>: é o endpoint que o
    /// Windows entrega para Teams e Zoom, então é o que está de fato na reunião.
    /// </summary>
    public static MMDevice? ResolverCaptura(string? id) => Resolver(id, DataFlow.Capture, Role.Communications);
}
