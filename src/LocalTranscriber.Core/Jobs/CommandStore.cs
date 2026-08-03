using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Core.Jobs;

public static class CommandTypes
{
    public const string ReprocessFile = "reprocess_file"; // payload = chemin audio
    public const string ReprocessProject = "reprocess_project"; // payload = chemin relatif du projet
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
        using var tx = c.BeginTransaction();

        var list = new List<TranscriptionCommand>();
        using (var sel = c.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id, type, payload, created_at FROM commands ORDER BY id";
            using var r = sel.ExecuteReader();
            while (r.Read())
                list.Add(
                    new TranscriptionCommand(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        DateTime.Parse(r.GetString(3))
                    )
                );
        }

        if (list.Count > 0)
        {
            using var del = c.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM commands";
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return list;
    }
}
