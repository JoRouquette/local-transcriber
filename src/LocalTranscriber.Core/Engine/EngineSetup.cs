using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalTranscriber.Core.Configuration;

namespace LocalTranscriber.Core.Engine;

/// <summary>
/// Manifeste ecrit dans l'environnement du moteur apres une installation reussie. Permet de
/// savoir a quelle version de l'app / a quelles dependances correspond l'environnement en
/// place, et donc de detecter qu'une mise a jour du moteur est necessaire.
/// </summary>
public sealed record EngineManifest(
    int Schema,
    string AppVersion,
    string Fingerprint,
    string Torch,
    string InstalledAtUtc
);

/// <summary>
/// Met en place l'environnement Python du moteur (installeur leger) : via <c>uv</c>, cree un
/// venv Python 3.11, installe torch (CPU/GPU) + les dependances pinnees + le point d'entree
/// console (installe en editable, il pointe vers la source livree avec l'app). L'installeur ne
/// contient donc PAS le moteur gele, seulement la source Python + le binaire uv (petit).
///
/// Trois operations :
///   - <see cref="SetupAsync"/> / InstallAsync(recreate:false) : 1er lancement ou mise a jour
///     des dependances (le venv existant est reutilise).
///   - InstallAsync(recreate:true) : reinstallation propre (l'environnement est efface puis
///     recree de zero) — utile en cas d'environnement corrompu ou possede par un autre compte.
/// Un <see cref="EngineManifest"/> est (re)ecrit a chaque installation reussie.
/// </summary>
public sealed class EngineSetup
{
    /// <summary>Version du format du manifeste. A incrementer si le schema change.</summary>
    private const int ManifestSchema = 1;

    /// <summary>Pin PyTorch (installe hors requirements.txt : index dedie CPU/GPU).</summary>
    public const string TorchSpec = "torch==2.2.2 torchaudio==2.2.2";
    private static readonly string[] TorchPackages = { "torch==2.2.2", "torchaudio==2.2.2" };

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Dossier de l'environnement Python cree localement (persistant).</summary>
    public string EnvDir { get; }

    /// <summary>Dossier source du moteur (transcriber_engine/, requirements.txt, pyproject.toml).</summary>
    public string EngineSourceDir { get; }

    /// <summary>Executable console du moteur, une fois l'environnement pret.</summary>
    public string ConsoleExe => Path.Combine(EnvDir, "Scripts", "transcriber-engine.exe");

    private string VenvPython => Path.Combine(EnvDir, "Scripts", "python.exe");
    private string ManifestPath => Path.Combine(EnvDir, ".engine-manifest.json");
    private string RequirementsPath => Path.Combine(EngineSourceDir, "requirements.txt");

    private readonly string _appDir;

    public EngineSetup(string? appDir = null, string? envDir = null)
    {
        _appDir = appDir ?? AppContext.BaseDirectory;
        EngineSourceDir = Path.Combine(_appDir, "engine");
        EnvDir = string.IsNullOrWhiteSpace(envDir) ? DefaultEnvDir : ConfigStore.ExpandPath(envDir);
    }

    /// <summary>
    /// Emplacement par defaut (profil utilisateur). La GUI comme le worker tournent desormais
    /// sous le compte de l'utilisateur : un dossier par-utilisateur evite le piege de propriete
    /// SYSTEM herite de l'ancien service LocalSystem.
    /// </summary>
    public static string DefaultEnvDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalTranscriberData",
            "engine-env"
        );

    /// <summary>Construit l'installeur a partir de la configuration (chemin d'env personnalisable).</summary>
    public static EngineSetup FromConfig(AppConfig config, string? appDir = null) =>
        new(appDir, config.EngineEnvDir);

    /// <summary>L'environnement est pret si l'executable console existe.</summary>
    public bool IsReady => File.Exists(ConsoleExe);

    /// <summary>
    /// Empreinte attendue des dependances (hash de requirements.txt + pin torch + schema).
    /// Ne change que si les dependances changent reellement (pas a chaque version d'app).
    /// </summary>
    public string ExpectedFingerprint()
    {
        var reqs = File.Exists(RequirementsPath) ? File.ReadAllText(RequirementsPath) : "";
        var payload = $"schema={ManifestSchema}\n{TorchSpec}\n{reqs}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Lit le manifeste de l'environnement installe (null si absent/illisible).</summary>
    public EngineManifest? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath)
                ? JsonSerializer.Deserialize<EngineManifest>(
                    File.ReadAllText(ManifestPath),
                    ManifestJson
                )
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Moteur installe ET aligne sur les dependances attendues.</summary>
    public bool IsUpToDate => IsReady && ReadManifest()?.Fingerprint == ExpectedFingerprint();

    /// <summary>Localise uv : binaire embarque (appdir\uv\uv.exe) sinon uv du PATH.</summary>
    public string ResolveUv()
    {
        var bundled = Path.Combine(_appDir, "uv", "uv.exe");
        return File.Exists(bundled) ? bundled : "uv";
    }

    /// <summary>
    /// 1er lancement ou mise a jour (le venv existant est reutilise, les dependances resynchronisees).
    /// </summary>
    public Task<bool> SetupAsync(
        IProgress<string>? progress,
        bool cuda = false,
        CancellationToken ct = default
    ) => InstallAsync(recreate: false, progress, cuda, appVersion: null, ct);

    /// <summary>
    /// Installe (ou met a jour) l'environnement. Idempotent. Diffuse la progression ligne a ligne.
    /// </summary>
    /// <param name="recreate">
    /// true : reinstallation propre — l'environnement est entierement efface puis recree.
    /// false : le venv existant est conserve, seules les dependances sont (re)synchronisees.
    /// </param>
    /// <param name="appVersion">Version de l'app a inscrire dans le manifeste (defaut : version de l'assembly).</param>
    public async Task<bool> InstallAsync(
        bool recreate,
        IProgress<string>? progress,
        bool cuda = false,
        string? appVersion = null,
        CancellationToken ct = default
    )
    {
        void Report(string m) => progress?.Report(m);

        if (!Directory.Exists(EngineSourceDir) || !File.Exists(RequirementsPath))
        {
            Report($"Source du moteur introuvable : {EngineSourceDir}");
            return false;
        }

        var uv = ResolveUv();
        var torchIndex = cuda
            ? "https://download.pytorch.org/whl/cu121"
            : "https://download.pytorch.org/whl/cpu";

        if (recreate)
        {
            Report("Reinstallation propre : suppression de l'environnement existant…");
            try
            {
                await DeleteEnvRobust(progress, ct);
            }
            catch (UnauthorizedAccessException)
            {
                Report(
                    "Echec : acces refuse a l'environnement existant. Il appartient probablement a "
                        + "un autre compte (SYSTEM, herite de l'ancien service). Supprimez-le "
                        + $"manuellement puis relancez : {EnvDir}"
                );
                return false;
            }
            catch (Exception ex)
            {
                Report($"Echec de la suppression de l'environnement : {ex.Message}");
                return false;
            }
        }

        var needVenv = recreate || !File.Exists(VenvPython);
        if (needVenv)
        {
            Report("Creation de l'environnement Python 3.11 (uv)…");
            // --clear : garantit un venv propre meme si un reliquat subsiste.
            if (
                !await RunAsync(
                    uv,
                    new[] { "venv", "--python", "3.11", "--clear", EnvDir },
                    progress,
                    ct
                )
            )
                return false;
        }
        else
        {
            Report("Mise a jour du moteur : environnement existant conserve.");
        }

        Report("Installation / mise a jour de PyTorch…");
        var torchArgs = new List<string> { "pip", "install", "--python", VenvPython };
        torchArgs.AddRange(TorchPackages);
        torchArgs.AddRange(new[] { "--index-url", torchIndex });
        if (!await RunAsync(uv, torchArgs.ToArray(), progress, ct))
            return false;

        Report("Installation / mise a jour des dependances du moteur…");
        if (
            !await RunAsync(
                uv,
                new[] { "pip", "install", "--python", VenvPython, "-r", RequirementsPath },
                progress,
                ct
            )
        )
            return false;

        Report("Installation du point d'entree du moteur…");
        if (
            !await RunAsync(
                uv,
                new[]
                {
                    "pip",
                    "install",
                    "--python",
                    VenvPython,
                    "-e",
                    EngineSourceDir,
                    "--no-deps",
                },
                progress,
                ct
            )
        )
            return false;

        if (!IsReady)
        {
            Report("Installation terminee mais executable introuvable.");
            return false;
        }

        WriteManifest(appVersion, progress);
        Report(recreate ? "Moteur reinstalle." : "Moteur a jour.");
        return true;
    }

    /// <summary>Ecrit le manifeste de version dans l'environnement (best effort).</summary>
    private void WriteManifest(string? appVersion, IProgress<string>? progress)
    {
        try
        {
            var manifest = new EngineManifest(
                ManifestSchema,
                appVersion ?? EntryAssemblyVersion(),
                ExpectedFingerprint(),
                TorchSpec,
                DateTime.UtcNow.ToString("o")
            );
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, ManifestJson));
        }
        catch (Exception ex)
        {
            progress?.Report($"Avertissement : manifeste non ecrit ({ex.Message}).");
        }
    }

    private static string EntryAssemblyVersion() =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetName()
            .Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    /// Supprime l'environnement de facon robuste : retire les attributs read-only et retente
    /// quelques fois (un verrou antivirus ou un handle en cours de liberation peut echouer une
    /// premiere tentative). Laisse remonter l'exception finale pour un message clair.
    /// </summary>
    private async Task DeleteEnvRobust(IProgress<string>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(EnvDir))
            return;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (
                    var file in Directory.EnumerateFiles(EnvDir, "*", SearchOption.AllDirectories)
                )
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch
                    { /* on tente quand meme la suppression */
                    }
                }
                Directory.Delete(EnvDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException && attempt < 4)
            {
                progress?.Report(
                    $"Suppression de l'environnement… nouvelle tentative ({attempt}/3) : {ex.Message}"
                );
                await Task.Delay(1000, ct);
            }
        }
    }

    private static async Task<bool> RunAsync(
        string file,
        string[] args,
        IProgress<string>? progress,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    progress?.Report(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    progress?.Report(e.Data);
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                progress?.Report(
                    $"Echec (code {proc.ExitCode}) : {Path.GetFileName(file)} {string.Join(' ', args)}"
                );
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            // Annulation : on tue l'arbre de process pour ne pas laisser uv/pip orphelin.
            KillTree(proc);
            return false;
        }
        catch (Exception ex)
        {
            progress?.Report("Erreur : " + ex.Message);
            return false;
        }
        finally
        {
            proc?.Dispose();
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
