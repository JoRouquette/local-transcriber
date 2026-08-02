using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Core.Jobs;

/// <summary>
/// File de traitement persistante (SQLite). Restart-safe et idempotente : un fichier
/// deja transcrit (meme hash) n'est jamais retraite. Partagee par le service ; la GUI
/// peut la lire pour afficher l'etat de la file.
/// </summary>
public sealed class JobStore
{
    private readonly string _connectionString;

    public JobStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        EnsureCreated();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void EnsureCreated()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                audio_path  TEXT NOT NULL,
                file_hash   TEXT NOT NULL,
                output_dir  TEXT NOT NULL,
                base_name   TEXT NOT NULL,
                status      TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL,
                error       TEXT,
                attempts    INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_hash ON jobs(file_hash);
            CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs(status);
            """;
        cmd.ExecuteNonQuery();
    }

    public bool AlreadyKnown(string fileHash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM jobs WHERE file_hash=$h AND status IN ('Pending','Processing','Done')";
        cmd.Parameters.AddWithValue("$h", fileHash);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Ajoute un job s'il n'existe pas deja. Retourne l'id, ou null si ignore.</summary>
    public long? Enqueue(string audioPath, string fileHash, string outputDir, string baseName)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO jobs (audio_path, file_hash, output_dir, base_name, status, created_at, updated_at)
            VALUES ($p, $h, $o, $b, 'Pending', $now, $now);
            SELECT id FROM jobs WHERE file_hash=$h;
            """;
        var now = DateTime.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$p", audioPath);
        cmd.Parameters.AddWithValue("$h", fileHash);
        cmd.Parameters.AddWithValue("$o", outputDir);
        cmd.Parameters.AddWithValue("$b", baseName);
        cmd.Parameters.AddWithValue("$now", now);
        var id = cmd.ExecuteScalar();
        return id is null ? null : Convert.ToInt64(id);
    }

    /// <summary>Prend le prochain job en attente et le passe en Processing (atomique).</summary>
    public TranscriptionJob? DequeueNext()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var sel = c.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT id, audio_path, file_hash, output_dir, base_name, attempts FROM jobs WHERE status='Pending' ORDER BY id LIMIT 1";
        long id;
        TranscriptionJob job;
        using (var r = sel.ExecuteReader())
        {
            if (!r.Read()) return null;
            id = r.GetInt64(0);
            job = new TranscriptionJob
            {
                Id = id,
                AudioPath = r.GetString(1),
                FileHash = r.GetString(2),
                OutputDir = r.GetString(3),
                BaseName = r.GetString(4),
                Attempts = r.GetInt32(5),
                Status = JobStatus.Processing,
            };
        }

        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE jobs SET status='Processing', attempts=attempts+1, updated_at=$now WHERE id=$id";
        upd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();
        tx.Commit();
        return job;
    }

    public void MarkDone(long id)
        => SetStatus(id, JobStatus.Done, null);

    public void MarkFailed(long id, string error)
        => SetStatus(id, JobStatus.Failed, error);

    /// <summary>Repasse en Pending les jobs restes Processing (ex. apres un crash du service).</summary>
    public int RequeueStale()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status='Pending', updated_at=$now WHERE status='Processing'";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    private void SetStatus(long id, JobStatus status, string? error)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status=$s, error=$e, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<TranscriptionJob> ListRecent(int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, audio_path, file_hash, output_dir, base_name, status, created_at, updated_at, error, attempts FROM jobs ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<TranscriptionJob>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TranscriptionJob
            {
                Id = r.GetInt64(0),
                AudioPath = r.GetString(1),
                FileHash = r.GetString(2),
                OutputDir = r.GetString(3),
                BaseName = r.GetString(4),
                Status = Enum.Parse<JobStatus>(r.GetString(5)),
                CreatedAt = DateTime.Parse(r.GetString(6)),
                UpdatedAt = DateTime.Parse(r.GetString(7)),
                Error = r.IsDBNull(8) ? null : r.GetString(8),
                Attempts = r.GetInt32(9),
            });
        }
        return list;
    }
}
