using System.Collections.ObjectModel;
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
    }

    public ObservableCollection<ProjectConfig> Projects => _settings.Projects;

    [ObservableProperty] private ProjectConfig? _selectedProject;

    [RelayCommand]
    private void AddProject()
    {
        var p = new ProjectConfig { Name = "Nouveau projet", RelativePath = "NouveauProjet", Enabled = true };
        Projects.Add(p);
        SelectedProject = p;
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveProject()
    {
        if (SelectedProject != null) Projects.Remove(SelectedProject);
    }

    private bool CanRemove() => SelectedProject != null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void ReprocessProject()
    {
        if (SelectedProject is null) return;
        _settings.EnqueueCommand(CommandTypes.ReprocessProject, SelectedProject.RelativePath);
        _snackbar.Enqueue($"Retraitement du projet demandé : {SelectedProject.Name}");
    }

    partial void OnSelectedProjectChanged(ProjectConfig? value)
    {
        RemoveProjectCommand.NotifyCanExecuteChanged();
        ReprocessProjectCommand.NotifyCanExecuteChanged();
    }
}
