using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace LocalTranscriber.Gui.Services;

// WindowsServiceControl vit dans le namespace racine LocalTranscriber.Gui.

/// <summary>
/// Encapsule l'auto-update Velopack : interroge les releases GitHub, télécharge le
/// paquet et l'applique (au redémarrage immédiat ou à la prochaine fermeture).
/// Ne fait rien tant que l'app tourne depuis <c>bin/</c> (non installée).
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        // Repo public → pas de token. prerelease=false : on ne prend que les releases stables.
        _mgr = new UpdateManager(
            new GithubSource("https://github.com/JoRouquette/local-transcriber", null, false)
        );
    }

    /// <summary>Vrai uniquement pour une app réellement installée (pas en développement).</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    public string CurrentVersion => _mgr.CurrentVersion?.ToString() ?? "—";

    /// <summary>
    /// Cherche une mise à jour et la télécharge le cas échéant.
    /// Retourne la version disponible (prête à appliquer) ou null si à jour.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (!_mgr.IsInstalled)
            return null;

        var info = await _mgr.CheckForUpdatesAsync();
        if (info is null)
        {
            _pending = null;
            return null;
        }

        await _mgr.DownloadUpdatesAsync(info);
        _pending = info;
        return info.TargetFullRelease.Version.ToString();
    }

    /// <summary>Vrai si une mise à jour s'appliquera après la fermeture (voir <see cref="ApplyOnExit"/>).</summary>
    public bool ApplyScheduledOnExit { get; private set; }

    /// <summary>Applique la mise à jour téléchargée et redémarre immédiatement l'application.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is null)
            return;
        // Velopack remplace le dossier `current\` ; le worker de fond tourne depuis ce meme
        // dossier et le verrouille. On l'arrete avant d'appliquer — la GUI relancee le redemarre.
        WindowsServiceControl.Stop();
        _mgr.ApplyUpdatesAndRestart(_pending);
    }

    /// <summary>Programme l'installation de la mise à jour à la prochaine fermeture (non intrusif).</summary>
    public void ApplyOnExit()
    {
        if (_pending is null)
            return;
        _mgr.WaitExitThenApplyUpdates(_pending);
        ApplyScheduledOnExit = true;
    }

    /// <summary>
    /// À appeler à la fermeture de l'application. Si une mise à jour s'appliquera après la sortie
    /// du processus (<see cref="ApplyOnExit"/>), on arrête le worker de fond pour libérer `current\`,
    /// sans quoi le remplacement Velopack échoue (fichiers verrouillés).
    /// </summary>
    public void OnAppExit()
    {
        if (ApplyScheduledOnExit)
            WindowsServiceControl.Stop();
    }
}
