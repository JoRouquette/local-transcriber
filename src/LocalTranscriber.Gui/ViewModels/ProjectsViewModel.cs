using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        DiscoverProjects(announce: false); // sync auto au démarrage
        _settings.Reloaded += () => DiscoverProjects(announce: false);
    }

    /// <summary>
    /// Ajoute automatiquement, en tant que projets <b>inactifs</b>, les sous-dossiers du
    /// dossier surveillé qui ne sont pas encore déclarés. Les projets existants gardent
    /// leurs réglages. Persiste si des nouveautés sont détectées.
    /// </summary>
    [RelayCommand]
    private void RefreshProjects() => DiscoverProjects(announce: true);

    private void DiscoverProjects(bool announce)
    {
        try
        {
            var watch = ConfigStore.ExpandPath(_settings.Config.WatchRoot);
            if (string.IsNullOrWhiteSpace(watch) || !Directory.Exists(watch))
            {
                if (announce)
                    _snackbar.Enqueue("Dossier surveillé introuvable : configurez-le d'abord.");
                return;
            }
            var known = new HashSet<string>(
                Projects.Select(p => p.RelativePath),
                StringComparer.OrdinalIgnoreCase
            );
            var added = 0;
            foreach (var dir in Directory.GetDirectories(watch))
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
            if (added > 0)
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
        var p = new ProjectConfig
        {
            Name = "Nouveau projet",
            RelativePath = "NouveauProjet",
            Enabled = true,
        };
        Projects.Add(p);
        SelectedProject = p;
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveProject()
    {
        if (SelectedProject != null)
            Projects.Remove(SelectedProject);
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
    }
}
