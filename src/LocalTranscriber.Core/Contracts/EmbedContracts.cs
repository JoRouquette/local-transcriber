namespace LocalTranscriber.Core.Contracts;

/// <summary>
/// Requete d'embedding envoyee au sidecar (JSON-lines sur TCP local).
/// <c>Kind</c> pilote le prefixe e5 : "query" -> "query: ", "passage" -> "passage: ".
/// </summary>
public sealed class EmbedRequest
{
    public List<string> Texts { get; set; } = new();

    /// <summary>"query" (question) ou "passage" (document a indexer).</summary>
    public string Kind { get; set; } = "passage";

    /// <summary>Jeton d'acces local (le worker et le sidecar partagent le meme). Facultatif : si
    /// le sidecar a ete demarre sans jeton, il ne le verifie pas.</summary>
    public string? Token { get; set; }
}

public sealed class EmbedResponse
{
    public List<float[]> Vectors { get; set; } = new();
    public int Dim { get; set; }
    public string? Model { get; set; }
    public string? Error { get; set; }

    public bool IsSuccess => Error is null && Vectors.Count > 0;
}
