using LocalTranscriber.Core.Embedding;

namespace LocalTranscriber.Core.Search;

public enum SearchMode
{
    Hybrid,
    Semantic,
    Keyword,
}

public sealed record SearchResult(
    string Path,
    string Project,
    string BaseName,
    string Speakers,
    string Snippet,
    double Score,
    string Mode
);

/// <summary>
/// Recherche hybride : fusionne la recherche plein-texte (FTS5) et la recherche
/// semantique (vecteurs e5) par Reciprocal Rank Fusion. La semantique est le moteur
/// principal, le plein-texte rattrape les termes exacts (noms propres, CIP...).
/// </summary>
public sealed class HybridSearch
{
    private const int RrfK = 60;

    private readonly TranscriptIndex _index;
    private readonly VectorStore _vectors;
    private readonly EmbeddingClient _embedder;

    public HybridSearch(TranscriptIndex index, VectorStore vectors, EmbeddingClient embedder)
    {
        _index = index;
        _vectors = vectors;
        _embedder = embedder;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        SearchMode mode,
        string? project = null,
        string? speaker = null,
        int limit = 20,
        CancellationToken ct = default
    )
    {
        return mode switch
        {
            SearchMode.Keyword => Keyword(query, project, speaker, limit),
            SearchMode.Semantic => await Semantic(query, project, speaker, limit, ct),
            _ => await Hybrid(query, project, speaker, limit, ct),
        };
    }

    private List<SearchResult> Keyword(string query, string? project, string? speaker, int limit) =>
        _index
            .Search(query, project, speaker, limit)
            .Select(h => new SearchResult(
                h.Path,
                h.Project,
                h.BaseName,
                h.Speakers,
                h.Snippet,
                0,
                "keyword"
            ))
            .ToList();

    private async Task<List<SearchResult>> Semantic(
        string query,
        string? project,
        string? speaker,
        int limit,
        CancellationToken ct
    )
    {
        var vec = await _embedder.EmbedOneAsync(query, "query", ct);
        if (vec is null)
            return new();
        // On collapse au meilleur fragment par document.
        return _vectors
            .Search(vec, project, speaker, limit * 4)
            .GroupBy(h => h.Path)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .Select(h => new SearchResult(
                h.Path,
                h.Project,
                h.BaseName,
                h.Speaker,
                Trim(h.Text),
                h.Score,
                "semantic"
            ))
            .ToList();
    }

    private async Task<List<SearchResult>> Hybrid(
        string query,
        string? project,
        string? speaker,
        int limit,
        CancellationToken ct
    )
    {
        var keyword = _index.Search(query, project, speaker, limit * 4);
        var vec = await _embedder.EmbedOneAsync(query, "query", ct);
        var semantic = vec is null
            ? new List<VectorHit>()
            : _vectors
                .Search(vec, project, speaker, limit * 4)
                .GroupBy(h => h.Path)
                .Select(g => g.OrderByDescending(h => h.Score).First())
                .ToList();

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var meta = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < keyword.Count; i++)
        {
            var h = keyword[i];
            scores[h.Path] = scores.GetValueOrDefault(h.Path) + 1.0 / (RrfK + i + 1);
            meta.TryAdd(
                h.Path,
                new SearchResult(h.Path, h.Project, h.BaseName, h.Speakers, h.Snippet, 0, "hybrid")
            );
        }
        for (var i = 0; i < semantic.Count; i++)
        {
            var h = semantic[i];
            scores[h.Path] = scores.GetValueOrDefault(h.Path) + 1.0 / (RrfK + i + 1);
            // Le fragment semantique fait un meilleur extrait : il prime sur le snippet FTS.
            meta[h.Path] = new SearchResult(
                h.Path,
                h.Project,
                h.BaseName,
                h.Speaker,
                Trim(h.Text),
                0,
                "hybrid"
            );
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => meta[kv.Key] with { Score = Math.Round(kv.Value, 5) })
            .ToList();
    }

    private static string Trim(string text, int max = 320) =>
        text.Length <= max ? text : text[..max].TrimEnd() + " …";
}
