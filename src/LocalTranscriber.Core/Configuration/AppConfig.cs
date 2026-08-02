namespace LocalTranscriber.Core.Configuration;

public sealed class EngineConfig
{
    public string ModelSize { get; set; } = "large-v3";
    public string Device { get; set; } = "auto";
    public string ComputeType { get; set; } = "auto";
    public int BatchSize { get; set; } = 16;
    public string Language { get; set; } = "auto";
}

public sealed class DiarizationConfig
{
    public bool Enabled { get; set; } = true;
    public int? MinSpeakers { get; set; }
    public int? MaxSpeakers { get; set; }
}

public sealed class SpeakerIdConfig
{
    public bool Enabled { get; set; }
    public double Threshold { get; set; } = 0.55;
    public string VoicesDirName { get; set; } = "voices";
}

public sealed class OutputConfig
{
    public bool Markdown { get; set; } = true;
    public bool Json { get; set; } = true;
    public bool Srt { get; set; } = true;
    public bool Text { get; set; } = true;
}

/// <summary>
/// Configuration d'un projet. Les sous-objets nuls heritent des reglages globaux
/// (voir <see cref="AppConfig.EffectiveFor"/>).
/// </summary>
public sealed class ProjectConfig
{
    public string Name { get; set; } = "";

    /// <summary>Chemin relatif au watchRoot (= dossier du projet a surveiller).</summary>
    public string RelativePath { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public EngineConfig? Engine { get; set; }
    public DiarizationConfig? Diarization { get; set; }
    public SpeakerIdConfig? SpeakerIdentification { get; set; }
    public OutputConfig? Outputs { get; set; }
}

/// <summary>Reglages effectifs pour un projet donne, apres fusion avec le global.</summary>
public sealed record EffectiveSettings(
    EngineConfig Engine,
    DiarizationConfig Diarization,
    SpeakerIdConfig SpeakerId,
    OutputConfig Outputs);

public sealed class AppConfig
{
    public string WatchRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public string ModelCacheDir { get; set; } = @"%LOCALAPPDATA%\LocalTranscriber\models";
    public string DataDir { get; set; } = @"%LOCALAPPDATA%\LocalTranscriber\data";
    public string EngineExecutable { get; set; } = @"engine\transcriber-engine.exe";

    public List<string> FileTypes { get; set; } = new()
    {
        ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aac"
    };

    public int StabilizationSeconds { get; set; } = 5;
    public int MaxParallelJobs { get; set; } = 1;

    // ---- Serveur MCP (HTTP local) et recherche semantique ----
    public int McpPort { get; set; } = 8765;
    public bool SemanticEnabled { get; set; } = true;
    public int EmbeddingSidecarPort { get; set; } = 8766;
    public string EmbeddingModel { get; set; } = "intfloat/multilingual-e5-small";
    public string EmbeddingDevice { get; set; } = "cpu";
    public int ChunkMaxChars { get; set; } = 800;
    public int ChunkOverlapSegments { get; set; } = 1;

    public EngineConfig Engine { get; set; } = new();
    public DiarizationConfig Diarization { get; set; } = new();
    public SpeakerIdConfig SpeakerIdentification { get; set; } = new();
    public OutputConfig Outputs { get; set; } = new();

    public List<ProjectConfig> Projects { get; set; } = new();

    /// <summary>Fusionne les reglages globaux avec les surcharges d'un projet.</summary>
    public EffectiveSettings EffectiveFor(ProjectConfig? project) => new(
        project?.Engine ?? Engine,
        project?.Diarization ?? Diarization,
        project?.SpeakerIdentification ?? SpeakerIdentification,
        project?.Outputs ?? Outputs);
}
