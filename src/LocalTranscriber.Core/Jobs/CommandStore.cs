using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Core.Jobs;

public static class CommandTypes
{
    public const string ReprocessFile = "reprocess_file"; // payload = chemin audio
    public const string ReprocessProject = "reprocess_project"; // payload = chemin relatif du projet
    public const string RetryFailed = "retry_failed"; // relance TOUS les jobs en echec (sans payload)
    public const string RequeueStale = "requeue_stale"; // debloque les jobs figes en Processing (sans payload)
}

public sealed record TranscriptionCommand(long Id, string Type, string Payload, DateTime CreatedAt);

/// <summary>
/// Canal de commandes entre la GUI et le service (petite table SQLite). La GUI empile
/// des ordres (retraiter un fichier / un projet), le service les draine et agit.
/// </summary>
public sealed class CommandStore
{
    private readonly string _connectionString;

    public CommandStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS commands (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                type       TEXT NOT NULL,
                payload    TEXT,
                created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Enqueue(string type, string payload)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO commands (type, payload, created_at) VALUES ($t, $p, $now)";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Lit toutes les commandes en attente et les retire (consomme).</summary>
    public IReadOnlyList<TranscriptionCommand> Drain()
    {
        using var c = Open();
        // IMMEDIATE : la transaction est ouverte en ecriture d'emblee (pas de promotion
        // read->write) — evite qu'une commande inseree entre le SELECT et le DELETE soit perdue.
        using var tx = c.BeginTransaction(deferred: false);

        var list = new List<TranscriptionCommand>();
        long maxId = 0;
        using (var sel = c.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id, type, payload, created_at FROM commands ORDER BY id";
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                if (id > maxId)
                    maxId = id;
                list.Add(
                    new TranscriptionCommand(
                        id,
                        r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        DateTime.Parse(
                            r.GetString(3),
                            null,
                            System.Globalization.DateTimeStyles.RoundtripKind
                        )
                    )
                );
            }
        }

        if (list.Count > 0)
        {
            // Borne la suppression aux lignes reellement lues (id <= maxId) : une commande
            // inseree apres le SELECT ne sera pas consommee a tort au prochain Drain.
            using var del = c.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM commands WHERE id <= $maxId";
            del.Parameters.AddWithValue("$maxId", maxId);
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return list;
    }
}
