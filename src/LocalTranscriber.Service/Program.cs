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
app.MapMcp("/mcp");
app.Run();
