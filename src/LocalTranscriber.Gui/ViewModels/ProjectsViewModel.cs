using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Gui.Services;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Page Projets : sous-dossiers avec réglages spécifiques.</summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public ProjectsViewModel(SettingsService settings) => _settings = settings;

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

    partial void OnSelectedProjectChanged(ProjectConfig? value) => RemoveProjectCommand.NotifyCanExecuteChanged();
}
