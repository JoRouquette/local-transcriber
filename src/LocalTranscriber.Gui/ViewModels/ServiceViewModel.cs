using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Engine;
using LocalTranscriber.Core.Jobs;
using LocalTranscriber.Gui.Services;
using MaterialDesignThemes.Wpf;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Page Service &amp; File : état du service et file d'attente, rafraîchis automatiquement.</summary>
public sealed partial class ServiceViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ISnackbarMessageQueue _snackbar;
    private readonly DispatcherTimer _timer;

    public ServiceViewModel(SettingsService settings, ISnackbarMessageQueue snackbar)
    {
        _settings = settings;
        _snackbar = snackbar;
        Jobs = new ObservableCollection<TranscriptionJob>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<TranscriptionJob> Jobs { get; }

    [ObservableProperty] private string _serviceStatus = "…";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private TranscriptionJob? _selectedJob;

    // ---- Environnement moteur (installeur leger) ----
    private readonly EngineSetup _engine = new();
    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private bool _isInstallingEngine;
    [ObservableProperty] private string _engineSetupLog = "";

    [RelayCommand]
    private async Task InstallEngine()
    {
        if (IsInstallingEngine) return;
        IsInstallingEngine = true;
        EngineSetupLog = "";
        var progress = new Progress<string>(line =>
        {
            var text = EngineSetupLog + line + "\n";
            EngineSetupLog = text.Length > 16000 ? text[^16000..] : text;
        });
        try
        {
            _snackbar.Enqueue("Installation du moteur Python… (plusieurs minutes au premier lancement)");
            var ok = await _engine.SetupAsync(progress, cuda: false);
            EngineReady = _engine.IsReady;
            _snackbar.Enqueue(ok ? "Moteur installé." : "Échec de l'installation (voir le journal ci-dessous).");
        }
        finally
        {
            IsInstallingEngine = false;
        }
    }

    [RelayCommand]
    private void ReprocessFile()
    {
        if (SelectedJob is null) return;
        _settings.EnqueueCommand(CommandTypes.ReprocessFile, SelectedJob.AudioPath);
        _snackbar.Enqueue($"Retraitement demandé : {Path.GetFileName(SelectedJob.AudioPath)}");
    }

    [RelayCommand]
    private async Task InstallService()
    {
        await RunAsync(WindowsServiceControl.Install, "Installation du service (autorisez l'UAC)…");
    }

    [RelayCommand]
    private async Task StartService()
    {
        await RunAsync(WindowsServiceControl.Start, "Démarrage du service…");
    }

    [RelayCommand]
    private async Task StopService()
    {
        await RunAsync(WindowsServiceControl.Stop, "Arrêt du service…");
    }

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

    public async Task RefreshAsync()
    {
        var status = await Task.Run(WindowsServiceControl.QueryStatus);
        ServiceStatus = status;
        IsRunning = status.Contains("cours", StringComparison.OrdinalIgnoreCase);
        EngineReady = _engine.IsReady;

        var jobs = await Task.Run(LoadJobs);
        Jobs.Clear();
        foreach (var j in jobs) Jobs.Add(j);
    }

    private IReadOnlyList<TranscriptionJob> LoadJobs()
    {
        try
        {
            var db = Path.Combine(ConfigStore.ExpandPath(_settings.Config.DataDir), "jobs.db");
            if (!File.Exists(db)) return Array.Empty<TranscriptionJob>();
            return new JobStore(db).ListRecent(50);
        }
        catch
        {
            return Array.Empty<TranscriptionJob>();
        }
    }
}
