using LocalTranscriber.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Permet de tourner comme vrai service Windows (demarrage avant login, auto-restart).
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LocalTranscriber";
});

builder.Services.AddHostedService<Worker>();

builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "LocalTranscriber";
});

var host = builder.Build();
host.Run();
