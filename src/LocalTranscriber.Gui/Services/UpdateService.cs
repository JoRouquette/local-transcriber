using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace LocalTranscriber.Gui.Services;

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
            new GithubSource("https://github.com/JoRouquette/local-transcriber", null, false));
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
        if (!_mgr.IsInstalled) return null;

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

    /// <summary>Applique la mise à jour téléchargée et redémarre immédiatement l'application.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is not null) _mgr.ApplyUpdatesAndRestart(_pending);
    }

    /// <summary>Programme l'installation de la mise à jour à la prochaine fermeture (non intrusif).</summary>
    public void ApplyOnExit()
    {
        if (_pending is not null) _mgr.WaitExitThenApplyUpdates(_pending);
    }
}
