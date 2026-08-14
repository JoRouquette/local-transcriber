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
    private CommandStore? _commands;
    private PythonEngineRunner? _runner;
    private string? _hfToken;
    private string _enginePath = "";

    // Cache (chemin -> taille+date) pour eviter de re-hasher les fichiers inchanges a chaque scan.
    private readonly Dictionary<string, (long Size, long Mtime)> _seen = new(
        StringComparer.OrdinalIgnoreCase
    );
    private EngineSetup _engineSetup = new();
    private string _dataDir = "";
    private EngineLogSink? _log;
    private bool _quietLogged;
    private bool _engineMissingLogged;

    // Anti-spam : on ne loggue l'erreur "base de donnees indisponible" qu'une fois par version de
    // config (sinon le message se repeterait a chaque tick tant que la base reste illisible).
    private DateTime _dbErrorLoggedMtime;

    public Worker(
        ILogger<Worker> logger,
        TranscriptIndex index,
        VectorStore vectors,
        EmbeddingClient embedder
    )
    {
        _logger = logger;
        _index = index;
        _vectors = vectors;
        _embedder = embedder;
        _sidecar = new SidecarManager(logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // IMPORTANT (service Windows) : rendre la main immediatement pour que l'hote
        // termine son demarrage et signale "En cours d'execution" au SCM sans attendre
        // le premier scan (qui, sur un dossier volumineux ou synchronise cloud, peut
        // depasser le delai de 30 s => erreur SCM 7009 "le service n'a pas repondu").
        await Task.Yield();

        // Identite du worker : PID + heure de demarrage. Permet de PROUVER dans les logs qu'un
        // seul worker tourne (un second demarrage laisserait une trace « doublon » cote Program).
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
            _logger.LogInformation(
                "LocalTranscriber demarre (worker PID {Pid}, {Start:u}).",
                self.Id,
                DateTime.Now
            );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReloadConfigIfChanged();
                if (
                    string.IsNullOrWhiteSpace(_config.WatchRoot)
                    || string.IsNullOrWhiteSpace(_config.OutputRoot)
                )
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                DrainCommands();
                ScanAndEnqueue();

                var outputRoot = ConfigStore.ExpandPath(_config.OutputRoot);
                _index.Refresh(outputRoot); // rafraichissement FTS (leger), meme en inactivite

                if (!File.Exists(_enginePath))
                {
                    if (!_engineMissingLogged)
                    {
                        _logger.LogWarning(
                            "Moteur Python non installe. Installez-le depuis l'application (onglet Service & File)."
                        );
                        _engineMissingLogged = true;
                    }
                }
                else if (_config.IsQuietNow(DateTime.Now))
                {
                    if (!_quietLogged)
                    {
                        _logger.LogInformation(
                            "Heures d'inactivite : traitement en pause (detection/mise en file maintenues)."
                        );
                        _quietLogged = true;
                    }
                }
                else
                {
                    _engineMissingLogged = false;
                    _quietLogged = false;

                    if (_config.SemanticEnabled)
                        await _sidecar.EnsureStartedAsync(
                            _enginePath,
                            _config.EmbeddingSidecarPort,
                            ConfigStore.ExpandPath(_config.ModelCacheDir),
                            _config.EmbeddingDevice,
                            stoppingToken
                        );

                    await ProcessQueueAsync(stoppingToken);
                    if (_config.SemanticEnabled)
                        await ReconcileVectorsAsync(outputRoot, 5, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur dans la boucle principale.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(2, _config.StabilizationSeconds)),
                stoppingToken
            );
        }

        _sidecar.Dispose();
        _logger.LogInformation("LocalTranscriber arrete.");
    }

    private void ReloadConfigIfChanged()
    {
        var path = ConfigStore.DefaultConfigPath;
        if (!File.Exists(path))
            return;
        var mtime = File.GetLastWriteTimeUtc(path);
        if (mtime == _configMtime && _jobs != null)
            return;

        _config = ConfigStore.Load(path);
        _configMtime = mtime;

        _dataDir = ConfigStore.ExpandPath(_config.DataDir);
        // Ces stores ouvrent une connexion SQLite par operation (using var c = Open()) et ne
        // conservent aucun handle persistant : rien a liberer avant de les recreer.
        try
        {
            Directory.CreateDirectory(_dataDir);
            _jobs = new JobStore(Path.Combine(_dataDir, "jobs.db"));
            _commands = new CommandStore(Path.Combine(_dataDir, "commands.db"));
        }
        catch (Exception ex)
        {
            // DataDir inaccessible, ou base verrouillee/corrompue : on ne peut pas traiter la file.
            // On loggue UNE fois par version de config (anti-spam) et on reessaiera au prochain tick
            // (un verrou transitoire — antivirus — finit par se liberer). Le service reste vivant.
            _jobs = null;
            _commands = null;
            if (_dbErrorLoggedMtime != mtime)
            {
                _dbErrorLoggedMtime = mtime;
                _logger.LogError(
                    ex,
                    "File indisponible : base de donnees inaccessible dans {Dir}.",
                    _dataDir
                );
            }
            return;
        }

        // Garde anti-boucle : les jobs interrompus ne sont repris que sous le plafond de
        // tentatives ; au-dela ils sont abandonnes (Failed) au lieu d'etre re-enfiles a l'infini.
        var staleMax = Math.Max(1, _config.MaxAutoRetries);
        var recovered = _jobs.RequeueStale(staleMax);
        if (recovered > 0)
            _logger.LogInformation(
                "Recuperation : {Count} job(s) interrompu(s) re-enfile(s) (plafond {Max}).",
                recovered,
                staleMax
            );
        _seen.Clear();

        _log = new EngineLogSink(Path.Combine(_dataDir, "logs"));
        _sidecar.OnLog = _log.Write;

        _engineSetup = EngineSetup.FromConfig(_config);
        _enginePath = ResolveEnginePath(_config.EngineExecutable);
        _hfToken = LoadHfToken();
        _runner = new PythonEngineRunner(_enginePath, _hfToken, _logger);

        _logger.LogInformation("Configuration (re)chargee. Moteur : {Engine}", _enginePath);
    }

    private string ResolveEnginePath(string configured)
    {
        var p = ConfigStore.ExpandPath(configured);
        var resolved = Path.IsPathRooted(p)
            ? p
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
        // Si l'exe configure n'existe pas (installeur leger : moteur pas encore installe),
        // on retombe sur l'executable de l'environnement Python mis en place au 1er lancement.
        return File.Exists(resolved) ? resolved : _engineSetup.ConsoleExe;
    }

    private string? LoadHfToken()
    {
        // 1. Parametres de l'application (recommande).
        if (!string.IsNullOrWhiteSpace(_config.HfToken))
            return _config.HfToken.Trim();

        // 2. Repli : variable d'environnement.
        var fromEnv = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

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
        if (_jobs is null)
            return;
        var watchRoot = ConfigStore.ExpandPath(_config.WatchRoot);
        var outputRoot = ConfigStore.ExpandPath(_config.OutputRoot);
        if (!Directory.Exists(watchRoot))
            return;

        // Retry auto configurable : avant le scan, on repasse en Pending les echecs restes sous
        // le plafond de tentatives. Desactive par defaut (les echecs restent alors en Failed).
        if (_config.AutoRetryFailedJobs)
        {
            var requeued = _jobs.RequeueFailedForRetry(_config.MaxAutoRetries);
            if (requeued > 0)
                _logger.LogInformation(
                    "Retry auto : {Count} fichier(s) en echec re-enfile(s).",
                    requeued
                );
        }

        var extensions = new HashSet<string>(_config.FileTypes, StringComparer.OrdinalIgnoreCase);
        var voicesDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _config.SpeakerIdentification.VoicesDirName,
        };
        foreach (var p in _config.Projects.Where(p => p.SpeakerIdentification != null))
            voicesDirNames.Add(p.SpeakerIdentification!.VoicesDirName);

        // IgnoreInaccessible : un sous-dossier a ACL restrictives (partage reseau/OneDrive) ne doit
        // pas interrompre tout le scan (sinon les fichiers situes apres ne sont jamais decouverts).
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };
        foreach (var file in Directory.EnumerateFiles(watchRoot, "*.*", enumOptions))
        {
            if (!extensions.Contains(Path.GetExtension(file)))
                continue;
            if (file.Split(Path.DirectorySeparatorChar).Any(seg => voicesDirNames.Contains(seg)))
                continue;

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                continue;
            }
            var key = (info.Length, info.LastWriteTimeUtc.Ticks);
            // Deja pris en compte et inchange -> on evite de re-hasher (economie CPU/IO).
            if (_seen.TryGetValue(file, out var prev) && prev == key)
                continue;

            if (!IsStable(file))
                continue;

            var project = PathResolver.FindProject(_config, file);
            if (project is { Enabled: false })
                continue;

            try
            {
                var hash = FileHasher.QuickHash(file);
                if (!_jobs.AlreadyKnown(hash))
                {
                    var outputDir = PathResolver.ResolveOutputDir(watchRoot, outputRoot, file);
                    _jobs.Enqueue(file, hash, outputDir, PathResolver.BaseName(file));
                    _logger.LogInformation("En file : {File}", file);
                }
                _seen[file] = key; // memorise (connu ou nouvellement enfile) pour ne plus re-hasher
            }
            catch (IOException)
            { /* encore en ecriture */
            }
        }
    }

    /// <summary>Applique les commandes de la GUI (retraiter un fichier / un projet).</summary>
    private void DrainCommands()
    {
        if (_commands is null || _jobs is null)
            return;
        var watchRoot = ConfigStore.ExpandPath(_config.WatchRoot);

        foreach (var cmd in _commands.Drain())
        {
            try
            {
                if (
                    cmd.Type == CommandTypes.ReprocessFile
                    && !string.IsNullOrWhiteSpace(cmd.Payload)
                )
                {
                    _jobs.DeleteByPath(cmd.Payload);
                    _seen.Remove(cmd.Payload);
                    _logger.LogInformation("Retraitement demande : {File}", cmd.Payload);
                }
                else if (cmd.Type == CommandTypes.ReprocessProject)
                {
                    var dir = Path.GetFullPath(Path.Combine(watchRoot, cmd.Payload));
                    _jobs.DeleteUnderPath(dir);
                    foreach (
                        var k in _seen
                            .Keys.Where(k => k.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                            .ToList()
                    )
                        _seen.Remove(k);
                    _logger.LogInformation("Retraitement du projet demande : {Dir}", dir);
                }
                else if (cmd.Type == CommandTypes.RetryFailed)
                {
                    var n = _jobs.RequeueAllFailed();
                    _logger.LogInformation("Relance des echecs : {N} job(s) re-enfile(s).", n);
                    _log?.Write($"[file] relance des echecs : {n} job(s)");
                }
                else if (cmd.Type == CommandTypes.RequeueStale)
                {
                    var n = _jobs.RequeueStale(Math.Max(1, _config.MaxAutoRetries));
                    _logger.LogInformation("Deblocage des jobs figes : {N} job(s).", n);
                    _log?.Write($"[file] deblocage des jobs figes : {n} job(s)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Commande ignoree : {Type}", cmd.Type);
            }
        }
    }

    private bool IsStable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc > DateTime.UtcNow.AddSeconds(-_config.StabilizationSeconds))
                return false;
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        if (_jobs is null || _runner is null)
            return;

        var cancelFlag = ControlSignals.CancelCurrentFlag(_dataDir);
        // Un drapeau reste d'une session precedente ne doit pas annuler le prochain job.
        TryDelete(cancelFlag);

        TranscriptionJob? job;
        while (!ct.IsCancellationRequested && (job = _jobs.DequeueNext()) is not null)
        {
            _logger.LogInformation("Traitement : {File}", job.AudioPath);
            _log?.Write($"[job] debut : {job.AudioPath}");

            // BLINDAGE : chaque job est isole. Une exception imprevue (I/O, moteur, SQLite...) ne
            // doit JAMAIS avorter le reste de la file ni laisser le job coince en Processing — on
            // le marque Failed et on passe au suivant. Seul l'arret du worker (ct) rompt la boucle.
            try
            {
                var request = BuildRequest(job);

                // CTS lie au worker + annulable par le drapeau d'annulation depose par la GUI.
                using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var pollStop = new CancellationTokenSource();
                var poller = WatchCancelAsync(cancelFlag, jobCts, pollStop.Token);

                // Garde-fou anti-blocage : au-dela de N min sans aucune sortie du moteur, on le tue.
                var inactivityTimeout =
                    _config.EngineInactivityTimeoutMinutes > 0
                        ? TimeSpan.FromMinutes(_config.EngineInactivityTimeoutMinutes)
                        : (TimeSpan?)null;

                EngineResult result;
                try
                {
                    result = await _runner.RunAsync(
                        request,
                        line => _log?.Write(line),
                        jobCts.Token,
                        inactivityTimeout
                    );
                }
                finally
                {
                    pollStop.Cancel();
                    await poller;
                }

                if (result.Status == "cancelled")
                {
                    _jobs.MarkFailed(job.Id, "Annule par l'utilisateur.");
                    _logger.LogWarning("Annule : {File}", job.AudioPath);
                    _log?.Write($"[job] annule : {job.AudioPath}");
                    // Annulation explicite : on ne poursuit pas la file dans cette passe.
                    break;
                }

                if (result.IsSuccess)
                {
                    _jobs.MarkDone(job.Id);
                    _logger.LogInformation(
                        "OK : {File} ({Segments} segments, {Speakers} locuteurs)",
                        job.AudioPath,
                        result.SegmentCount,
                        result.SpeakerCount
                    );
                    _log?.Write(
                        $"[job] termine : {job.AudioPath} ({result.SegmentCount} segments, {result.SpeakerCount} locuteurs)"
                    );

                    if (_config.SemanticEnabled && result.JsonPath is { } jp && File.Exists(jp))
                        await VectorizeAsync(jp, ConfigStore.ExpandPath(_config.OutputRoot), ct);
                }
                else
                {
                    _jobs.MarkFailed(job.Id, result.Error ?? "erreur inconnue");
                    _logger.LogError("Echec : {File} — {Error}", job.AudioPath, result.Error);
                    _log?.Write($"[job] echec : {job.AudioPath} — {result.Error}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Arret du worker : on laisse le job en Processing (RequeueStale le reprendra,
                // sous plafond, au redemarrage) et on sort proprement de la passe.
                break;
            }
            catch (Exception ex)
            {
                // Filet ultime : le job fautif est marque Failed, la file continue.
                try
                {
                    _jobs.MarkFailed(job.Id, "Erreur inattendue : " + ex.Message);
                }
                catch (Exception markEx)
                {
                    _logger.LogError(markEx, "Echec du MarkFailed pour {File}", job.AudioPath);
                }
                _logger.LogError(ex, "Echec inattendu : {File}", job.AudioPath);
                _log?.Write($"[job] echec inattendu : {job.AudioPath} — {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Surveille le drapeau d'annulation pendant un traitement. Si la GUI le depose, annule le
    /// job en cours (le moteur est alors tue par <see cref="PythonEngineRunner"/>).
    /// </summary>
    private async Task WatchCancelAsync(
        string flagPath,
        CancellationTokenSource jobCts,
        CancellationToken until
    )
    {
        try
        {
            while (!until.IsCancellationRequested && !jobCts.IsCancellationRequested)
            {
                if (File.Exists(flagPath))
                {
                    TryDelete(flagPath);
                    _logger.LogWarning("Annulation du traitement en cours demandee.");
                    _log?.Write("[job] annulation demandee");
                    jobCts.Cancel();
                    return;
                }
                await Task.Delay(500, until);
            }
        }
        catch (OperationCanceledException)
        { /* le job s'est termine normalement : arret du poller */
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        { /* best effort */
        }
    }

    private async Task ReconcileVectorsAsync(string outputRoot, int max, CancellationToken ct)
    {
        if (!Directory.Exists(outputRoot))
            return;
        var done = 0;
        foreach (
            var json in Directory.EnumerateFiles(outputRoot, "*.json", SearchOption.AllDirectories)
        )
        {
            if (done >= max || ct.IsCancellationRequested)
                break;
            var mtime = File.GetLastWriteTimeUtc(json).ToString("o");
            if (_vectors.IsUpToDate(json, mtime))
                continue;
            if (await VectorizeAsync(json, outputRoot, ct))
                done++;
        }
    }

    private async Task<bool> VectorizeAsync(
        string jsonPath,
        string outputRoot,
        CancellationToken ct
    )
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(jsonPath).ToString("o");
            var segments = TranscriptReader.ReadSegments(jsonPath);
            var chunks = Chunker.Chunk(
                segments,
                _config.ChunkMaxChars,
                _config.ChunkOverlapSegments
            );
            if (chunks.Count == 0)
                return false;

            var resp = await _embedder.EmbedAsync(chunks.Select(c => c.Text), "passage", ct);
            if (!resp.IsSuccess || resp.Vectors.Count != chunks.Count)
            {
                _logger.LogWarning(
                    "Embeddings indisponibles pour {File} ({Error})",
                    jsonPath,
                    resp.Error ?? "compte incoherent"
                );
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

    private static (string Project, string BaseName) DeriveProject(
        string outputRoot,
        string jsonPath
    )
    {
        var relDir = Path.GetDirectoryName(Path.GetRelativePath(outputRoot, jsonPath)) ?? "";
        var project =
            relDir
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
            ?? "(racine)";
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
            ChunkingEnabled = _config.Chunking.Enabled,
            ChunkThresholdMinutes = _config.Chunking.ThresholdMinutes,
            ChunkMinutes = _config.Chunking.ChunkMinutes,
            ChunkMinSilenceSeconds = _config.Chunking.MinSilenceSeconds,
            MaxAudioMinutes = _config.MaxAudioMinutes,
        };
    }
}
