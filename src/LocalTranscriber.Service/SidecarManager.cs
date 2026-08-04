using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace LocalTranscriber.Service;

/// <summary>
/// Pilote le sidecar d'embeddings (moteur gele en mode --serve-embeddings) :
/// demarrage, surveillance, redemarrage si mort. Le modele reste charge en memoire.
/// </summary>
public sealed class SidecarManager : IDisposable
{
    private readonly ILogger _logger;
    private Process? _proc;

    /// <summary>Reçoit les lignes de log du sidecar (pour le journal fichier / la GUI).</summary>
    public Action<string>? OnLog { get; set; }

    public SidecarManager(ILogger logger) => _logger = logger;

    public bool IsRunning => _proc is { HasExited: false };

    public async Task EnsureStartedAsync(
        string enginePath,
        int port,
        string cacheDir,
        string device,
        CancellationToken ct
    )
    {
        if (IsRunning)
            return;
        if (!File.Exists(enginePath))
        {
            _logger.LogWarning(
                "Sidecar embeddings : moteur introuvable ({Engine}). Recherche semantique indisponible.",
                enginePath
            );
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = enginePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--serve-embeddings");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(device);
        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            psi.ArgumentList.Add("--cache-dir");
            psi.ArgumentList.Add(cacheDir);
        }

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            _logger.LogDebug("[embeddings] {Line}", e.Data);
            OnLog?.Invoke("[embeddings] " + e.Data);
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            _logger.LogDebug("[embeddings] {Line}", e.Data);
            OnLog?.Invoke("[embeddings] " + e.Data);
        };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        _logger.LogInformation(
            "Sidecar embeddings demarre (port {Port}), chargement du modele...",
            port
        );

        // Attend que le port accepte les connexions (chargement/telechargement du modele).
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_proc.HasExited)
            {
                _logger.LogError("Le sidecar embeddings s'est arrete au demarrage.");
                return;
            }
            if (await CanConnectAsync(port, ct))
            {
                _logger.LogInformation("Sidecar embeddings pret.");
                return;
            }
            await Task.Delay(1000, ct);
        }
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(3000);
            }
        }
        catch
        { /* best effort */
        }
        _proc?.Dispose();
    }
}
