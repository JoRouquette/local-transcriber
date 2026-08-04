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
/// Découpe des fichiers longs avant transcription : au-delà du seuil, l'audio est coupé
/// aux silences en chunks (transcrits séparément puis fusionnés). L'alignement et la
/// diarisation restent réalisés sur le fichier entier.
/// </summary>
public sealed class ChunkingConfig
{
    public bool Enabled { get; set; } = true;
    public int ThresholdMinutes { get; set; } = 20;
    public int ChunkMinutes { get; set; } = 10;
    public double MinSilenceSeconds { get; set; } = 0.5;
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

/// <summary>Plage d'inactivite : le service ne lance aucune transcription pendant cette fenetre.</summary>
public sealed class QuietPeriod
{
    /// <summary>Jours concernes ("mon","tue","wed","thu","fri","sat","sun"). Vide = tous les jours.</summary>
    public List<string> Days { get; set; } = new();

    /// <summary>Heure de debut, format "HH:mm".</summary>
    public string Start { get; set; } = "22:00";

    /// <summary>Heure de fin, format "HH:mm". Si End &lt; Start, la plage passe minuit.</summary>
    public string End { get; set; } = "06:00";
}

/// <summary>Reglages effectifs pour un projet donne, apres fusion avec le global.</summary>
public sealed record EffectiveSettings(
    EngineConfig Engine,
    DiarizationConfig Diarization,
    SpeakerIdConfig SpeakerId,
    OutputConfig Outputs
);

public sealed class AppConfig
{
    public string WatchRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";

    // Emplacements machine-wide (%PROGRAMDATA%) : conserves pour la compatibilite avec les
    // installations existantes. Le worker ne tourne plus en service LocalSystem (session 0)
    // mais en tache planifiee dans la SESSION DE L'UTILISATEUR ; %PROGRAMDATA% reste toutefois
    // partageable et sans risque pour ces caches/donnees.
    public string ModelCacheDir { get; set; } = @"%PROGRAMDATA%\LocalTranscriber\models";
    public string DataDir { get; set; } = @"%PROGRAMDATA%\LocalTranscriber\data";
    public string EngineExecutable { get; set; } = @"engine\transcriber-engine.exe";

    /// <summary>
    /// Environnement Python du moteur (venv cree par uv au 1er lancement). Par defaut sous
    /// %LOCALAPPDATA% (profil utilisateur) : la GUI comme le worker tournent desormais sous
    /// le compte de l'utilisateur, donc un emplacement par-utilisateur evite le piege de
    /// propriete SYSTEM (dossier cree jadis par le service LocalSystem => acces refuse a la
    /// reinstallation). Voir <see cref="Engine.EngineSetup"/>.
    /// </summary>
    public string EngineEnvDir { get; set; } = @"%LOCALAPPDATA%\LocalTranscriberData\engine-env";

    /// <summary>Installer automatiquement les mises à jour (téléchargées au lancement, appliquées à la fermeture).</summary>
    public bool AutoInstallUpdates { get; set; } = true;

    /// <summary>
    /// Jeton Hugging Face pour la diarisation (pyannote). Stocke dans les parametres
    /// (jamais dans le depot). Si vide, on retombe sur la variable d'environnement
    /// HF_TOKEN ou un fichier .env a cote de l'executable.
    /// </summary>
    public string? HfToken { get; set; }

    public List<string> FileTypes { get; set; } =
        new() { ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wma", ".aac" };

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
    public ChunkingConfig Chunking { get; set; } = new();

    public List<ProjectConfig> Projects { get; set; } = new();

    /// <summary>Plages d'inactivite pendant lesquelles aucune transcription n'est lancee.</summary>
    public List<QuietPeriod> QuietHours { get; set; } = new();

    /// <summary>Fusionne les reglages globaux avec les surcharges d'un projet.</summary>
    public EffectiveSettings EffectiveFor(ProjectConfig? project) =>
        new(
            project?.Engine ?? Engine,
            project?.Diarization ?? Diarization,
            project?.SpeakerIdentification ?? SpeakerIdentification,
            project?.Outputs ?? Outputs
        );

    /// <summary>Indique si l'instant donne tombe dans une plage d'inactivite.</summary>
    public bool IsQuietNow(DateTime now)
    {
        var t = TimeOnly.FromDateTime(now);
        var today = DayKey(now.DayOfWeek);
        var yesterday = DayKey(now.AddDays(-1).DayOfWeek);

        foreach (var p in QuietHours)
        {
            if (!TimeOnly.TryParse(p.Start, out var s) || !TimeOnly.TryParse(p.End, out var e))
                continue;

            bool allDays = p.Days is null || p.Days.Count == 0;
            bool DayIn(string d) =>
                allDays
                || p.Days!.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase));

            if (s <= e)
            {
                if (DayIn(today) && t >= s && t < e)
                    return true;
            }
            else // passe minuit : [s,24h) le jour de debut, [0,e) le lendemain
            {
                if (DayIn(today) && t >= s)
                    return true;
                if (DayIn(yesterday) && t < e)
                    return true;
            }
        }
        return false;
    }

    private static string DayKey(DayOfWeek d) =>
        d switch
        {
            DayOfWeek.Monday => "mon",
            DayOfWeek.Tuesday => "tue",
            DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu",
            DayOfWeek.Friday => "fri",
            DayOfWeek.Saturday => "sat",
            _ => "sun",
        };
}
