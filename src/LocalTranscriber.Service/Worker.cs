using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Contracts;
using LocalTranscriber.Core.Embedding;
using LocalTranscriber.Core.Engine;
using LocalTranscriber.Core.Jobs;
using LocalTranscriber.Core.Paths;
using LocalTranscriber.Core.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalTranscriber.Service;

/// <summary>
/// Coeur du service : surveille le dossier racine, met les nouveaux audios en file,
/// les traite via le moteur, puis met a jour l'index FTS et les vecteurs semantiques.
/// Seul redacteur de l'index (le MCP, dans le meme processus, ne fait que lire).
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly TranscriptIndex _index;
    private readonly VectorStore _vectors;
    private readonly EmbeddingClient _embedder;
    private readonly SidecarManager _sidecar;

    private AppConfig _config = new();
    private DateTime _configMtime = DateTime.MinValue;
    private JobStore? _jobs;
    private PythonEngineRunner? _runner;
    private string? _hfToken;
    private string _enginePath = "";

    public Worker(ILogger<Worker> logger, TranscriptIndex index, VectorStore vectors, EmbeddingClient embedder)
    {
        _logger = logger;
        _index = index;
        _vectors = vectors;
        _embedder = embedder;
        _sidecar = new SidecarManager(logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LocalTranscriber demarre.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReloadConfigIfChanged();
                if (string.IsNullOrWhiteSpace(_config.WatchRoot) || string.IsNullOrWhiteSpace(_config.OutputRoot))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                if (_config.SemanticEnabled)
                    await _sidecar.EnsureStartedAsync(_enginePath, _config.EmbeddingSidecarPort,
                        ConfigStore.ExpandPath(_config.ModelCacheDir), _config.EmbeddingDevice, stoppingToken);

                ScanAndEnqueue();
                await ProcessQueueAsync(stoppingToken);

                var outputRoot = ConfigStore.ExpandPath(_config.OutputRoot);
                _index.Refresh(outputRoot);
                if (_config.SemanticEnabled)
                    await ReconcileVectorsAsync(outputRoot, 5, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur dans la boucle principale.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _config.StabilizationSeconds)), stoppingToken);
        }

        _sidecar.Dispose();
        _logger.LogInformation("LocalTranscriber arrete.");
    }

    private void ReloadConfigIfChanged()
    {
        var path = ConfigStore.DefaultConfigPath;
        if (!File.Exists(path)) return;
        var mtime = File.GetLastWriteTimeUtc(path);
        if (mtime == _configMtime && _jobs != null) return;

        _config = ConfigStore.Load(path);
        _configMtime = mtime;

        var dataDir = ConfigStore.ExpandPath(_config.DataDir);
        Directory.CreateDirectory(dataDir);
        _jobs = new JobStore(Path.Combine(dataDir, "jobs.db"));
        _jobs.RequeueStale();

        _enginePath = ResolveEnginePath(_config.EngineExecutable);
        _hfToken = LoadHfToken();
        _runner = new PythonEngineRunner(_enginePath, _hfToken, _logger);

        _logger.LogInformation("Configuration (re)chargee. Moteur : {Engine}", _enginePath);
    }

    private static string ResolveEnginePath(string configured)
    {
        var p = ConfigStore.ExpandPath(configured);
        return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
    }

    private string? LoadHfToken()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var envFile = Path.Combine(AppContext.BaseDirectory, ".env");
        if (File.Exists(envFile))
            foreach (var line in File.ReadAllLines(envFile))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("HF_TOKEN=", StringComparison.OrdinalIgnoreCase))
                    return trimmed["HF_TOKEN=".Length..].Trim().Trim('"');
            }
        return null;
    }

    private void ScanAndEnqueue()
    {
        if (_jobs is null) return;
        var watchRoot = ConfigStore.ExpandPath(_config.WatchRoot);
        var outputRoot = ConfigStore.ExpandPath(_config.OutputRoot);
        if (!Directory.Exists(watchRoot)) return;

        var extensions = new HashSet<string>(_config.FileTypes, StringComparer.OrdinalIgnoreCase);
        var voicesDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _config.SpeakerIdentification.VoicesDirName };
        foreach (var p in _config.Projects.Where(p => p.SpeakerIdentification != null))
            voicesDirNames.Add(p.SpeakerIdentification!.VoicesDirName);

        foreach (var file in Directory.EnumerateFiles(watchRoot, "*.*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if (file.Split(Path.DirectorySeparatorChar).Any(seg => voicesDirNames.Contains(seg))) continue;
            if (!IsStable(file)) continue;

            var project = PathResolver.FindProject(_config, file);
            if (project is { Enabled: false }) continue;

            try
            {
                var hash = FileHasher.QuickHash(file);
                if (_jobs.AlreadyKnown(hash)) continue;
                var outputDir = PathResolver.ResolveOutputDir(watchRoot, outputRoot, file);
                _jobs.Enqueue(file, hash, outputDir, PathResolver.BaseName(file));
                _logger.LogInformation("En file : {File}", file);
            }
            catch (IOException) { /* encore en ecriture */ }
        }
    }

    private bool IsStable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc > DateTime.UtcNow.AddSeconds(-_config.StabilizationSeconds)) return false;
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch { return false; }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        if (_jobs is null || _runner is null) return;

        TranscriptionJob? job;
        while (!ct.IsCancellationRequested && (job = _jobs.DequeueNext()) is not null)
        {
            _logger.LogInformation("Traitement : {File}", job.AudioPath);
            var request = BuildRequest(job);
            var result = await _runner.RunAsync(request, ct);

            if (result.IsSuccess)
            {
                _jobs.MarkDone(job.Id);
                _logger.LogInformation("OK : {File} ({Segments} segments, {Speakers} locuteurs)",
                    job.AudioPath, result.SegmentCount, result.SpeakerCount);

                if (_config.SemanticEnabled && result.JsonPath is { } jp && File.Exists(jp))
                    await VectorizeAsync(jp, ConfigStore.ExpandPath(_config.OutputRoot), ct);
            }
            else
            {
                _jobs.MarkFailed(job.Id, result.Error ?? "erreur inconnue");
                _logger.LogError("Echec : {File} — {Error}", job.AudioPath, result.Error);
            }
        }
    }

    private async Task ReconcileVectorsAsync(string outputRoot, int max, CancellationToken ct)
    {
        if (!Directory.Exists(outputRoot)) return;
        var done = 0;
        foreach (var json in Directory.EnumerateFiles(outputRoot, "*.json", SearchOption.AllDirectories))
        {
            if (done >= max || ct.IsCancellationRequested) break;
            var mtime = File.GetLastWriteTimeUtc(json).ToString("o");
            if (_vectors.IsUpToDate(json, mtime)) continue;
            if (await VectorizeAsync(json, outputRoot, ct)) done++;
        }
    }

    private async Task<bool> VectorizeAsync(string jsonPath, string outputRoot, CancellationToken ct)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(jsonPath).ToString("o");
            var segments = TranscriptReader.ReadSegments(jsonPath);
            var chunks = Chunker.Chunk(segments, _config.ChunkMaxChars, _config.ChunkOverlapSegments);
            if (chunks.Count == 0) return false;

            var resp = await _embedder.EmbedAsync(chunks.Select(c => c.Text), "passage", ct);
            if (!resp.IsSuccess || resp.Vectors.Count != chunks.Count)
            {
                _logger.LogWarning("Embeddings indisponibles pour {File} ({Error})", jsonPath, resp.Error ?? "compte incoherent");
                return false;
            }

            var (project, baseName) = DeriveProject(outputRoot, jsonPath);
            var items = chunks.Zip(resp.Vectors, (c, v) => (c, v)).ToList();
            _vectors.ReplaceForPath(jsonPath, project, baseName, mtime, items);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vectorisation echouee pour {File}", jsonPath);
            return false;
        }
    }

    private static (string Project, string BaseName) DeriveProject(string outputRoot, string jsonPath)
    {
        var relDir = Path.GetDirectoryName(Path.GetRelativePath(outputRoot, jsonPath)) ?? "";
        var project = relDir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(racine)";
        return (project, Path.GetFileNameWithoutExtension(jsonPath));
    }

    private EngineRequest BuildRequest(TranscriptionJob job)
    {
        var project = PathResolver.FindProject(_config, job.AudioPath);
        var s = _config.EffectiveFor(project);
        string? voicesDir = s.SpeakerId.Enabled
            ? PathResolver.ResolveVoicesDir(_config, project, s.SpeakerId.VoicesDirName)
            : null;

        return new EngineRequest
        {
            AudioPath = job.AudioPath,
            OutputDir = job.OutputDir,
            BaseName = job.BaseName,
            Language = s.Engine.Language,
            ModelSize = s.Engine.ModelSize,
            Device = s.Engine.Device,
            ComputeType = s.Engine.ComputeType,
            BatchSize = s.Engine.BatchSize,
            DiarizationEnabled = s.Diarization.Enabled,
            MinSpeakers = s.Diarization.MinSpeakers,
            MaxSpeakers = s.Diarization.MaxSpeakers,
            SpeakerIdEnabled = s.SpeakerId.Enabled && voicesDir != null,
            VoicesDir = voicesDir,
            SpeakerIdThreshold = s.SpeakerId.Threshold,
            OutputMarkdown = s.Outputs.Markdown,
            OutputJson = s.Outputs.Json,
            OutputSrt = s.Outputs.Srt,
            OutputText = s.Outputs.Text,
            ModelCacheDir = ConfigStore.ExpandPath(_config.ModelCacheDir),
        };
    }
}
