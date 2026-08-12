using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Core.Jobs;

/// <summary>Instantané de la file d'attente pour le monitoring de l'interface.</summary>
public sealed record JobSummary(
    int Pending,
    int Processing,
    int Done,
    int Failed,
    string? CurrentFile,
    string? LastError,
    string? LastErrorFile,
    DateTime? CurrentStartedAt
);

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

        // Migrations additives (bases creees par des versions anterieures) : SQLite n'a pas de
        // "ADD COLUMN IF NOT EXISTS", on ignore l'erreur "duplicate column".
        AddColumnIfMissing(c, "started_at", "TEXT");
        AddColumnIfMissing(c, "finished_at", "TEXT");
    }

    private static void AddColumnIfMissing(SqliteConnection c, string column, string type)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"ALTER TABLE jobs ADD COLUMN {column} {type}";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        { /* colonne deja presente */
        }
    }

    public bool AlreadyKnown(string fileHash)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Failed est inclus : un fichier deja tente et echoue ne doit pas etre re-enfile en boucle
        // par le scan. Le retraitement manuel (bouton Retraiter) passe par un autre chemin.
        cmd.CommandText =
            "SELECT COUNT(1) FROM jobs WHERE file_hash=$h AND status IN ('Pending','Processing','Done','Failed')";
        cmd.Parameters.AddWithValue("$h", fileHash);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Ajoute un job s'il n'existe pas deja (INSERT OR IGNORE sur le hash) et retourne son id.
    /// Le SELECT final renvoie toujours l'id, qu'il s'agisse d'une insertion ou d'un doublon
    /// ignore : la valeur n'est donc jamais null en pratique.
    /// </summary>
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
        // IMMEDIATE : SELECT + UPDATE se font sous un verrou d'ecriture pris d'emblee, sans
        // promotion read->write — deux workers ne peuvent pas prendre le meme job en concurrence.
        using var tx = c.BeginTransaction(deferred: false);
        using var sel = c.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText =
            "SELECT id, audio_path, file_hash, output_dir, base_name, attempts FROM jobs WHERE status='Pending' ORDER BY id LIMIT 1";
        long id;
        TranscriptionJob job;
        using (var r = sel.ExecuteReader())
        {
            if (!r.Read())
                return null;
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

        var now = DateTime.UtcNow.ToString("o");
        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText =
            "UPDATE jobs SET status='Processing', attempts=attempts+1, updated_at=$now, started_at=$now, finished_at=NULL WHERE id=$id";
        upd.Parameters.AddWithValue("$now", now);
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();
        tx.Commit();
        job.StartedAt = DateTime.Parse(
            now,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind
        );
        return job;
    }

    public void MarkDone(long id) => SetStatus(id, JobStatus.Done, null);

    public void MarkFailed(long id, string error) => SetStatus(id, JobStatus.Failed, error);

    /// <summary>
    /// Retry auto configurable : repasse en Pending les jobs Failed dont le nombre de tentatives
    /// reste strictement sous <paramref name="maxAttempts"/> (attempts est incremente a chaque
    /// DequeueNext, donc maxAttempts = nombre total de tentatives autorisees). Retourne le nombre
    /// de jobs re-enfiles. Un maxAttempts &lt;= 0 ne retente rien.
    /// </summary>
    public int RequeueFailedForRetry(int maxAttempts)
    {
        if (maxAttempts <= 0)
            return 0;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE jobs SET status='Pending', updated_at=$now WHERE status='Failed' AND attempts < $max";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$max", maxAttempts);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Relance manuelle : repasse en Pending TOUS les jobs Failed et REMET leur compteur de
    /// tentatives a zero (repart proprement), sans tenir compte du plafond (contrairement a
    /// <see cref="RequeueFailedForRetry"/>). Efface l'erreur.
    /// </summary>
    public int RequeueAllFailed()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE jobs SET status='Pending', error=NULL, attempts=0, updated_at=$now WHERE status='Failed'";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Recupere les jobs restes « Processing » (ex. worker interrompu : kill watchdog, OOM, crash,
    /// redemarrage). GARDE ANTI-BOUCLE : seuls ceux dont le nombre de tentatives est encore SOUS
    /// <paramref name="maxAttempts"/> sont repasses en Pending ; au-dela, le job est marque Failed
    /// et abandonne — sinon un fichier qui interrompt le worker serait re-enfile a chaque
    /// redemarrage, indefiniment. Retourne le nombre de jobs effectivement re-enfiles.
    /// </summary>
    public int RequeueStale(int maxAttempts)
    {
        if (maxAttempts < 1)
            maxAttempts = 1;
        using var c = Open();
        var now = DateTime.UtcNow.ToString("o");

        // 1) Au-dela du plafond : on abandonne (Failed) plutot que de boucler.
        using (var fail = c.CreateCommand())
        {
            fail.CommandText =
                "UPDATE jobs SET status='Failed', error=$e, updated_at=$now, finished_at=$now WHERE status='Processing' AND attempts >= $max";
            fail.Parameters.AddWithValue(
                "$e",
                "Interrompu a repetition (redemarrages du worker) — abandonne."
            );
            fail.Parameters.AddWithValue("$now", now);
            fail.Parameters.AddWithValue("$max", maxAttempts);
            fail.ExecuteNonQuery();
        }

        // 2) Sous le plafond : nouvelle chance.
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE jobs SET status='Pending', updated_at=$now WHERE status='Processing' AND attempts < $max";
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$max", maxAttempts);
        return cmd.ExecuteNonQuery();
    }

    private void SetStatus(long id, JobStatus status, string? error)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE jobs SET status=$s, error=$e, updated_at=$now, finished_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Oublie le job d'un fichier (pour forcer son retraitement au prochain scan).</summary>
    public int DeleteByPath(string audioPath)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM jobs WHERE audio_path = $p";
        cmd.Parameters.AddWithValue("$p", audioPath);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Oublie tous les jobs dont le fichier est sous un dossier (retraitement d'un projet).</summary>
    public int DeleteUnderPath(string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var c = Open();
        using var cmd = c.CreateCommand();
        // ESCAPE '~' : les chemins Windows contiennent des '\', on ne peut pas l'utiliser.
        cmd.CommandText = "DELETE FROM jobs WHERE audio_path LIKE $pfx ESCAPE '~'";
        var escaped = prefix.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_");
        cmd.Parameters.AddWithValue("$pfx", escaped + "%");
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Résumé de la file pour le monitoring (compteurs + fichier en cours + dernière erreur).</summary>
    public JobSummary Summarize()
    {
        using var c = Open();
        var counts = new Dictionary<string, int>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT status, COUNT(*) FROM jobs GROUP BY status";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                counts[r.GetString(0)] = r.GetInt32(1);
        }
        string? current = null;
        DateTime? currentStarted = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "SELECT audio_path, started_at FROM jobs WHERE status='Processing' ORDER BY updated_at DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                current = r.GetString(0);
                if (!r.IsDBNull(1))
                    currentStarted = DateTime.Parse(
                        r.GetString(1),
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind
                    );
            }
        }
        string? lastErr = null,
            lastErrFile = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "SELECT audio_path, error FROM jobs WHERE status='Failed' AND error IS NOT NULL ORDER BY updated_at DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                lastErrFile = r.GetString(0);
                lastErr = r.GetString(1);
            }
        }
        int Get(string s) => counts.TryGetValue(s, out var n) ? n : 0;
        return new JobSummary(
            Get("Pending"),
            Get("Processing"),
            Get("Done"),
            Get("Failed"),
            current,
            lastErr,
            lastErrFile,
            currentStarted
        );
    }

    public IReadOnlyList<TranscriptionJob> ListRecent(int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id, audio_path, file_hash, output_dir, base_name, status, created_at, updated_at, error, attempts, started_at, finished_at FROM jobs ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<TranscriptionJob>();
        using var r = cmd.ExecuteReader();
        DateTime? ParseUtc(int i) =>
            r.IsDBNull(i)
                ? null
                : DateTime.Parse(
                    r.GetString(i),
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind
                );
        while (r.Read())
        {
            list.Add(
                new TranscriptionJob
                {
                    Id = r.GetInt64(0),
                    AudioPath = r.GetString(1),
                    FileHash = r.GetString(2),
                    OutputDir = r.GetString(3),
                    BaseName = r.GetString(4),
                    Status = Enum.Parse<JobStatus>(r.GetString(5)),
                    CreatedAt = DateTime.Parse(
                        r.GetString(6),
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind
                    ),
                    UpdatedAt = DateTime.Parse(
                        r.GetString(7),
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind
                    ),
                    Error = r.IsDBNull(8) ? null : r.GetString(8),
                    Attempts = r.GetInt32(9),
                    StartedAt = ParseUtc(10),
                    FinishedAt = ParseUtc(11),
                }
            );
        }
        return list;
    }
}
