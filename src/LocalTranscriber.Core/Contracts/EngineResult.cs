using System.Collections.Generic;

namespace LocalTranscriber.Core.Contracts;

/// <summary>Un locuteur identifie dans la transcription.</summary>
public sealed class SpeakerInfo
{
    /// <summary>Label brut de diarisation, ex. "SPEAKER_00".</summary>
    public string Label { get; set; } = "";

    /// <summary>Nom resolu via les snippets de voix, si l'identification est active.</summary>
    public string? Name { get; set; }

    /// <summary>Score de similarite cosinus avec le snippet retenu (0..1), si applicable.</summary>
    public double? Confidence { get; set; }
}

/// <summary>
/// Resultat renvoye par le moteur Python sur stdout (JSON une ligne), en fin de traitement.
/// </summary>
public sealed class EngineResult
{
    /// <summary>"ok" ou "error".</summary>
    public string Status { get; set; } = "error";

    public string AudioPath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string? Language { get; set; }
    public int SpeakerCount { get; set; }
    public int SegmentCount { get; set; }

    public List<SpeakerInfo> Speakers { get; set; } = new();

    public string? MarkdownPath { get; set; }
    public string? JsonPath { get; set; }
    public string? SrtPath { get; set; }
    public string? TextPath { get; set; }

    public string? EngineVersion { get; set; }
    public string? Error { get; set; }

    public bool IsSuccess => string.Equals(Status, "ok", System.StringComparison.OrdinalIgnoreCase);
}
