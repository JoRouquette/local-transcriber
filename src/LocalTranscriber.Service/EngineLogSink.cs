namespace LocalTranscriber.Service;

/// <summary>
/// Journal fichier tournant (un fichier par jour) sous <c>DataDir\logs</c>, partagé par le
/// worker : il y écrit les logs du moteur Python (stderr), du sidecar d'embeddings et les
/// évènements de traitement. La GUI, qui tourne dans un autre processus, lit (tail) ce fichier
/// pour afficher les logs du traitement en cours.
/// </summary>
public sealed class EngineLogSink
{
    private readonly string _dir;
    private readonly object _gate = new();

    public EngineLogSink(string logDir)
    {
        _dir = logDir;
        Directory.CreateDirectory(_dir);
        PruneOld();
    }

    /// <summary>Fichier du jour (nom triable, lu par la GUI qui prend le plus récent).</summary>
    public string CurrentFile => Path.Combine(_dir, $"worker-{DateTime.Now:yyyyMMdd}.log");

    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(
                    CurrentFile,
                    $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}"
                );
            }
        }
        catch
        { /* le journal ne doit jamais casser le traitement */
        }
    }

    /// <summary>Supprime les journaux de plus de 14 jours (best effort).</summary>
    private void PruneOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var f in Directory.EnumerateFiles(_dir, "worker-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch
        { /* sans importance */
        }
    }
}
