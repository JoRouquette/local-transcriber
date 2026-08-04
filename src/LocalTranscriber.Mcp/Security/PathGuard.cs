namespace LocalTranscriber.Mcp.Security;

/// <summary>
/// Garde-fou : confine toute lecture de fichier a l'interieur de OutputRoot
/// (anti-traversee de chemin). Toute demande hors racine est rejetee.
/// </summary>
public sealed class PathGuard
{
    private readonly string _root;

    public PathGuard(string outputRoot) =>
        _root =
            Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

    public bool IsInside(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
            when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Chemin malforme (caracteres invalides, trop long...) : considere hors perimetre.
            return false;
        }

        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            return false;

        // Anti-contournement par lien symbolique / jonction : si le chemin existe et pointe
        // (via un ou plusieurs liens) hors de la racine, on refuse. ResolveLinkTarget(true)
        // suit toute la chaine de liens jusqu'a la cible finale (null si ce n'est pas un lien).
        try
        {
            FileSystemInfo? finalTarget = null;
            if (File.Exists(full))
                finalTarget = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            else if (Directory.Exists(full))
                finalTarget = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);

            if (finalTarget is not null)
            {
                var targetFull = Path.GetFullPath(finalTarget.FullName);
                if (!targetFull.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or PathTooLongException
            )
        {
            // En cas de doute sur la resolution du lien, on refuse (fail-safe).
            return false;
        }

        return true;
    }

    /// <summary>Retourne le chemin complet valide, ou null s'il sort de la racine.</summary>
    public string? Resolve(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
            when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return IsInside(full) ? full : null;
    }

    public string Root => _root;
}
