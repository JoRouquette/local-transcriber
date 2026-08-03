namespace LocalTranscriber.Core.Contracts;

/// <summary>
/// Requete envoyee au moteur Python gele (transcriber-engine.exe) pour un fichier audio.
/// Serialisee en snake_case et passee via un fichier temporaire (argument --request).
/// Le jeton Hugging Face n'est PAS inclus ici : il transite par la variable
/// d'environnement HF_TOKEN pour ne jamais atterrir dans un fichier temporaire.
/// </summary>
public sealed class EngineRequest
{
    public string AudioPath { get; set; } = "";

    /// <summary>Dossier de sortie deja calcule (miroir du dossier projet).</summary>
    public string OutputDir { get; set; } = "";

    /// <summary>Nom de base des fichiers de sortie (sans extension).</summary>
    public string BaseName { get; set; } = "";

    /// <summary>"auto" pour auto-detection, ou un code ISO ("fr", "en", ...).</summary>
    public string Language { get; set; } = "auto";

    public string ModelSize { get; set; } = "large-v3";

    /// <summary>"auto" | "cuda" | "cpu".</summary>
    public string Device { get; set; } = "auto";

    /// <summary>"auto" | "float16" | "int8" | "int8_float16" | "float32".</summary>
    public string ComputeType { get; set; } = "auto";

    public int BatchSize { get; set; } = 16;

    public bool DiarizationEnabled { get; set; } = true;
    public int? MinSpeakers { get; set; }
    public int? MaxSpeakers { get; set; }

    public bool SpeakerIdEnabled { get; set; }
    public string? VoicesDir { get; set; }
    public double SpeakerIdThreshold { get; set; } = 0.55;

    public bool OutputMarkdown { get; set; } = true;
    public bool OutputJson { get; set; } = true;
    public bool OutputSrt { get; set; } = true;
    public bool OutputText { get; set; } = true;

    public string ModelCacheDir { get; set; } = "";

    // Découpe par silence des fichiers longs (transcription par chunks).
    public bool ChunkingEnabled { get; set; }
    public int ChunkThresholdMinutes { get; set; } = 20;
    public int ChunkMinutes { get; set; } = 10;
    public double ChunkMinSilenceSeconds { get; set; } = 0.5;
}
