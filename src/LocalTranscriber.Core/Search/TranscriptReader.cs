using System.Text.Json;

namespace LocalTranscriber.Core.Search;

/// <summary>Lecture des segments depuis un fichier de transcription .json (sortie du moteur).</summary>
public static class TranscriptReader
{
    public static List<TranscriptSegment> ReadSegments(string jsonPath)
    {
        var segments = new List<TranscriptSegment>();
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (
            !doc.RootElement.TryGetProperty("segments", out var segs)
            || segs.ValueKind != JsonValueKind.Array
        )
            return segments;

        foreach (var s in segs.EnumerateArray())
        {
            var text = s.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var start =
                s.TryGetProperty("start", out var st) && st.TryGetDouble(out var sv) ? sv : 0;
            var end = s.TryGetProperty("end", out var en) && en.TryGetDouble(out var ev) ? ev : 0;
            string? name =
                s.TryGetProperty("speaker_name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(name))
                name = s.TryGetProperty("speaker_label", out var lb) ? lb.GetString() : null;
            segments.Add(new TranscriptSegment(start, end, text, name));
        }
        return segments;
    }
}
