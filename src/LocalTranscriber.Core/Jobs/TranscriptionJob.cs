namespace LocalTranscriber.Core.Jobs;

public enum JobStatus
{
    Pending,
    Processing,
    Done,
    Failed,
}

public sealed class TranscriptionJob
{
    public long Id { get; set; }
    public string AudioPath { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string BaseName { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Debut du traitement (passage en Processing). Null tant qu'en attente.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Fin du traitement (Done ou Failed). Null tant que non termine.</summary>
    public DateTime? FinishedAt { get; set; }

    public string? Error { get; set; }
    public int Attempts { get; set; }

    /// <summary>
    /// Duree de traitement : reelle si termine, ecoulee (jusqu'a maintenant) si en cours,
    /// null sinon. Affichee dans le monitoring.
    /// </summary>
    public TimeSpan? Duration =>
        StartedAt is null ? null
        : Status == JobStatus.Processing ? DateTime.UtcNow - StartedAt.Value
        : FinishedAt is null ? null
        : FinishedAt.Value - StartedAt.Value;

    /// <summary>Duree formatee courte (m:ss ou h:mm:ss), ou vide.</summary>
    public string DurationText =>
        Duration is { } d
            ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
            : "";
}
