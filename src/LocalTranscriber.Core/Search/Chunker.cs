namespace LocalTranscriber.Core.Search;

public sealed record TranscriptSegment(double Start, double End, string Text, string? SpeakerName);

public sealed record TranscriptChunk(int Index, double Start, double End, string Speaker, string Text);

/// <summary>
/// Regroupe les segments d'une transcription en fragments de taille homogene
/// (fenetre glissante par tours de parole, avec chevauchement) pour l'embedding.
/// </summary>
public static class Chunker
{
    public static List<TranscriptChunk> Chunk(
        IReadOnlyList<TranscriptSegment> segments,
        int maxChars = 800,
        int overlapSegments = 1)
    {
        var chunks = new List<TranscriptChunk>();
        var buffer = new List<TranscriptSegment>();
        var length = 0;

        void Flush()
        {
            if (buffer.Count == 0) return;
            var text = string.Join(" ", buffer.Select(s => s.Text.Trim())).Trim();
            if (text.Length == 0) { buffer.Clear(); length = 0; return; }
            var speaker = buffer
                .Where(s => !string.IsNullOrWhiteSpace(s.SpeakerName))
                .GroupBy(s => s.SpeakerName!)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "Inconnu";
            chunks.Add(new TranscriptChunk(chunks.Count, buffer[0].Start, buffer[^1].End, speaker, text));
        }

        foreach (var seg in segments)
        {
            var segLen = seg.Text?.Length ?? 0;
            if (length > 0 && length + segLen > maxChars)
            {
                Flush();
                // Chevauchement : on repart avec les derniers segments du buffer precedent.
                var overlap = buffer.Skip(Math.Max(0, buffer.Count - overlapSegments)).ToList();
                buffer = new List<TranscriptSegment>(overlap);
                length = overlap.Sum(s => s.Text?.Length ?? 0);
            }
            buffer.Add(seg);
            length += segLen;
        }
        Flush();
        return chunks;
    }
}
