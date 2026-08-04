using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Core.Search;

public sealed record VectorHit(
    string Path,
    string Project,
    string BaseName,
    string Speaker,
    double Start,
    string Text,
    double Score
);

/// <summary>
/// Stockage et recherche des vecteurs de fragments (SQLite, BLOB float32).
/// Recherche par cosinus en force brute : les vecteurs e5 etant normalises,
/// cosinus = produit scalaire. Suffisant a l'echelle personnelle ; on pourra
/// passer a sqlite-vec si le volume grandit.
/// </summary>
public sealed class VectorStore
{
    private readonly string _connectionString;
    private readonly bool _readOnly;

    public VectorStore(string dbPath, bool readOnly = false)
    {
        _readOnly = readOnly;
        if (!readOnly)
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        if (!readOnly)
            EnsureCreated();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        if (!_readOnly)
        {
            using var pragma = c.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
        return c;
    }

    private void EnsureCreated()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chunks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                path        TEXT NOT NULL,
                project     TEXT,
                base_name   TEXT,
                chunk_index INTEGER,
                speaker     TEXT,
                start       REAL,
                "end"       REAL,
                text        TEXT,
                dim         INTEGER,
                vector      BLOB,
                mtime       TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_chunks_path ON chunks(path);
            CREATE INDEX IF NOT EXISTS ix_chunks_project ON chunks(project);
            """;
        cmd.ExecuteNonQuery();
    }

    public bool IsUpToDate(string path, string mtime)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT mtime FROM chunks WHERE path=$p LIMIT 1";
        cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteScalar() is string existing && existing == mtime;
    }

    public static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBlob(byte[] blob)
    {
        var vec = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
        return vec;
    }

    public void ReplaceForPath(
        string path,
        string project,
        string baseName,
        string mtime,
        IReadOnlyList<(TranscriptChunk Chunk, float[] Vector)> items
    )
    {
        if (_readOnly)
            throw new InvalidOperationException("VectorStore ouvert en lecture seule.");
        using var c = Open();
        using var tx = c.BeginTransaction();

        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM chunks WHERE path=$p";
            del.Parameters.AddWithValue("$p", path);
            del.ExecuteNonQuery();
        }

        foreach (var (chunk, vector) in items)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO chunks (path, project, base_name, chunk_index, speaker, start, "end", text, dim, vector, mtime)
                VALUES ($p, $proj, $b, $idx, $spk, $st, $en, $txt, $dim, $vec, $mt);
                """;
            ins.Parameters.AddWithValue("$p", path);
            ins.Parameters.AddWithValue("$proj", project);
            ins.Parameters.AddWithValue("$b", baseName);
            ins.Parameters.AddWithValue("$idx", chunk.Index);
            ins.Parameters.AddWithValue("$spk", chunk.Speaker);
            ins.Parameters.AddWithValue("$st", chunk.Start);
            ins.Parameters.AddWithValue("$en", chunk.End);
            ins.Parameters.AddWithValue("$txt", chunk.Text);
            ins.Parameters.AddWithValue("$dim", vector.Length);
            ins.Parameters.AddWithValue("$vec", ToBlob(vector));
            ins.Parameters.AddWithValue("$mt", mtime);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteMissing(IEnumerable<string> existingPaths)
    {
        if (_readOnly)
            return;
        var keep = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        using var c = Open();
        var toDelete = new List<string>();
        using (var sel = c.CreateCommand())
        {
            sel.CommandText = "SELECT DISTINCT path FROM chunks";
            using var r = sel.ExecuteReader();
            while (r.Read())
                if (!keep.Contains(r.GetString(0)))
                    toDelete.Add(r.GetString(0));
        }
        foreach (var p in toDelete)
        {
            using var del = c.CreateCommand();
            del.CommandText = "DELETE FROM chunks WHERE path=$p";
            del.Parameters.AddWithValue("$p", p);
            del.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<VectorHit> Search(
        float[] query,
        string? project = null,
        string? speaker = null,
        int topK = 20
    )
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Filtre par dimension : seuls les vecteurs de meme dimension que la requete sont
        // comparables. Ecarte d'emblee les vecteurs produits par un modele d'embedding different.
        cmd.CommandText = """
            SELECT path, project, base_name, speaker, start, text, vector
            FROM chunks
            WHERE ($proj IS NULL OR project = $proj)
              AND ($spk IS NULL OR speaker = $spk)
              AND dim = $qdim
            """;
        cmd.Parameters.AddWithValue("$proj", (object?)project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spk", (object?)speaker ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$qdim", query.Length);

        var scored = new List<VectorHit>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var vec = FromBlob((byte[])r["vector"]);
                var score = Dot(query, vec);
                scored.Add(
                    new VectorHit(
                        r.GetString(0),
                        r.GetString(1),
                        r.GetString(2),
                        r.GetString(3),
                        r.GetDouble(4),
                        r.GetString(5),
                        score
                    )
                );
            }
        }
        return scored.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    private static double Dot(float[] a, float[] b)
    {
        // Dimensions differentes = vecteurs non comparables : score neutre (0) plutot qu'un
        // produit partiel trompeur. En pratique la requete SQL filtre deja par dimension.
        if (a.Length != b.Length)
            return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }
}
