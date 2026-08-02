using LocalTranscriber.Core.Configuration;

namespace LocalTranscriber.Core.Paths;

/// <summary>
/// Traduit un fichier audio du dossier surveille vers son dossier de sortie miroir,
/// et retrouve le projet auquel il appartient.
/// </summary>
public static class PathResolver
{
    /// <summary>Chemin relatif du fichier par rapport a la racine surveillee.</summary>
    public static string RelativeTo(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel;
    }

    /// <summary>Dossier de sortie miroir (meme arborescence sous outputRoot).</summary>
    public static string ResolveOutputDir(string watchRoot, string outputRoot, string audioPath)
    {
        var relDir = Path.GetDirectoryName(RelativeTo(watchRoot, audioPath)) ?? "";
        return Path.GetFullPath(Path.Combine(outputRoot, relDir));
    }

    public static string BaseName(string audioPath) =>
        Path.GetFileNameWithoutExtension(audioPath);

    /// <summary>
    /// Retrouve le projet dont le <c>RelativePath</c> prefixe le fichier (correspondance
    /// la plus longue). Retourne null si le fichier est a la racine (reglages globaux).
    /// </summary>
    public static ProjectConfig? FindProject(AppConfig config, string audioPath)
    {
        var rel = RelativeTo(config.WatchRoot, audioPath).Replace('\\', '/');
        ProjectConfig? best = null;
        var bestLen = -1;
        foreach (var p in config.Projects)
        {
            var pp = p.RelativePath.Replace('\\', '/').Trim('/');
            if (pp.Length == 0) continue;
            if ((rel + "/").StartsWith(pp + "/", StringComparison.OrdinalIgnoreCase) && pp.Length > bestLen)
            {
                best = p;
                bestLen = pp.Length;
            }
        }
        return best;
    }

    /// <summary>Dossier des snippets de voix pour un projet, s'il existe.</summary>
    public static string? ResolveVoicesDir(AppConfig config, ProjectConfig? project, string voicesDirName)
    {
        if (project is null || string.IsNullOrWhiteSpace(voicesDirName))
            return null;
        var dir = Path.Combine(config.WatchRoot, project.RelativePath, voicesDirName);
        return Directory.Exists(dir) ? Path.GetFullPath(dir) : null;
    }
}
