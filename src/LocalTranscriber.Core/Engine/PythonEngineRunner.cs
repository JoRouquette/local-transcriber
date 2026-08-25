using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalTranscriber.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalTranscriber.Core.Engine;

/// <summary>
/// Invoque le moteur Python gele (transcriber-engine.exe) pour un fichier.
/// La requete est passee via un fichier temporaire ; le resultat est lu sur stdout.
/// Le jeton HF est injecte par variable d'environnement (jamais ecrit sur disque).
/// </summary>
public sealed class PythonEngineRunner
{
    /// <summary>
    /// Attente maximale du drainage de stdout/stderr apres la sortie du process moteur. En regime
    /// normal le drainage est quasi instantane ; ce plafond n'existe que pour ne pas figer la file
    /// quand un sous-process orphelin retient les handles (voir <see cref="RunAsync"/>).
    /// </summary>
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly string _enginePath;
    private readonly string? _hfToken;
    private readonly ILogger _logger;

    public PythonEngineRunner(string enginePath, string? hfToken, ILogger? logger = null)
    {
        _enginePath = enginePath;
        _hfToken = hfToken;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <param name="onLog">Reçoit les lignes de log (stderr) du moteur, en temps réel.</param>
    /// <param name="inactivityTimeout">
    /// Si &gt; 0, le moteur est tué s'il n'émet plus aucune sortie pendant cette durée (garde-fou
    /// anti-blocage). Basé sur l'inactivité, pas la durée totale : un long fichier qui progresse
    /// n'est jamais interrompu. Null = pas de garde-fou.
    /// </param>
    public async Task<EngineResult> RunAsync(
        EngineRequest request,
        Action<string>? onLog = null,
        CancellationToken ct = default,
        TimeSpan? inactivityTimeout = null
    )
    {
        if (!File.Exists(_enginePath))
            return new EngineResult
            {
                Status = "error",
                AudioPath = request.AudioPath,
                Error = $"Moteur introuvable : {_enginePath}",
            };

        var reqPath = Path.Combine(Path.GetTempPath(), $"lt-req-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                reqPath,
                JsonSerializer.Serialize(request, JsonDefaults.Options),
                ct
            );
        }
        catch (Exception ex)
        {
            // Disque plein, %TEMP% inaccessible/verrouille par un antivirus... : on ne laisse pas
            // l'exception remonter (elle avorterait le job en amont), on renvoie une erreur propre.
            return new EngineResult
            {
                Status = "error",
                AudioPath = request.AudioPath,
                Error = $"Impossible d'ecrire la requete moteur ({reqPath}) : {ex.Message}",
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName = _enginePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--request");
        psi.ArgumentList.Add(reqPath);
        // On LIT les flux en UTF-8 (StandardOutput/ErrorEncoding ci-dessus) : il faut donc
        // imposer a Python d'y ECRIRE en UTF-8. Sans cela il utilise l'encodage console de
        // Windows (cp1252 en France), et deux choses cassent : les caracteres representables en
        // cp1252 (c cedille, points de suspension) sortent en octets invalides en UTF-8 et
        // s'affichent en caractere de remplacement ; ceux qui n'existent pas en cp1252 (la
        // fleche U+2192 des logs de chunking) sortent en texte litteral "→" via
        // backslashreplace. Cela ne touchait pas que les logs : le resultat moteur est
        // serialise avec ensure_ascii=False, donc les messages d'erreur et les noms de
        // locuteurs accentues arrivaient corrompus jusqu'a la GUI.
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (!string.IsNullOrWhiteSpace(_hfToken))
            psi.Environment["HF_TOKEN"] = _hfToken;

        var stdout = new StringBuilder();
        var bufferLock = new object();
        // Les handlers de flux tournent sur des threads du pool : on ne considere la lecture
        // terminee que lorsque la ligne sentinelle (e.Data == null) est recue pour chaque flux.
        var stdoutDone = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var stderrDone = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        // Garde-fou anti-blocage : on note l'instant de la derniere sortie du moteur ; un watchdog
        // tue le process s'il reste muet trop longtemps (deadlock CUDA, driver plante...).
        long lastActivityTicks = DateTime.UtcNow.Ticks;
        void MarkActivity() => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
        // CTS lie a l'annulation externe : le watchdog l'annulera aussi pour debloquer WaitForExit.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timedOut = false;

        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    stdoutDone.TrySetResult(true);
                    return;
                }
                MarkActivity();
                lock (bufferLock)
                    stdout.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    stderrDone.TrySetResult(true);
                    return;
                }
                MarkActivity();
                _logger.LogDebug("[engine] {Line}", e.Data);
                onLog?.Invoke(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Watchdog d'inactivite : boucle legere qui compare l'inactivite au seuil et, le cas
            // echeant, marque le timeout et annule runCts (ce qui fait sortir WaitForExitAsync).
            var watchdog = Task.CompletedTask;
            if (inactivityTimeout is { } limit && limit > TimeSpan.Zero)
            {
                watchdog = Task.Run(
                    async () =>
                    {
                        try
                        {
                            while (!runCts.IsCancellationRequested)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(15), runCts.Token);
                                var idle = TimeSpan.FromTicks(
                                    DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityTicks)
                                );
                                if (idle >= limit)
                                {
                                    timedOut = true;
                                    runCts.Cancel();
                                    return;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        { /* fin normale : le process s'est termine, on arrete le watchdog */
                        }
                    },
                    CancellationToken.None
                );
            }

            try
            {
                await proc.WaitForExitAsync(runCts.Token);
            }
            finally
            {
                // Le process est sorti (ou a ete annule) : on arrete le watchdog proprement.
                if (!runCts.IsCancellationRequested)
                    runCts.Cancel();
                try
                {
                    await watchdog;
                }
                catch
                { /* le watchdog ne remonte rien d'utile */
                }
            }
            // On attend le drainage complet des deux flux avant de lire les buffers : les handlers
            // peuvent encore avoir des lignes en attente au moment ou le process se termine.
            //
            // MAIS cette attente doit etre bornee : les sentinelles (e.Data == null) n'arrivent qu'a
            // la fermeture des handles stdout/stderr, et un sous-process Python orphelin (worker
            // dataloader, ffmpeg) peut les retenir alors que le process parent est deja sorti. Sans
            // timeout on figerait la file indefiniment, et sans garde-fou : le watchdog vient d'etre
            // annule juste au-dessus. A noter : tuer ne sert a rien ici (le parent est sorti, l'enfant
            // est reparente et n'est plus joignable via `proc`) -- la seule sortie est d'arreter
            // d'attendre et d'exploiter ce qui a deja ete recu (le JSON de resultat est flushe par le
            // moteur avant sa sortie, donc le job aboutit dans la plupart des cas).
            try
            {
                await Task.WhenAll(stdoutDone.Task, stderrDone.Task)
                    .WaitAsync(StreamDrainTimeout, ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "[engine] flux non draines apres {Seconds}s (sous-process orphelin ?) : on exploite la sortie deja recue.",
                    StreamDrainTimeout.TotalSeconds
                );
                try
                {
                    proc.CancelOutputRead();
                    proc.CancelErrorRead();
                }
                catch
                { /* best effort : la lecture asynchrone peut deja etre terminee */
                }
                KillTree(proc); // no-op si le parent est deja sorti, utile s'il vit encore
            }

            string raw;
            lock (bufferLock)
                raw = stdout.ToString().Trim();
            if (string.IsNullOrEmpty(raw))
                return new EngineResult
                {
                    Status = "error",
                    AudioPath = request.AudioPath,
                    Error = "Le moteur n'a rien renvoye.",
                };

            var result = JsonSerializer.Deserialize<EngineResult>(raw, JsonDefaults.Options);
            return result
                ?? new EngineResult
                {
                    Status = "error",
                    AudioPath = request.AudioPath,
                    Error = "Resultat moteur illisible.",
                };
        }
        catch (OperationCanceledException)
        {
            // On tue le process moteur et son arbre pour ne pas laisser de Python orphelin.
            KillTree(proc);

            // Timeout d'inactivite (watchdog) vs annulation externe (arret worker / annulation
            // utilisateur). L'annulation externe prime si les deux surviennent.
            if (timedOut && !ct.IsCancellationRequested)
            {
                var minutes = inactivityTimeout?.TotalMinutes ?? 0;
                return new EngineResult
                {
                    Status = "error",
                    AudioPath = request.AudioPath,
                    Error =
                        $"Moteur bloque : aucune activite pendant {minutes:0} min, traitement interrompu.",
                };
            }

            return new EngineResult
            {
                Status = "cancelled",
                AudioPath = request.AudioPath,
                Error = "Traitement annule.",
            };
        }
        catch (Exception ex)
        {
            return new EngineResult
            {
                Status = "error",
                AudioPath = request.AudioPath,
                Error = ex.Message,
            };
        }
        finally
        {
            proc?.Dispose();
            try
            {
                File.Delete(reqPath);
            }
            catch
            { /* best effort */
            }
        }
    }

    private static void KillTree(Process? proc)
    {
        try
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch
        { /* best effort */
        }
    }
}
