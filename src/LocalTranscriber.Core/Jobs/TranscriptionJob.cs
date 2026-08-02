namespace LocalTranscriber.Core.Jobs;

public enum JobStatus
{
    Pending,
    Processing,
    Done,
    Failed
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
    public string? Error { get; set; }
    public int Attempts { get; set; }
}
