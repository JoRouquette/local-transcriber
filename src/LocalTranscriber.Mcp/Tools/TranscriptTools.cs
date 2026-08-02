using System.ComponentModel;
using System.Text.Json;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Search;
using ModelContextProtocol.Server;

namespace LocalTranscriber.Mcp.Tools;

/// <summary>
/// Outils MCP exposes a Claude Desktop pour interroger les transcriptions locales.
/// </summary>
[McpServerToolType]
public sealed class TranscriptTools
{
    private readonly TranscriptIndex _index;

    public TranscriptTools(TranscriptIndex index) => _index = index;

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    [McpServerTool(Name = "list_projects")]
    [Description("Liste les projets pour lesquels des transcriptions existent.")]
    public string ListProjects()
    {
        RefreshFromConfig();
        return Json(_index.ListProjects());
    }

    [McpServerTool(Name = "list_transcripts")]
    [Description("Liste les transcriptions disponibles, optionnellement filtrees par projet.")]
    public string ListTranscripts(
        [Description("Nom du projet (optionnel).")] string? project = null,
        [Description("Nombre maximum de resultats (defaut 100).")] int limit = 100)
    {
        RefreshFromConfig();
        var docs = _index.ListDocuments(project, limit)
            .Select(d => new { d.Project, d.BaseName, d.Language, d.DurationSeconds, d.Speakers, d.TranscribedAt, d.Path });
        return Json(docs);
    }

    [McpServerTool(Name = "search_transcripts")]
    [Description("Recherche plein-texte dans les transcriptions. Filtres optionnels par projet et par locuteur.")]
    public string SearchTranscripts(
        [Description("Requete de recherche plein-texte (syntaxe FTS5 : mots, \"phrase exacte\", prefixe*).")] string query,
        [Description("Nom du projet (optionnel).")] string? project = null,
        [Description("Nom de locuteur a filtrer (optionnel).")] string? speaker = null,
        [Description("Nombre maximum de resultats (defaut 20).")] int limit = 20)
    {
        RefreshFromConfig();
        var hits = _index.Search(query, project, speaker, limit)
            .Select(h => new { h.Project, h.BaseName, h.Language, h.Speakers, h.Snippet, h.Path });
        return Json(hits);
    }

    [McpServerTool(Name = "get_transcript")]
    [Description("Retourne le contenu complet d'une transcription (Markdown de preference) a partir de son chemin.")]
    public string GetTranscript(
        [Description("Chemin du fichier de transcription (.json ou .md) retourne par les autres outils.")] string path)
    {
        var md = Path.ChangeExtension(path, ".md");
        if (File.Exists(md)) return File.ReadAllText(md);
        if (File.Exists(path)) return File.ReadAllText(path);
        return $"Transcription introuvable : {path}";
    }

    private void RefreshFromConfig()
    {
        try
        {
            var config = ConfigStore.Load();
            _index.Refresh(ConfigStore.ExpandPath(config.OutputRoot));
        }
        catch { /* l'index reste utilisable meme si le refresh echoue */ }
    }
}
