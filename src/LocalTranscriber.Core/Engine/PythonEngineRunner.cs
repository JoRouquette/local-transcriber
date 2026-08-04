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
    public async Task<EngineResult> RunAsync(
        EngineRequest request,
        Action<string>? onLog = null,
        CancellationToken ct = default
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
        await File.WriteAllTextAsync(
            reqPath,
            JsonSerializer.Serialize(request, JsonDefaults.Options),
            ct
        );

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
        if (!string.IsNullOrWhiteSpace(_hfToken))
            psi.Environment["HF_TOKEN"] = _hfToken;

        var stdout = new StringBuilder();
        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stdout.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;
                _logger.LogDebug("[engine] {Line}", e.Data);
                onLog?.Invoke(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            var raw = stdout.ToString().Trim();
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
            // Annulation (arret du worker ou annulation du job) : on tue le process moteur
            // et son arbre pour ne pas laisser de Python orphelin, puis on signale l'annulation.
            KillTree(proc);
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
