using System.Security.Cryptography;

namespace LocalTranscriber.Core.Engine;

public static class FileHasher
{
    /// <summary>
    /// Empreinte rapide et stable d'un fichier : taille + SHA-256 des premiers et
    /// derniers 1 Mo. Suffisant pour l'idempotence sans lire des fichiers audio entiers.
    /// </summary>
    public static string QuickHash(string path)
    {
        var info = new FileInfo(path);
        const int chunk = 1024 * 1024;
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);

        var head = new byte[Math.Min(chunk, (int)Math.Min(info.Length, chunk))];
        _ = fs.Read(head, 0, head.Length);
        sha.TransformBlock(head, 0, head.Length, null, 0);

        if (info.Length > chunk)
        {
            fs.Seek(-Math.Min(chunk, info.Length - chunk), SeekOrigin.End);
            var tail = new byte[chunk];
            var read = fs.Read(tail, 0, tail.Length);
            sha.TransformBlock(tail, 0, read, null, 0);
        }

        var sizeBytes = BitConverter.GetBytes(info.Length);
        sha.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);
        return Convert.ToHexString(sha.Hash!);
    }
}
