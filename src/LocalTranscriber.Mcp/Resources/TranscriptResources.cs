using System.ComponentModel;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Mcp.Security;
using ModelContextProtocol.Server;

namespace LocalTranscriber.Mcp.Resources;

/// <summary>
/// Expose les transcriptions comme ressources MCP adressables :
/// <c>transcript://{project}/{name}</c> -> contenu Markdown. Claude Desktop peut les
/// attacher directement. Toute lecture est confinee a OutputRoot par le garde-fou.
/// </summary>
[McpServerResourceType]
public sealed class TranscriptResources
{
    private readonly OutputLocation _output;
    private readonly PathGuard _guard;

    public TranscriptResources(OutputLocation output)
    {
        _output = output;
        _guard = new PathGuard(output.OutputRoot);
    }

    [McpServerResource(
        UriTemplate = "transcript://{project}/{name}",
        Name = "transcript",
        MimeType = "text/markdown"
    )]
    [Description("Transcription Markdown d'un fichier, adressee par projet et nom de base.")]
    public string Read(
        [Description("Nom du projet (dossier de premier niveau sous la racine de sortie).")]
            string project,
        [Description("Nom de base du fichier, sans extension.")] string name
    )
    {
        // Entree non fiable : on borne resolution de chemin et lecture pour renvoyer un
        // message propre plutot que laisser une exception remonter via le framework MCP.
        if (project is null || name is null)
            return "Transcription introuvable : projet ou nom manquant.";

        try
        {
            var md = Path.Combine(_output.OutputRoot, project, name + ".md");
            var resolved = _guard.Resolve(md);
            if (resolved is null || !File.Exists(resolved))
                return $"Transcription introuvable : transcript://{project}/{name}";
            return File.ReadAllText(resolved);
        }
        catch (Exception ex)
        {
            return $"Lecture impossible de transcript://{project}/{name} : {ex.Message}";
        }
    }
}
