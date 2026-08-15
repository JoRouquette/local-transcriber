using System.Threading;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Embedding;
using LocalTranscriber.Core.Search;
using LocalTranscriber.Core.Security;
using LocalTranscriber.Mcp.Resources;
using LocalTranscriber.Mcp.Tools;
using LocalTranscriber.Service;

// Instance unique machine-wide : un seul worker peut tourner a la fois. Deux workers = deux
// redacteurs de la meme base SQLite (index + jobs) + double bind du port MCP => corruption et
// erreurs de liaison. Le mutex Global\ couvre toutes les sessions (tache planifiee ET lancement
// manuel). On se fie uniquement a createdNew (pas de WaitOne) pour eviter tout blocage/abandon.
using var singleInstance = new Mutex(
    initiallyOwned: true,
    @"Global\LocalTranscriber.Worker.SingleInstance",
    out var isPrimary
);
if (!isPrimary)
{
    Console.Error.WriteLine(
        $"[worker] Doublon detecte (PID {Environment.ProcessId}) : une instance du worker tourne "
            + "deja. Arret immediat de ce processus."
    );
    return; // sortie 0 : la tache planifiee ne boucle pas en erreur.
}

Console.Error.WriteLine($"[worker] Instance unique acquise (PID {Environment.ProcessId}).");

// Hote ASP.NET Core : Kestrel (localhost) sert le MCP en HTTP, et le meme processus
// fait tourner le Worker (surveillance/transcription). Installable en service Windows.
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "LocalTranscriber");

if (OperatingSystem.IsWindows())
    builder.Logging.AddEventLog(settings => settings.SourceName = "LocalTranscriber");

var config = ConfigStore.Load();
var dataDir = ConfigStore.ExpandPath(config.DataDir);
try
{
    Directory.CreateDirectory(dataDir);
}
catch (Exception ex)
{
    // DataDir inaccessible (droits, chemin reseau indisponible au boot...) : on trace sur stderr
    // AVANT la construction de l'hote (aucun logger/EventLog encore dispo) et on sort proprement.
    Console.Error.WriteLine(
        $"[worker] Dossier de donnees inaccessible ({dataDir}) : {ex.Message}. Arret."
    );
    return;
}
var indexDb = Path.Combine(dataDir, "index.db");

// Un seul processus = un seul redacteur de l'index (FTS + vecteurs, meme base).
builder.Services.AddSingleton(new TranscriptIndex(indexDb));
builder.Services.AddSingleton(new VectorStore(indexDb));

// Jeton d'acces local partage : le worker demarre le sidecar avec ce meme jeton, donc le client
// doit le presenter a chaque requete (le sidecar refuse sinon).
builder.Services.AddSingleton(
    new EmbeddingClient(config.EmbeddingSidecarPort, AccessToken.GetOrCreate())
);
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

// Auth MCP optionnelle (opt-in) : en plus du loopback, on peut exiger un jeton d'acces local.
// Utile sur une machine multi-comptes (un autre utilisateur ne pourrait pas lire les
// transcriptions via 127.0.0.1). Desactive par defaut pour ne pas casser une config existante.
// Lu au demarrage (un changement de McpRequireToken impose un redemarrage du service).
var mcpRequireToken = config.McpRequireToken;
var mcpAccessToken = mcpRequireToken ? AccessToken.GetOrCreate() : null;

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

        // Jeton : accepte via ?token=... (URL, compatible client `url` et pont mcp-remote) ou
        // via l'en-tete Authorization: Bearer <jeton>. Comparaison a temps constant.
        if (mcpRequireToken)
        {
            var provided = context.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(provided))
            {
                var auth = context.Request.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    provided = auth["Bearer ".Length..].Trim();
            }
            if (!AccessToken.Matches(provided, mcpAccessToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next();
    }
);

app.MapMcp("/mcp");
app.Run();
