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
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonDefaults.Options)
                ?? new AppConfig()
            : new AppConfig();

        MigrateLegacyUserPaths(config);
        ApplyLocalOverlay(config, path);
        return config;
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
    /// Superpose les secrets de config.local.json (actuellement : hf_token). Cherche le
    /// fichier a cote du config, puis dans le repertoire courant, puis a cote de l'exe —
    /// ce qui couvre l'app installee comme l'execution en dev depuis le depot.
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
                var overlay = JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(local),
                    JsonDefaults.Options
                );
                if (overlay is not null && !string.IsNullOrWhiteSpace(overlay.HfToken))
                    config.HfToken = overlay.HfToken;
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
        var token = config.HfToken;
        config.HfToken = null;
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonDefaults.Options));
        config.HfToken = token; // restaure l'objet en memoire

        var localPath = Path.Combine(dir, LocalFileName);
        if (!string.IsNullOrWhiteSpace(token))
            File.WriteAllText(
                localPath,
                JsonSerializer.Serialize(new LocalSecrets { HfToken = token }, JsonDefaults.Options)
            );
    }

    private sealed class LocalSecrets
    {
        public string? HfToken { get; set; }
    }
}
