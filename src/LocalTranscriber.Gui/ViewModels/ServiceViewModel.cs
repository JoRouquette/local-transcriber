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

    public ServiceViewModel(SettingsService settings, ISnackbarMessageQueue snackbar)
    {
        _settings = settings;
        _snackbar = snackbar;
        _engine = EngineSetup.FromConfig(settings.Config);
        Jobs = new ObservableCollection<TranscriptionJob>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<TranscriptionJob> Jobs { get; }

    [ObservableProperty]
    private string _serviceStatus = "…";

    [ObservableProperty]
    private bool _isRunning;

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
    private Task ReinstallEngine() =>
        RunEngineSetupAsync(recreate: true, "Réinstallation propre du moteur Python…");

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
        if (ProcessingCount == 0)
        {
            _snackbar.Enqueue("Aucun traitement en cours.");
            return;
        }
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

    // ================= Worker : cycle de vie =================

    [RelayCommand]
    private async Task InstallService() =>
        await RunAsync(
            WindowsServiceControl.Install,
            "Activation du worker en arrière-plan (session utilisateur)…"
        );

    [RelayCommand]
    private async Task StartService() =>
        await RunAsync(WindowsServiceControl.Start, "Démarrage du worker…");

    [RelayCommand]
    private async Task StopService() =>
        await RunAsync(WindowsServiceControl.Stop, "Arrêt du worker…");

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync();

    private async Task RunAsync(Action action, string message)
    {
        try
        {
            _snackbar.Enqueue(message);
            await Task.Run(action);
            await Task.Delay(1500);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _snackbar.Enqueue("Erreur : " + ex.Message);
        }
    }

    // ================= Rafraîchissement =================

    public async Task RefreshAsync()
    {
        var status = await Task.Run(WindowsServiceControl.QueryStatus);
        ServiceStatus = status;
        IsRunning = status.Contains("cours", StringComparison.OrdinalIgnoreCase);

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
