using System.Security.Cryptography;

namespace LocalTranscriber.Core.Security;

/// <summary>
/// Jeton d'acces local, partage entre le worker (qui heberge le MCP et le sidecar), la GUI (qui
/// l'affiche) et — via l'URL configuree — Claude Desktop. Stocke en clair dans un fichier
/// SOUS %LOCALAPPDATA% (per-utilisateur, ACL par defaut restreinte au proprietaire) : sur une
/// machine multi-comptes, un autre utilisateur ne peut donc pas le lire pour appeler le MCP
/// loopback. Le worker et la GUI tournent sous le meme utilisateur, ils voient le meme fichier.
/// </summary>
public static class AccessToken
{
    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalTranscriberData",
            "access.token"
        );

    /// <summary>Retourne le jeton existant ou en cree un (aleatoire) s'il n'existe pas encore.</summary>
    public static string GetOrCreate()
    {
        var existing = Read();
        if (existing is not null)
            return existing;

        var token = Generate();
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, token);
        }
        catch
        { /* si l'ecriture echoue, on renvoie quand meme un jeton utilisable pour cette session */
        }
        return token;
    }

    /// <summary>Lit le jeton s'il existe (sans en creer). Null si absent/illisible.</summary>
    public static string? Read()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return null;
            var t = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Comparaison a temps constant (evite une fuite par timing, bonne pratique).</summary>
    public static bool Matches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
            return false;
        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Generate()
    {
        // 24 octets aleatoires, encodes en base64 URL-safe (utilisable tel quel dans une URL).
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
