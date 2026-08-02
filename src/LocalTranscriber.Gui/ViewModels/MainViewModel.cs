using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Jobs;
using Microsoft.Win32;

namespace LocalTranscriber.Gui.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private AppConfig _config;

    public MainViewModel()
    {
        _config = ConfigStore.Load();
        Projects = new ObservableCollection<ProjectConfig>(_config.Projects);
        Jobs = new ObservableCollection<TranscriptionJob>();

        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Reload);
        BrowseWatchCommand = new RelayCommand(() => WatchRoot = Browse(WatchRoot) ?? WatchRoot);
        BrowseOutputCommand = new RelayCommand(() => OutputRoot = Browse(OutputRoot) ?? OutputRoot);
        AddProjectCommand = new RelayCommand(AddProject);
        RemoveProjectCommand = new RelayCommand(RemoveProject, () => SelectedProject != null);

        InstallServiceCommand = new RelayCommand(() => Safe(WindowsServiceControl.Install));
        StartServiceCommand = new RelayCommand(() => Safe(WindowsServiceControl.Start));
        StopServiceCommand = new RelayCommand(() => Safe(WindowsServiceControl.Stop));
        RefreshStatusCommand = new RelayCommand(RefreshStatus);
        RefreshJobsCommand = new RelayCommand(RefreshJobs);

        RefreshStatus();
        RefreshJobs();
    }

    // ---- Options pour les listes deroulantes ----
    public string[] ModelSizes { get; } = { "tiny", "base", "small", "medium", "large-v2", "large-v3" };
    public string[] Devices { get; } = { "auto", "cuda", "cpu" };
    public string[] ComputeTypes { get; } = { "auto", "float16", "int8", "int8_float16", "float32" };
    public string[] Languages { get; } = { "auto", "fr", "en", "es", "de", "it", "nl", "pt" };

    // ---- Chemins ----
    public string WatchRoot { get => _config.WatchRoot; set { _config.WatchRoot = value; OnPropertyChanged(); } }
    public string OutputRoot { get => _config.OutputRoot; set { _config.OutputRoot = value; OnPropertyChanged(); } }
    public string ModelCacheDir { get => _config.ModelCacheDir; set { _config.ModelCacheDir = value; OnPropertyChanged(); } }

    // ---- Moteur (global) ----
    public string ModelSize { get => _config.Engine.ModelSize; set { _config.Engine.ModelSize = value; OnPropertyChanged(); } }
    public string Device { get => _config.Engine.Device; set { _config.Engine.Device = value; OnPropertyChanged(); } }
    public string ComputeType { get => _config.Engine.ComputeType; set { _config.Engine.ComputeType = value; OnPropertyChanged(); } }
    public string Language { get => _config.Engine.Language; set { _config.Engine.Language = value; OnPropertyChanged(); } }

    public string HfToken { get => _config.HfToken ?? ""; set { _config.HfToken = value; OnPropertyChanged(); } }

    public bool DiarizationEnabled { get => _config.Diarization.Enabled; set { _config.Diarization.Enabled = value; OnPropertyChanged(); } }
    public bool SpeakerIdEnabled { get => _config.SpeakerIdentification.Enabled; set { _config.SpeakerIdentification.Enabled = value; OnPropertyChanged(); } }
    public double SpeakerThreshold { get => _config.SpeakerIdentification.Threshold; set { _config.SpeakerIdentification.Threshold = value; OnPropertyChanged(); } }

    // ---- Projets ----
    public ObservableCollection<ProjectConfig> Projects { get; }
    private ProjectConfig? _selectedProject;
    public ProjectConfig? SelectedProject { get => _selectedProject; set => Set(ref _selectedProject, value); }

    // ---- Etat / file ----
    public ObservableCollection<TranscriptionJob> Jobs { get; }
    private string _serviceStatus = "";
    public string ServiceStatus { get => _serviceStatus; set => Set(ref _serviceStatus, value); }

    // ---- Commandes ----
    public ICommand SaveCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand BrowseWatchCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand AddProjectCommand { get; }
    public ICommand RemoveProjectCommand { get; }
    public ICommand InstallServiceCommand { get; }
    public ICommand StartServiceCommand { get; }
    public ICommand StopServiceCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand RefreshJobsCommand { get; }

    private void Save()
    {
        _config.Projects = Projects.ToList();
        ConfigStore.Save(_config);
        MessageBox.Show("Configuration enregistree.\nLe service la rechargera automatiquement.",
            "LocalTranscriber", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Reload()
    {
        _config = ConfigStore.Load();
        Projects.Clear();
        foreach (var p in _config.Projects) Projects.Add(p);
        foreach (var name in new[] { nameof(WatchRoot), nameof(OutputRoot), nameof(ModelCacheDir),
                     nameof(ModelSize), nameof(Device), nameof(ComputeType), nameof(Language),
                     nameof(DiarizationEnabled), nameof(SpeakerIdEnabled), nameof(SpeakerThreshold), nameof(HfToken) })
            OnPropertyChanged(name);
        RefreshJobs();
    }

    private void AddProject()
    {
        var p = new ProjectConfig { Name = "Nouveau projet", RelativePath = "NouveauProjet", Enabled = true };
        Projects.Add(p);
        SelectedProject = p;
    }

    private void RemoveProject()
    {
        if (SelectedProject != null) Projects.Remove(SelectedProject);
    }

    private static string? Browse(string current)
    {
        var dlg = new OpenFolderDialog { Title = "Choisir un dossier" };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(ConfigStore.ExpandPath(current)))
            dlg.InitialDirectory = ConfigStore.ExpandPath(current);
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private void RefreshStatus() => ServiceStatus = WindowsServiceControl.QueryStatus();

    private void RefreshJobs()
    {
        Jobs.Clear();
        try
        {
            var db = Path.Combine(ConfigStore.ExpandPath(_config.DataDir), "jobs.db");
            if (!File.Exists(db)) return;
            var store = new JobStore(db);
            foreach (var j in store.ListRecent(50)) Jobs.Add(j);
        }
        catch { /* base absente ou verrouillee */ }
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
