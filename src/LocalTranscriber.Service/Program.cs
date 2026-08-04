using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Embedding;
using LocalTranscriber.Core.Search;
using LocalTranscriber.Mcp.Resources;
using LocalTranscriber.Mcp.Tools;
using LocalTranscriber.Service;

// Hote ASP.NET Core : Kestrel (localhost) sert le MCP en HTTP, et le meme processus
// fait tourner le Worker (surveillance/transcription). Installable en service Windows.
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "LocalTranscriber");

if (OperatingSystem.IsWindows())
    builder.Logging.AddEventLog(settings => settings.SourceName = "LocalTranscriber");

var config = ConfigStore.Load();
var dataDir = ConfigStore.ExpandPath(config.DataDir);
Directory.CreateDirectory(dataDir);
var indexDb = Path.Combine(dataDir, "index.db");

// Un seul processus = un seul redacteur de l'index (FTS + vecteurs, meme base).
builder.Services.AddSingleton(new TranscriptIndex(indexDb));
builder.Services.AddSingleton(new VectorStore(indexDb));
builder.Services.AddSingleton(new EmbeddingClient(config.EmbeddingSidecarPort));
builder.Services.AddSingleton(sp => new HybridSearch(
    sp.GetRequiredService<TranscriptIndex>(),
    sp.GetRequiredService<VectorStore>(),
    sp.GetRequiredService<EmbeddingClient>()
));
builder.Services.AddSingleton(new OutputLocation(ConfigStore.ExpandPath(config.OutputRoot)));

builder.Services.AddHostedService<Worker>();

builder
    .Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(TranscriptTools).Assembly)
    .WithResourcesFromAssembly(typeof(TranscriptResources).Assembly);

builder.WebHost.UseUrls($"http://127.0.0.1:{config.McpPort}");

var app = builder.Build();

// M1 — Protection anti-DNS-rebinding du transport MCP HTTP. Kestrel n'ecoute que sur la
// boucle locale (127.0.0.1), mais une page web malveillante ouverte dans un navigateur
// pourrait tenter d'atteindre le serveur via un nom DNS qui resout vers 127.0.0.1
// (rebinding). On rejette donc (403) toute requete dont l'en-tete Host ou Origin ne pointe
// pas vers un hote loopback autorise. L'en-tete Origin absent est tolere : les clients MCP
// non navigateur (Claude Desktop) n'en envoient pas ; seul un navigateur en poserait un,
// cas ou la verification prend tout son sens. On ne casse ainsi aucun client MCP local.
static bool IsLoopbackHost(string? host)
{
    if (string.IsNullOrEmpty(host))
        return false;
    // Retire les crochets IPv6 eventuels ([::1] -> ::1).
    host = host.Trim('[', ']');
    return host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}

app.Use(
    async (context, next) =>
    {
        // Host : l'hote (sans le port) doit figurer dans l'allow-list loopback.
        if (!IsLoopbackHost(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Origin : s'il est present (client navigateur), son hote doit etre loopback ;
        // absent, on laisse passer (clients MCP non navigateur legitimes).
        var origin = context.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            if (
                !Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                || !IsLoopbackHost(originUri.Host)
            )
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next();
    }
);

app.MapMcp("/mcp");
app.Run();
