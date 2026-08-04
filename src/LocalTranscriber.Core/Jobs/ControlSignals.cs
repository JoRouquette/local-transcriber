namespace LocalTranscriber.Core.Jobs;

/// <summary>
/// Signaux de contrôle simples entre la GUI et le worker, matérialisés par des fichiers dans
/// le dossier de données (canal cross-process trivial, indépendant des bases SQLite). La GUI
/// dépose le fichier, le worker le détecte pendant un traitement et réagit.
/// </summary>
public static class ControlSignals
{
    /// <summary>Dossier des signaux de contrôle sous le dossier de données.</summary>
    public static string ControlDir(string dataDir) => Path.Combine(dataDir, "control");

    /// <summary>
    /// Drapeau « annuler le traitement en cours ». Le worker le supprime dès qu'il l'a pris en
    /// compte.
    /// </summary>
    public static string CancelCurrentFlag(string dataDir) =>
        Path.Combine(ControlDir(dataDir), "cancel-current.flag");
}
