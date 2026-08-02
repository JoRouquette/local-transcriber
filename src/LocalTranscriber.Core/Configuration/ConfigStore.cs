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
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LocalTranscriber", "config.json");

    /// <summary>Developpe les variables d'environnement Windows (%LOCALAPPDATA%, ...).</summary>
    public static string ExpandPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? path : Environment.ExpandEnvironmentVariables(path);

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        if (!File.Exists(path))
            return new AppConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonDefaults.Options) ?? new AppConfig();
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, JsonDefaults.Options);
        File.WriteAllText(path, json);
    }
}
