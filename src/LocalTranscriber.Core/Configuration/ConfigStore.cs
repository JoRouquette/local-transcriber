using System.Text.Json;
using LocalTranscriber.Core.Contracts;

namespace LocalTranscriber.Core.Configuration;

/// <summary>
/// Chargement / sauvegarde de la configuration, partages par la GUI et le service.
/// Emplacement par defaut : %PROGRAMDATA%\LocalTranscriber\config.json (lisible par le
/// service qui tourne sous un compte systeme comme par la GUI de l'utilisateur).
/// </summary>
public static class ConfigStore
{
    public static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LocalTranscriber",
            "config.json"
        );

    /// <summary>Developpe les variables d'environnement Windows (%LOCALAPPDATA%, ...).</summary>
    public static string ExpandPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? path : Environment.ExpandEnvironmentVariables(path);

    /// <summary>Fichier de secrets local (hors depot) qui surcharge le config partageable.</summary>
    public const string LocalFileName = "config.local.json";

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;

        AppConfig config;
        if (File.Exists(path))
        {
            try
            {
                config =
                    JsonSerializer.Deserialize<AppConfig>(
                        File.ReadAllText(path),
                        JsonDefaults.Options
                    ) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                // Config corrompue (crash pendant une ecriture, edition manuelle, antivirus...) :
                // ne JAMAIS faire tomber le service. Cette exception remontait auparavant avant
                // meme la construction de l'hote (donc avant tout logger), rendant le demarrage
                // impossible sans trace. On sauvegarde le fichier fautif pour diagnostic et on
                // repart sur les valeurs par defaut.
                BackupCorruptConfig(path, ex);
                config = new AppConfig();
            }
        }
        else
        {
            config = new AppConfig();
        }

        MigrateLegacyUserPaths(config);
        ApplyLocalOverlay(config, path);
        return config;
    }

    /// <summary>Copie le config illisible vers un fichier horodate et trace la cause. Ne leve jamais.</summary>
    private static void BackupCorruptConfig(string path, Exception ex)
    {
        try
        {
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
            Console.Error.WriteLine(
                $"[config] {path} illisible ({ex.Message}). Copie de diagnostic : {backup}. "
                    + "Valeurs par defaut appliquees."
            );
        }
        catch
        { /* best effort : le diagnostic ne doit pas empecher le demarrage */
        }
    }

    /// <summary>
    /// Bascule en memoire les anciens emplacements par utilisateur (%LOCALAPPDATA%) vers
    /// %PROGRAMDATA% pour que la GUI (utilisateur) et le service (LocalSystem) partagent
    /// les memes fichiers. Ne touche pas aux chemins personnalises par l'utilisateur.
    /// </summary>
    private static void MigrateLegacyUserPaths(AppConfig config)
    {
        static string Fix(string p) =>
            string.IsNullOrWhiteSpace(p)
                ? p
                : p.Replace(
                    @"%LOCALAPPDATA%\LocalTranscriber",
                    @"%PROGRAMDATA%\LocalTranscriber",
                    StringComparison.OrdinalIgnoreCase
                );

        config.DataDir = Fix(config.DataDir);
        config.ModelCacheDir = Fix(config.ModelCacheDir);
    }

    /// <summary>
    /// Superpose les secrets de config.local.json (hf_token). Le token est stocke CHIFFRE
    /// (DPAPI CurrentUser, champ <c>hf_token_enc</c>) ; on tolere l'ancien format en clair
    /// (<c>hf_token</c>) pour la migration — il sera reecrit chiffre au prochain Save. Cherche
    /// le fichier a cote du config, puis dans le repertoire courant, puis a cote de l'exe.
    /// </summary>
    private static void ApplyLocalOverlay(AppConfig config, string mainPath)
    {
        foreach (var dir in LocalSearchDirs(mainPath))
        {
            var local = Path.Combine(dir, LocalFileName);
            if (!File.Exists(local))
                continue;
            try
            {
                var overlay = JsonSerializer.Deserialize<LocalSecrets>(
                    File.ReadAllText(local),
                    JsonDefaults.Options
                );
                if (overlay is not null)
                {
                    // Priorite au chiffre ; repli sur l'ancien clair (migration transparente).
                    var token = SecretProtector.Unprotect(overlay.HfTokenEnc);
                    if (string.IsNullOrWhiteSpace(token))
                        token = overlay.HfToken;
                    if (!string.IsNullOrWhiteSpace(token))
                        config.HfToken = token;
                }
            }
            catch
            { /* fichier local invalide : on ignore */
            }
            break; // premier trouve = gagnant
        }
    }

    private static IEnumerable<string> LocalSearchDirs(string mainPath)
    {
        var configDir = Path.GetDirectoryName(mainPath);
        if (!string.IsNullOrEmpty(configDir))
            yield return configDir;
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultConfigPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Le secret (hf_token) ne va pas dans le config partageable, mais dans config.local.json.
        // try/finally : le token est toujours restaure en memoire, meme si la serialisation leve.
        var token = config.HfToken;
        config.HfToken = null;
        try
        {
            WriteAtomic(path, JsonSerializer.Serialize(config, JsonDefaults.Options));
        }
        finally
        {
            config.HfToken = token; // restaure l'objet en memoire
        }

        var localPath = Path.Combine(dir, LocalFileName);
        if (!string.IsNullOrWhiteSpace(token))
        {
            // Chiffrement au repos (DPAPI CurrentUser). Si le chiffrement echoue (contexte sans
            // DPAPI, tres improbable sous Windows), on ne perd pas le token : repli en clair avec
            // avertissement, plutot que de casser la diarisation.
            var enc = SecretProtector.Protect(token);
            var secrets = enc is not null
                ? new LocalSecrets { HfTokenEnc = enc }
                : new LocalSecrets { HfToken = token };
            if (enc is null)
                Console.Error.WriteLine(
                    "[config] Chiffrement DPAPI indisponible : le token HF est stocke en clair dans "
                        + LocalFileName
                        + "."
                );
            WriteAtomic(localPath, JsonSerializer.Serialize(secrets, JsonDefaults.Options));
        }
        else if (File.Exists(localPath))
            // Plus de token : on retire le fichier de secrets plutot que de laisser un secret perime.
            File.Delete(localPath);
    }

    /// <summary>Ecriture atomique : fichier temporaire puis remplacement par <see cref="File.Move"/>.</summary>
    private static void WriteAtomic(string destPath, string content)
    {
        var tempPath = destPath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, destPath, overwrite: true);
    }

    private sealed class LocalSecrets
    {
        /// <summary>Ancien format : token en clair. Conserve en lecture pour la migration.</summary>
        public string? HfToken { get; set; }

        /// <summary>Format courant : token chiffre DPAPI (base64), champ JSON <c>hf_token_enc</c>.</summary>
        public string? HfTokenEnc { get; set; }
    }
}
