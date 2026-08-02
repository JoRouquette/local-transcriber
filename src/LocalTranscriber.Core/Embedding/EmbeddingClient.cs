using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalTranscriber.Core.Contracts;

namespace LocalTranscriber.Core.Embedding;

/// <summary>
/// Client du sidecar d'embeddings (protocole JSON-lines sur TCP local).
/// Une connexion par appel : simple et robuste a l'echelle personnelle.
/// </summary>
public sealed class EmbeddingClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;

    public EmbeddingClient(int port, string host = "127.0.0.1", int timeoutMs = 30000)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public async Task<EmbedResponse> EmbedAsync(IEnumerable<string> texts, string kind, CancellationToken ct = default)
    {
        var request = new EmbedRequest { Texts = texts.ToList(), Kind = kind };
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);
            await client.ConnectAsync(_host, _port, cts.Token);

            await using var stream = client.GetStream();
            // JSON compact (une ligne) : le sidecar lit en JSON-lines.
            var payload = JsonSerializer.Serialize(request, JsonDefaults.Compact) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(bytes, cts.Token);
            await stream.FlushAsync(cts.Token);

            var line = await ReadLineAsync(stream, cts.Token);
            if (string.IsNullOrEmpty(line))
                return new EmbedResponse { Error = "Reponse vide du sidecar." };

            return JsonSerializer.Deserialize<EmbedResponse>(line, JsonDefaults.Options)
                   ?? new EmbedResponse { Error = "Reponse illisible du sidecar." };
        }
        catch (Exception ex)
        {
            return new EmbedResponse { Error = ex.Message };
        }
    }

    /// <summary>Embedding d'un seul texte (renvoie null en cas d'echec).</summary>
    public async Task<float[]?> EmbedOneAsync(string text, string kind, CancellationToken ct = default)
    {
        var resp = await EmbedAsync(new[] { text }, kind, ct);
        return resp.IsSuccess ? resp.Vectors[0] : null;
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            var chunk = Encoding.UTF8.GetString(buffer, 0, read);
            var nl = chunk.IndexOf('\n');
            if (nl >= 0)
            {
                sb.Append(chunk.AsSpan(0, nl));
                break;
            }
            sb.Append(chunk);
        }
        return sb.ToString();
    }
}
