using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Serveur MCP stdio expose a Claude Desktop. Toute la journalisation part sur stderr
// (stdout est reserve au protocole MCP).
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Index de recherche partage, construit depuis la config puis rafraichi au demarrage.
builder.Services.AddSingleton(_ =>
{
    var config = ConfigStore.Load();
    var dataDir = ConfigStore.ExpandPath(config.DataDir);
    var index = new TranscriptIndex(Path.Combine(dataDir, "index.db"));
    index.Refresh(ConfigStore.ExpandPath(config.OutputRoot));
    return index;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
