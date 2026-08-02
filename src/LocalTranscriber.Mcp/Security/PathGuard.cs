namespace LocalTranscriber.Mcp.Security;

/// <summary>
/// Garde-fou : confine toute lecture de fichier a l'interieur de OutputRoot
/// (anti-traversee de chemin). Toute demande hors racine est rejetee.
/// </summary>
public sealed class PathGuard
{
    private readonly string _root;

    public PathGuard(string outputRoot)
        => _root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public bool IsInside(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retourne le chemin complet valide, ou null s'il sort de la racine.</summary>
    public string? Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        return IsInside(full) ? full : null;
    }

    public string Root => _root;
}
