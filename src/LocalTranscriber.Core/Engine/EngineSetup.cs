using System.Diagnostics;

namespace LocalTranscriber.Core.Engine;

/// <summary>
/// Met en place l'environnement Python du moteur au premier lancement (installeur leger) :
/// via <c>uv</c>, cree un venv Python 3.11, installe torch (CPU/GPU) + les dependances
/// pinnees + le point d'entree console. L'installeur ne contient donc PAS le moteur gele,
/// seulement la source Python + le binaire uv (petit).
/// </summary>
public sealed class EngineSetup
{
    /// <summary>Dossier de l'environnement Python cree localement (persistant).</summary>
    public string EnvDir { get; }

    /// <summary>Dossier source du moteur (transcriber_engine/, requirements.txt, pyproject.toml).</summary>
    public string EngineSourceDir { get; }

    /// <summary>Executable console du moteur, une fois l'environnement pret.</summary>
    public string ConsoleExe => Path.Combine(EnvDir, "Scripts", "transcriber-engine.exe");

    private string VenvPython => Path.Combine(EnvDir, "Scripts", "python.exe");

    private readonly string _appDir;

    public EngineSetup(string? appDir = null, string? envDir = null)
    {
        _appDir = appDir ?? AppContext.BaseDirectory;
        EngineSourceDir = Path.Combine(_appDir, "engine");
        EnvDir = envDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalTranscriber", "engine-env");
    }

    /// <summary>L'environnement est pret si l'executable console existe.</summary>
    public bool IsReady => File.Exists(ConsoleExe);

    /// <summary>Localise uv : binaire embarque (appdir\uv\uv.exe) sinon uv du PATH.</summary>
    public string ResolveUv()
    {
        var bundled = Path.Combine(_appDir, "uv", "uv.exe");
        return File.Exists(bundled) ? bundled : "uv";
    }

    /// <summary>
    /// Installe l'environnement (idempotent). Diffuse la progression ligne a ligne.
    /// </summary>
    public async Task<bool> SetupAsync(IProgress<string>? progress, bool cuda = false, CancellationToken ct = default)
    {
        var uv = ResolveUv();
        var torchIndex = cuda
            ? "https://download.pytorch.org/whl/cu121"
            : "https://download.pytorch.org/whl/cpu";

        void Report(string m) => progress?.Report(m);

        if (!Directory.Exists(EngineSourceDir) || !File.Exists(Path.Combine(EngineSourceDir, "requirements.txt")))
        {
            Report($"Source du moteur introuvable : {EngineSourceDir}");
            return false;
        }

        Report("Creation de l'environnement Python 3.11 (uv)…");
        if (!await RunAsync(uv, new[] { "venv", "--python", "3.11", EnvDir }, progress, ct)) return false;

        Report("Installation de PyTorch…");
        if (!await RunAsync(uv, new[] { "pip", "install", "--python", VenvPython,
            "torch==2.2.2", "torchaudio==2.2.2", "--index-url", torchIndex }, progress, ct)) return false;

        Report("Installation des dependances du moteur…");
        if (!await RunAsync(uv, new[] { "pip", "install", "--python", VenvPython,
            "-r", Path.Combine(EngineSourceDir, "requirements.txt") }, progress, ct)) return false;

        Report("Installation du point d'entree du moteur…");
        if (!await RunAsync(uv, new[] { "pip", "install", "--python", VenvPython,
            "-e", EngineSourceDir, "--no-deps" }, progress, ct)) return false;

        var ok = IsReady;
        Report(ok ? "Moteur installe." : "Installation terminee mais executable introuvable.");
        return ok;
    }

    private static async Task<bool> RunAsync(string file, string[] args, IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) progress?.Report(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) progress?.Report(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                progress?.Report($"Echec (code {proc.ExitCode}) : {Path.GetFileName(file)} {string.Join(' ', args)}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report("Erreur : " + ex.Message);
            return false;
        }
    }
}
