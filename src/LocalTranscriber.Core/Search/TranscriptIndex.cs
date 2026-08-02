using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalTranscriber.Core.Search;

public sealed record TranscriptHit(
    string Path,
    string Project,
    string BaseName,
    string Language,
    string Speakers,
    string Snippet);

public sealed record TranscriptDoc(
    string Path,
    string Project,
    string BaseName,
    string Language,
    double DurationSeconds,
    string Speakers,
    string TranscribedAt);

/// <summary>
/// Index de recherche plein-texte (SQLite FTS5) construit a partir des fichiers .json
/// produits par le moteur. Consomme par le serveur MCP pour repondre a Claude Desktop.
/// </summary>
public sealed class TranscriptIndex
{
    private readonly string _connectionString;

    public TranscriptIndex(string dbPath)
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
            CREATE TABLE IF NOT EXISTS documents (
                path         TEXT PRIMARY KEY,
                project      TEXT,
                base_name    TEXT,
                language     TEXT,
                duration     REAL,
                speakers     TEXT,
                transcribed_at TEXT,
                mtime        TEXT
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS transcripts_fts USING fts5(
                content, path UNINDEXED, project UNINDEXED, speakers UNINDEXED, tokenize='unicode61'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Scanne le dossier de sortie et (re)indexe les .json nouveaux ou modifies.</summary>
    public int Refresh(string outputRoot)
    {
        if (!Directory.Exists(outputRoot)) return 0;
        var indexed = 0;
        using var c = Open();

        foreach (var jsonPath in Directory.EnumerateFiles(outputRoot, "*.json", SearchOption.AllDirectories))
        {
            var mtime = File.GetLastWriteTimeUtc(jsonPath).ToString("o");
            if (IsUpToDate(c, jsonPath, mtime)) continue;

            try
            {
                var (project, baseName, language, duration, speakers, transcribedAt, content) =
                    Parse(outputRoot, jsonPath);
                Upsert(c, jsonPath, project, baseName, language, duration, speakers, transcribedAt, mtime, content);
                indexed++;
            }
            catch
            {
                // fichier partiel / non conforme : on ignore, il sera repris au prochain refresh
            }
        }
        return indexed;
    }

    private static bool IsUpToDate(SqliteConnection c, string path, string mtime)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT mtime FROM documents WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteScalar() is string existing && existing == mtime;
    }

    private static (string, string, string, double, string, string, string) Parse(string outputRoot, string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        var meta = root.GetProperty("metadata");

        string language = meta.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
        double duration = meta.TryGetProperty("duration_seconds", out var d) && d.TryGetDouble(out var dv) ? dv : 0;
        string transcribedAt = meta.TryGetProperty("transcribed_at", out var t) ? t.GetString() ?? "" : "";

        var speakerNames = new List<string>();
        if (meta.TryGetProperty("speakers", out var sp) && sp.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sp.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var label = s.TryGetProperty("label", out var lb) ? lb.GetString() : null;
                speakerNames.Add(name ?? label ?? "");
            }
        }

        var sb = new StringBuilder();
        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segs.EnumerateArray())
            {
                var text = seg.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
            }
        }

        var relDir = Path.GetDirectoryName(Path.GetRelativePath(outputRoot, jsonPath)) ?? "";
        var project = relDir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(racine)";
        var baseName = Path.GetFileNameWithoutExtension(jsonPath);

        return (project, baseName, language, duration, string.Join(", ", speakerNames), transcribedAt, sb.ToString());
    }

    private static void Upsert(SqliteConnection c, string path, string project, string baseName,
        string language, double duration, string speakers, string transcribedAt, string mtime, string content)
    {
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM transcripts_fts WHERE path=$p; DELETE FROM documents WHERE path=$p;";
            del.Parameters.AddWithValue("$p", path);
            del.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO documents (path, project, base_name, language, duration, speakers, transcribed_at, mtime)
                VALUES ($p, $proj, $b, $lang, $dur, $spk, $ta, $mt);
                INSERT INTO transcripts_fts (content, path, project, speakers) VALUES ($c, $p, $proj, $spk);
                """;
            ins.Parameters.AddWithValue("$p", path);
            ins.Parameters.AddWithValue("$proj", project);
            ins.Parameters.AddWithValue("$b", baseName);
            ins.Parameters.AddWithValue("$lang", language);
            ins.Parameters.AddWithValue("$dur", duration);
            ins.Parameters.AddWithValue("$spk", speakers);
            ins.Parameters.AddWithValue("$ta", transcribedAt);
            ins.Parameters.AddWithValue("$mt", mtime);
            ins.Parameters.AddWithValue("$c", content);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<TranscriptHit> Search(string query, string? project = null, string? speaker = null, int limit = 20)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT f.path, f.project, d.base_name, d.language, f.speakers,
                   snippet(transcripts_fts, 0, '[', ']', ' … ', 12) AS snip
            FROM transcripts_fts f
            JOIN documents d ON d.path = f.path
            WHERE transcripts_fts MATCH $q
              AND ($proj IS NULL OR f.project = $proj)
              AND ($spk IS NULL OR f.speakers LIKE '%' || $spk || '%')
            ORDER BY rank
            LIMIT $l;
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$proj", (object?)project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$spk", (object?)speaker ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", limit);

        var hits = new List<TranscriptHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            hits.Add(new TranscriptHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
        return hits;
    }

    public IReadOnlyList<string> ListProjects()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT project FROM documents ORDER BY project";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public IReadOnlyList<TranscriptDoc> ListDocuments(string? project = null, int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT path, project, base_name, language, duration, speakers, transcribed_at
            FROM documents
            WHERE ($proj IS NULL OR project = $proj)
            ORDER BY transcribed_at DESC
            LIMIT $l;
            """;
        cmd.Parameters.AddWithValue("$proj", (object?)project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<TranscriptDoc>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TranscriptDoc(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetString(5), r.GetString(6)));
        return list;
    }
}
