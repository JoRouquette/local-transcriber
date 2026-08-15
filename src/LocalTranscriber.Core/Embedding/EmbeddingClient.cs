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
    private readonly string? _token;

    public EmbeddingClient(
        int port,
        string? authToken = null,
        string host = "127.0.0.1",
        int timeoutMs = 30000
    )
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
        _token = authToken;
    }

    public async Task<EmbedResponse> EmbedAsync(
        IEnumerable<string> texts,
        string kind,
        CancellationToken ct = default
    )
    {
        var request = new EmbedRequest
        {
            Texts = texts.ToList(),
            Kind = kind,
            Token = _token,
        };
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
    public async Task<float[]?> EmbedOneAsync(
        string text,
        string kind,
        CancellationToken ct = default
    )
    {
        var resp = await EmbedAsync(new[] { text }, kind, ct);
        return resp.IsSuccess ? resp.Vectors[0] : null;
    }

    // Plafond de securite : une reponse d'embeddings normale fait quelques dizaines de Ko ; au-dela
    // de 64 Mo on considere que le sidecar est buggé/hostile et on coupe (evite une croissance
    // memoire non bornee pendant toute la fenetre de timeout si aucun '\n' n'arrive).
    private const int MaxResponseBytes = 64 * 1024 * 1024;

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        // On accumule les octets bruts et on ne decode qu'une seule fois, en UTF-8, une fois la
        // ligne complete : decoder chunk par chunk pourrait couper un caractere multi-octets a
        // cheval sur deux lectures. Le byte '\n' (0x0A) n'apparait jamais dans une sequence UTF-8.
        var bytes = new List<byte>();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            var nl = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (nl >= 0)
            {
                bytes.AddRange(buffer[..nl]);
                break;
            }
            bytes.AddRange(buffer[..read]);
            if (bytes.Count > MaxResponseBytes)
                throw new InvalidOperationException(
                    "Reponse du sidecar embeddings trop volumineuse (plafond depasse)."
                );
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
