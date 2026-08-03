using System.ComponentModel;
using System.Text.Json;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Search;
using LocalTranscriber.Mcp.Security;
using ModelContextProtocol.Server;

namespace LocalTranscriber.Mcp.Tools;

/// <summary>Outils MCP (lecture seule) exposes a Claude Desktop.</summary>
[McpServerToolType]
public sealed class TranscriptTools
{
    private readonly TranscriptIndex _index;
    private readonly HybridSearch _search;
    private readonly PathGuard _guard;

    public TranscriptTools(TranscriptIndex index, HybridSearch search, OutputLocation output)
    {
        _index = index;
        _search = search;
        _guard = new PathGuard(output.OutputRoot);
    }

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    [McpServerTool(Name = "list_projects")]
    [Description("Liste les projets pour lesquels des transcriptions existent.")]
    public string ListProjects() => Json(_index.ListProjects());

    [McpServerTool(Name = "list_transcripts")]
    [Description("Liste les transcriptions disponibles, optionnellement filtrees par projet.")]
    public string ListTranscripts(
        [Description("Nom du projet (optionnel).")] string? project = null,
        [Description("Nombre maximum de resultats (defaut 100).")] int limit = 100
    ) =>
        Json(
            _index
                .ListDocuments(project, limit)
                .Select(d => new
                {
                    d.Project,
                    d.BaseName,
                    d.Language,
                    d.DurationSeconds,
                    d.Speakers,
                    d.TranscribedAt,
                    d.Path,
                })
        );

    [McpServerTool(Name = "get_speakers")]
    [Description("Liste les locuteurs identifies, optionnellement pour un projet donne.")]
    public string GetSpeakers([Description("Nom du projet (optionnel).")] string? project = null) =>
        Json(_index.ListSpeakers(project));

    [McpServerTool(Name = "search_transcripts")]
    [Description(
        "Recherche dans les transcriptions. mode = hybrid (defaut, semantique + mots-cles), semantic, ou keyword. Filtres optionnels par projet et locuteur."
    )]
    public async Task<string> SearchTranscripts(
        [Description("Question ou termes a rechercher.")] string query,
        [Description("hybrid | semantic | keyword (defaut hybrid).")] string mode = "hybrid",
        [Description("Nom du projet (optionnel).")] string? project = null,
        [Description("Nom de locuteur a filtrer (optionnel).")] string? speaker = null,
        [Description("Nombre maximum de resultats (defaut 20).")] int limit = 20,
        CancellationToken ct = default
    )
    {
        var parsed = mode?.ToLowerInvariant() switch
        {
            "semantic" => SearchMode.Semantic,
            "keyword" => SearchMode.Keyword,
            _ => SearchMode.Hybrid,
        };
        var results = await _search.SearchAsync(query, parsed, project, speaker, limit, ct);
        return Json(
            results.Select(r => new
            {
                r.Project,
                r.BaseName,
                r.Speakers,
                r.Snippet,
                r.Score,
                r.Mode,
                r.Path,
            })
        );
    }

    [McpServerTool(Name = "get_transcript")]
    [Description(
        "Retourne une transcription par tours de parole, paginee. Filtre optionnel par locuteur. Utilise next_offset pour la page suivante."
    )]
    public string GetTranscript(
        [Description("Chemin (.json ou .md) retourne par les autres outils.")] string path,
        [Description("Ne renvoyer que les tours de ce locuteur (optionnel).")]
            string? speaker = null,
        [Description("Index de depart (defaut 0).")] int offset = 0,
        [Description("Nombre de tours par page (defaut 50).")] int limit = 50
    )
    {
        var jsonPath = Path.ChangeExtension(path, ".json");
        var resolved = _guard.Resolve(jsonPath);
        if (resolved is null || !File.Exists(resolved))
            return Json(
                new { error = "Transcription introuvable ou hors du dossier de sortie.", path }
            );

        var turns = ToTurns(TranscriptReader.ReadSegments(resolved));
        if (!string.IsNullOrWhiteSpace(speaker))
            turns = turns
                .Where(t => string.Equals(t.Speaker, speaker, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var page = turns.Skip(Math.Max(0, offset)).Take(Math.Max(1, limit)).ToList();
        int? next = offset + page.Count < turns.Count ? offset + page.Count : null;

        return Json(
            new
            {
                path = resolved,
                total_turns = turns.Count,
                offset,
                next_offset = next,
                turns = page.Select(t => new
                {
                    t.Speaker,
                    start = Math.Round(t.Start, 2),
                    t.Text,
                }),
            }
        );
    }

    private sealed record Turn(string Speaker, double Start, string Text);

    private static List<Turn> ToTurns(IReadOnlyList<TranscriptSegment> segments)
    {
        var turns = new List<Turn>();
        string? current = null;
        var buffer = new List<string>();
        var start = 0.0;

        void Flush()
        {
            if (current != null && buffer.Count > 0)
                turns.Add(new Turn(current, start, string.Join(" ", buffer).Trim()));
        }

        foreach (var s in segments)
        {
            var spk = s.SpeakerName ?? "Inconnu";
            if (spk != current)
            {
                Flush();
                current = spk;
                buffer = new List<string>();
                start = s.Start;
            }
            buffer.Add(s.Text.Trim());
        }
        Flush();
        return turns;
    }
}
