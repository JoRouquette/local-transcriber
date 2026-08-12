using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Engine;
using LocalTranscriber.Core.Jobs;
using LocalTranscriber.Gui.Services;
using MaterialDesignThemes.Wpf;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Page Traitements et fichiers : état du moteur, du worker, de la file et logs live.</summary>
public sealed partial class ServiceViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ISnackbarMessageQueue _snackbar;
    private readonly DispatcherTimer _timer;
    private EngineSetup _engine;

    // Garde de re-entrance : un tick est ignore si un rafraichissement est deja en cours.
    private bool _refreshing;

    public ServiceViewModel(SettingsService settings, ISnackbarMessageQueue snackbar)
    {
        _settings = settings;
        _snackbar = snackbar;
        _engine = EngineSetup.FromConfig(settings.Config);
        Jobs = new ObservableCollection<TranscriptionJob>();

        // La config peut etre rechargee : on reconstruit le moteur sur la config a jour.
        _settings.Reloaded += OnSettingsReloaded;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Recharge : le moteur pointait sur une config perimee, on le recree.</summary>
    private void OnSettingsReloaded()
    {
        _engine = EngineSetup.FromConfig(_settings.Config);
        _ = RefreshAsync();
    }

    public ObservableCollection<TranscriptionJob> Jobs { get; }

    [ObservableProperty]
    private string _serviceStatus = "…";

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>État métier du worker : pilote la visibilité des boutons selon la situation réelle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(IsRunningState))]
    [NotifyCanExecuteChangedFor(nameof(InstallServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartServiceCommand))]
    private WorkerState _workerState = WorkerState.Error;

    /// <summary>Vrai pendant une transition (démarrage/arrêt/redémarrage) : gèle les boutons.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartServiceCommand))]
    private bool _isBusy;

    /// <summary>Libellé de la transition en cours (« Démarrage… », « Arrêt… », « Redémarrage… »).</summary>
    [ObservableProperty]
    private string _workerBusyLabel = "";

    public bool IsNotInstalled => WorkerState == WorkerState.NotInstalled;
    public bool IsStopped => WorkerState == WorkerState.Stopped;
    public bool IsRunningState => WorkerState == WorkerState.Running;

    [ObservableProperty]
    private TranscriptionJob? _selectedJob;

    // ---- Environnement moteur (installeur leger) ----
    [ObservableProperty]
    private bool _engineReady;

    [ObservableProperty]
    private bool _engineUpToDate;

    [ObservableProperty]
    private string _engineStatus = "Vérification du moteur…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunEngineSetup))]
    private bool _isInstallingEngine;

    /// <summary>Faux pendant une installation (désactive les boutons moteur).</summary>
    public bool CanRunEngineSetup => !IsInstallingEngine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEngineLog))]
    private string _engineSetupLog = "";

    /// <summary>Le journal reste affiché tant qu'il n'est pas vide (y compris après un échec).</summary>
    public bool HasEngineLog => !string.IsNullOrWhiteSpace(EngineSetupLog);

    // ---- Monitoring de la file ----
    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _processingCount;

    [ObservableProperty]
    private int _doneCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private string _currentFile = "—";

    [ObservableProperty]
    private string _currentElapsed = "";

    [ObservableProperty]
    private string _lastError = "";

    // ---- Logs live + santé des services ----
    [ObservableProperty]
    private string _liveLog = "";

    [ObservableProperty]
    private bool _mcpHealthy;

    [ObservableProperty]
    private bool _sidecarHealthy;

    // ================= Moteur : mise à jour / réinstallation =================

    [RelayCommand]
    private Task UpdateEngine() =>
        RunEngineSetupAsync(recreate: false, "Mise à jour du moteur Python…");

    [RelayCommand]
    private Task ReinstallEngine()
    {
        var confirm = MessageBox.Show(
            "Réinstaller proprement le moteur Python ?\n\nL'environnement actuel sera supprimé "
                + "puis recréé de zéro (plusieurs minutes). Le worker sera arrêté puis redémarré.",
            "Confirmer la réinstallation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );
        return confirm == MessageBoxResult.Yes
            ? RunEngineSetupAsync(recreate: true, "Réinstallation propre du moteur Python…")
            : Task.CompletedTask;
    }

    private async Task RunEngineSetupAsync(bool recreate, string startMessage)
    {
        if (IsInstallingEngine)
            return;
        IsInstallingEngine = true;
        EngineSetupLog = "";

        var progress = new Progress<string>(line =>
        {
            var text = EngineSetupLog + line + "\n";
            // Journal borné : on garde les derniers ~200 Ko.
            EngineSetupLog = text.Length > 200_000 ? text[^200_000..] : text;
        });
        void Log(string m) => ((IProgress<string>)progress).Report(m);

        var wasRunning = IsRunning;
        try
        {
            _snackbar.Enqueue(startMessage);
            // Le worker garde le sidecar Python ouvert : il verrouille l'environnement.
            // On l'arrête pour que uv puisse (re)créer le venv sans « accès refusé ».
            Log("Arrêt du worker pour libérer l'environnement…");
            await Task.Run(WindowsServiceControl.Stop);
            await Task.Delay(1500);

            var ok = await _engine.InstallAsync(recreate, progress, cuda: false);
            UpdateEngineStatus();
            _snackbar.Enqueue(ok ? "Moteur prêt." : "Échec (voir le journal ci-dessous).");
        }
        catch (Exception ex)
        {
            Log("Erreur : " + ex.Message);
            _snackbar.Enqueue("Erreur : " + ex.Message);
        }
        finally
        {
            IsInstallingEngine = false;
            if (wasRunning)
            {
                try
                {
                    await Task.Run(WindowsServiceControl.Start);
                }
                catch
                { /* le worker sera relançable manuellement */
                }
            }
        }
    }

    [RelayCommand]
    private void CopyEngineLog()
    {
        try
        {
            Clipboard.SetText(EngineSetupLog ?? "");
            _snackbar.Enqueue("Journal copié dans le presse-papiers.");
        }
        catch
        { /* le presse-papiers peut être indisponible ponctuellement */
        }
    }

    // ================= File : actions =================

    [RelayCommand]
    private void ReprocessFile()
    {
        if (SelectedJob is null)
            return;
        _settings.EnqueueCommand(CommandTypes.ReprocessFile, SelectedJob.AudioPath);
        _snackbar.Enqueue($"Retraitement demandé : {Path.GetFileName(SelectedJob.AudioPath)}");
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var dir = SelectedJob?.OutputDir;
        if (string.IsNullOrWhiteSpace(dir))
            dir = ConfigStore.ExpandPath(_settings.Config.OutputRoot);
        try
        {
            if (Directory.Exists(dir))
                Process.Start(
                    new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }
                );
            else
                _snackbar.Enqueue("Dossier de sortie introuvable : " + dir);
        }
        catch (Exception ex)
        {
            _snackbar.Enqueue("Impossible d'ouvrir le dossier : " + ex.Message);
        }
    }

    [RelayCommand]
    private void CancelCurrent()
    {
        // On n'utilise PLUS le compteur ProcessingCount comme garde : il vient du resume DB
        // rafraichi toutes les 3 s, donc souvent perime — c'etait la cause de l'annulation
        // « aleatoire » (bouton refuse alors qu'un job tourne, ou flag pose trop tard). On depose
        // toujours le drapeau : le worker l'ignore s'il ne traite rien (et le nettoie au prochain
        // passage), et l'honore immediatement si un job est en cours.
        try
        {
            var dataDir = ConfigStore.ExpandPath(_settings.Config.DataDir);
            var flag = ControlSignals.CancelCurrentFlag(dataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
            File.WriteAllText(flag, DateTime.UtcNow.ToString("o"));
            _snackbar.Enqueue("Annulation demandée — le traitement en cours va s'arrêter.");
        }
        catch (Exception ex)
        {
            _snackbar.Enqueue("Erreur : " + ex.Message);
        }
    }

    [RelayCommand]
    private void RetryFailed()
    {
        _settings.EnqueueCommand(CommandTypes.RetryFailed, "");
        _snackbar.Enqueue("Relance des fichiers en échec demandée.");
    }

    [RelayCommand]
    private void UnblockStuck()
    {
        _settings.EnqueueCommand(CommandTypes.RequeueStale, "");
        _snackbar.Enqueue("Déblocage des traitements figés demandé.");
    }

    // ================= Worker : cycle de vie =================

    private bool CanInstall => !IsBusy && WorkerState == WorkerState.NotInstalled;
    private bool CanStart => !IsBusy && WorkerState == WorkerState.Stopped;
    private bool CanStop => !IsBusy && WorkerState == WorkerState.Running;
    private bool CanRestart => !IsBusy && WorkerState == WorkerState.Running;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallService() =>
        RunWorkerAsync(
            WindowsServiceControl.Install,
            "Activation du worker en arrière-plan…",
            "Installation…",
            "Worker installé et démarré.",
            "L'installation du worker a échoué."
        );

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartService() =>
        RunWorkerAsync(
            WindowsServiceControl.Start,
            "Démarrage du worker…",
            "Démarrage…",
            "Worker démarré.",
            "Le worker n'a pas démarré (voir le journal)."
        );

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopService() =>
        RunWorkerAsync(
            WindowsServiceControl.Stop,
            "Arrêt du worker…",
            "Arrêt…",
            "Worker arrêté.",
            "Le worker ne s'est pas arrêté."
        );

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartService() =>
        RunWorkerAsync(
            WindowsServiceControl.Restart,
            "Redémarrage du worker…",
            "Redémarrage…",
            "Worker redémarré.",
            "Le redémarrage a échoué."
        );

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync();

    /// <summary>
    /// Exécute une action de cycle de vie du worker en VÉRIFIANT le résultat. L'action bloque
    /// jusqu'à ce que l'état cible soit atteint (ou timeout) et renvoie un booléen : on affiche
    /// donc un retour honnête au lieu d'un message optimiste. Le timer d'auto-refresh est mis en
    /// pause le temps de la transition pour ne pas afficher d'état intermédiaire trompeur.
    /// </summary>
    private async Task RunWorkerAsync(
        Func<bool> action,
        string startMessage,
        string busyLabel,
        string okMessage,
        string failMessage
    )
    {
        if (IsBusy)
            return;
        IsBusy = true;
        WorkerBusyLabel = busyLabel;
        _timer.Stop();
        _snackbar.Enqueue(startMessage);
        try
        {
            var reached = await Task.Run(action);
            _snackbar.Enqueue(reached ? okMessage : failMessage);
        }
        catch (Exception ex)
        {
            _snackbar.Enqueue("Erreur : " + ex.Message);
        }
        finally
        {
            WorkerBusyLabel = "";
            IsBusy = false;
            await RefreshAsync();
            _timer.Start();
        }
    }

    // ================= Rafraîchissement =================

    public async Task RefreshAsync()
    {
        // Un rafraichissement deja en cours, ou une transition worker en cours : on saute ce tick
        // pour eviter le chevauchement et l'affichage d'un etat intermediaire trompeur.
        if (_refreshing || IsBusy)
            return;
        _refreshing = true;
        try
        {
            var state = await Task.Run(WindowsServiceControl.QueryState);
            ServiceStatus = WindowsServiceControl.Describe(state);
            // L'etat pilote a la fois l'affichage et le gating des boutons (via WorkerState).
            WorkerState = state;
            IsRunning = state == WorkerState.Running;

            UpdateEngineStatus();

            var jobs = await Task.Run(LoadJobs);
            Jobs.Clear();
            foreach (var j in jobs)
                Jobs.Add(j);

            var summary = await Task.Run(LoadSummary);
            if (summary is not null)
            {
                PendingCount = summary.Pending;
                ProcessingCount = summary.Processing;
                DoneCount = summary.Done;
                FailedCount = summary.Failed;
                CurrentFile = string.IsNullOrEmpty(summary.CurrentFile)
                    ? "—"
                    : Path.GetFileName(summary.CurrentFile);
                CurrentElapsed = summary.CurrentStartedAt is { } started
                    ? FormatDuration(DateTime.UtcNow - started)
                    : "";
                LastError = string.IsNullOrEmpty(summary.LastError)
                    ? ""
                    : $"{Path.GetFileName(summary.LastErrorFile)} — {summary.LastError}";
            }

            LiveLog = await Task.Run(TailLog);
            McpHealthy = await CanConnectAsync(_settings.Config.McpPort);
            SidecarHealthy =
                _settings.Config.SemanticEnabled
                && await CanConnectAsync(_settings.Config.EmbeddingSidecarPort);
        }
        catch (Exception ex)
        {
            // Un tick ne doit jamais remonter d'exception non observee : on le trace en dernier ressort.
            Debug.WriteLine("RefreshAsync: " + ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateEngineStatus()
    {
        try
        {
            EngineReady = _engine.IsReady;
            if (!EngineReady)
            {
                EngineUpToDate = false;
                EngineStatus = "Moteur Python : non installé";
                return;
            }
            EngineUpToDate = _engine.IsUpToDate;
            var installed = _engine.ReadManifest()?.AppVersion;
            var suffix = string.IsNullOrEmpty(installed) ? "" : $" (v{installed})";
            EngineStatus = EngineUpToDate
                ? $"Moteur Python : à jour{suffix}"
                : $"Moteur Python : mise à jour requise{suffix}";
        }
        catch
        {
            EngineStatus = "Moteur Python : état indéterminé";
        }
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
            d = TimeSpan.Zero;
        return d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
    }

    private string TailLog()
    {
        try
        {
            var logDir = Path.Combine(ConfigStore.ExpandPath(_settings.Config.DataDir), "logs");
            if (!Directory.Exists(logDir))
                return "";
            var latest = new DirectoryInfo(logDir)
                .GetFiles("worker-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
                return "";

            using var fs = new FileStream(
                latest.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            // On ne lit que la fin du fichier (au plus 64 Ko) pour rester léger.
            const long window = 64 * 1024;
            if (fs.Length > window)
                fs.Seek(-window, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd().Replace("\r", "");
            var lines = text.Split('\n');
            var tail = lines.Length > 200 ? lines[^200..] : lines;
            return string.Join("\n", tail).TrimEnd();
        }
        catch
        {
            return "";
        }
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(300);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private JobSummary? LoadSummary()
    {
        try
        {
            var db = Path.Combine(ConfigStore.ExpandPath(_settings.Config.DataDir), "jobs.db");
            return File.Exists(db) ? new JobStore(db).Summarize() : null;
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<TranscriptionJob> LoadJobs()
    {
        try
        {
            var db = Path.Combine(ConfigStore.ExpandPath(_settings.Config.DataDir), "jobs.db");
            if (!File.Exists(db))
                return Array.Empty<TranscriptionJob>();
            return new JobStore(db).ListRecent(50);
        }
        catch
        {
            return Array.Empty<TranscriptionJob>();
        }
    }
}
