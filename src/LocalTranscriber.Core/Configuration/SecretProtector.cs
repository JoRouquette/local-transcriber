using System.Security.Cryptography;
using System.Text;

namespace LocalTranscriber.Core.Configuration;

/// <summary>
/// Chiffrement au repos des secrets (jeton Hugging Face) via DPAPI en portee CURRENT_USER :
/// le chiffre n'est dechiffrable que par le compte Windows qui l'a produit. Meme si
/// <c>config.local.json</c> reste lisible par d'autres comptes locaux (ACL heritee de
/// %PROGRAMDATA%), ils ne peuvent pas recuperer le token en clair.
///
/// Viable car la GUI et le worker tournent desormais sous LE MEME utilisateur (tache planifiee
/// en session utilisateur, plus de service LocalSystem). DPAPI est Windows-only : hors Windows
/// (jamais en pratique) on retombe sur un simple encodage base64, non secret.
/// </summary>
public static class SecretProtector
{
    // Entropie additionnelle : lie le chiffre a cette application (defense en profondeur).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalTranscriber.secret.v1");

    /// <summary>Chiffre une chaine en base64 (DPAPI CurrentUser). Retourne null si l'entree est vide.</summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return null;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var enc = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch
            {
                // Cas tres improbable (contexte sans DPAPI). On ne veut pas perdre le token :
                // l'appelant retombera sur un stockage en clair, avec avertissement.
                return null;
            }
        }
        return null;
    }

    /// <summary>Dechiffre une chaine produite par <see cref="Protect"/>. Null si illisible/vide.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
            return null;
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var enc = Convert.FromBase64String(protectedBase64);
            var bytes = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Chiffre produit par un AUTRE utilisateur/machine, ou corrompu : indechiffrable ici.
            // On renvoie null : le token sera considere absent (message clair cote diarisation).
            return null;
        }
    }
}
