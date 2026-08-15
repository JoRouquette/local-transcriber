using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Jobs;
using LocalTranscriber.Gui.Services;
using MaterialDesignThemes.Wpf;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Page Projets : sous-dossiers avec réglages spécifiques.</summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ISnackbarMessageQueue _snackbar;

    public ProjectsViewModel(SettingsService settings, ISnackbarMessageQueue snackbar)
    {
        _settings = settings;
        _snackbar = snackbar;
        // Sync auto au démarrage/rechargement : en mémoire uniquement, sans réécrire
        // config.local.json (qui contient le token) sans action explicite de l'utilisateur.
        _ = DiscoverProjectsAsync(announce: false, persist: false);
        _settings.Reloaded += () => _ = DiscoverProjectsAsync(announce: false, persist: false);
    }

    /// <summary>
    /// Ajoute automatiquement, en tant que projets <b>inactifs</b>, les sous-dossiers du
    /// dossier surveillé qui ne sont pas encore déclarés. Les projets existants gardent
    /// leurs réglages. Persiste si des nouveautés sont détectées.
    /// </summary>
    [RelayCommand]
    private Task RefreshProjects() => DiscoverProjectsAsync(announce: true, persist: true);

    private async Task DiscoverProjectsAsync(bool announce, bool persist)
    {
        try
        {
            var watch = ConfigStore.ExpandPath(_settings.Config.WatchRoot);
            // Enumeration disque HORS thread UI : sur un dossier reseau lent/indisponible,
            // Directory.Exists/GetDirectories peut bloquer plusieurs dizaines de secondes.
            var dirs = await Task.Run(() =>
                string.IsNullOrWhiteSpace(watch) || !Directory.Exists(watch)
                    ? null
                    : Directory.GetDirectories(watch)
            );
            if (dirs is null)
            {
                if (announce)
                    _snackbar.Enqueue("Dossier surveillé introuvable : configurez-le d'abord.");
                return;
            }

            // De retour sur le thread UI : la mise a jour d'ObservableCollection s'y fait.
            var known = new HashSet<string>(
                Projects.Select(p => p.RelativePath),
                StringComparer.OrdinalIgnoreCase
            );
            var added = 0;
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || known.Contains(name))
                    continue;
                Projects.Add(
                    new ProjectConfig
                    {
                        Name = name,
                        RelativePath = name,
                        Enabled = false,
                    }
                );
                known.Add(name);
                added++;
            }
            // On ne persiste (réécriture de config.local.json) que sur action explicite.
            if (added > 0 && persist)
                _settings.Save();
            if (announce)
                _snackbar.Enqueue(
                    added > 0
                        ? $"{added} projet(s) détecté(s) et ajouté(s) (inactifs)."
                        : "Aucun nouveau dossier : projets à jour."
                );
        }
        catch (Exception ex)
        {
            if (announce)
                _snackbar.Enqueue("Découverte des projets : " + ex.Message);
        }
    }

    public ObservableCollection<ProjectConfig> Projects => _settings.Projects;

    [ObservableProperty]
    private ProjectConfig? _selectedProject;

    [RelayCommand]
    private void AddProject()
    {
        // Nom/chemin uniques : deux ajouts sans renommage produiraient sinon deux entrees de meme
        // RelativePath (identifiant de dossier cote backend) — ambiguite silencieuse.
        var name = "Nouveau projet";
        var rel = "NouveauProjet";
        var i = 2;
        while (
            Projects.Any(p =>
                string.Equals(p.RelativePath, rel, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            name = $"Nouveau projet {i}";
            rel = $"NouveauProjet{i}";
            i++;
        }
        var proj = new ProjectConfig
        {
            Name = name,
            RelativePath = rel,
            Enabled = true,
        };
        Projects.Add(proj);
        SelectedProject = proj;
        _settings.Save(); // coherent avec le reste de la page (persistance immediate)
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveProject()
    {
        if (SelectedProject is null)
            return;
        var confirm = MessageBox.Show(
            $"Supprimer le projet « {SelectedProject.Name} » et ses réglages spécifiques ?",
            "Confirmer la suppression",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );
        if (confirm != MessageBoxResult.Yes)
            return;
        Projects.Remove(SelectedProject);
        _settings.Save(); // rend la suppression reellement definitive (coherent avec la confirmation)
    }

    private bool CanRemove() => SelectedProject != null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void ReprocessProject()
    {
        if (SelectedProject is null)
            return;
        _settings.EnqueueCommand(CommandTypes.ReprocessProject, SelectedProject.RelativePath);
        _snackbar.Enqueue($"Retraitement du projet demandé : {SelectedProject.Name}");
    }

    partial void OnSelectedProjectChanged(ProjectConfig? value)
    {
        RemoveProjectCommand.NotifyCanExecuteChanged();
        ReprocessProjectCommand.NotifyCanExecuteChanged();
        LoadProjectSettings(value);
    }

    // ==== Réglages spécifiques au projet sélectionné (diarisation + identification) ====
    // Le backend fusionne déjà « projet sinon global » via AppConfig.EffectiveFor ; ici on
    // édite les surcharges ProjectConfig.Diarization / .SpeakerIdentification.

    private bool _loadingProject;

    [ObservableProperty]
    private bool _hasSelectedProject;

    /// <summary>Le projet a des réglages propres (sinon il hérite du global).</summary>
    [ObservableProperty]
    private bool _projectHasSpecific;

    [ObservableProperty]
    private bool _projectDiarization = true;

    [ObservableProperty]
    private int _projectSpeakerCount;

    [ObservableProperty]
    private bool _projectSpeakerId;

    [ObservableProperty]
    private double _projectThreshold = 0.55;

    private void LoadProjectSettings(ProjectConfig? p)
    {
        _loadingProject = true;
        HasSelectedProject = p is not null;
        if (p is not null)
        {
            var g = _settings.Config;
            ProjectHasSpecific = p.Diarization is not null || p.SpeakerIdentification is not null;
            var diar = p.Diarization ?? g.Diarization;
            var sid = p.SpeakerIdentification ?? g.SpeakerIdentification;
            ProjectDiarization = diar.Enabled;
            ProjectSpeakerCount =
                diar.MinSpeakers.HasValue && diar.MinSpeakers == diar.MaxSpeakers
                    ? diar.MinSpeakers.Value
                    : 0;
            ProjectSpeakerId = sid.Enabled;
            ProjectThreshold = sid.Threshold;
        }
        _loadingProject = false;
    }

    partial void OnProjectHasSpecificChanged(bool value)
    {
        if (_loadingProject || SelectedProject is null)
            return;
        if (value)
        {
            PersistProjectSettings();
        }
        else
        {
            SelectedProject.Diarization = null;
            SelectedProject.SpeakerIdentification = null;
            _settings.Save();
            LoadProjectSettings(SelectedProject); // réaffiche les valeurs globales héritées
        }
    }

    partial void OnProjectDiarizationChanged(bool value) => PersistProjectSettings();

    partial void OnProjectSpeakerCountChanged(int value) => PersistProjectSettings();

    partial void OnProjectSpeakerIdChanged(bool value) => PersistProjectSettings();

    partial void OnProjectThresholdChanged(double value) => PersistProjectSettings();

    /// <summary>Écrit les surcharges du projet sélectionné dans la config, puis sauvegarde.</summary>
    private void PersistProjectSettings()
    {
        if (_loadingProject || SelectedProject is null || !ProjectHasSpecific)
            return;
        var g = _settings.Config;
        int? spk = ProjectSpeakerCount > 0 ? ProjectSpeakerCount : null;
        SelectedProject.Diarization = new DiarizationConfig
        {
            Enabled = ProjectDiarization,
            MinSpeakers = spk,
            MaxSpeakers = spk,
        };
        SelectedProject.SpeakerIdentification = new SpeakerIdConfig
        {
            Enabled = ProjectSpeakerId,
            Threshold = ProjectThreshold,
            VoicesDirName = g.SpeakerIdentification.VoicesDirName,
        };
        // Le modele est deja a jour en memoire (ci-dessus) ; on DEBOUNCE l'ecriture disque : faire
        // glisser le slider « Seuil » declenchait sinon des dizaines de _settings.Save() en rafale.
        DebouncedSave();
    }

    private CancellationTokenSource? _saveCts;

    private void DebouncedSave()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_saveCts.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            if (!token.IsCancellationRequested)
                _settings.Save();
        }
        catch (OperationCanceledException)
        { /* remplace par une sauvegarde plus recente : rien a faire */
        }
    }
}
