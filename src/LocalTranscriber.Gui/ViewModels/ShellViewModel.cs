using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Gui.Services;
using MaterialDesignThemes.Wpf;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Un élément de la barre de navigation latérale.</summary>
public sealed record NavItem(string Title, PackIconKind Icon, object Page);

/// <summary>ViewModel de la coquille : navigation, sauvegarde globale, thème, snackbar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly GeneralViewModel _general;

    public ISnackbarMessageQueue MessageQueue { get; }
    public ObservableCollection<NavItem> NavItems { get; }

    public ShellViewModel(
        SettingsService settings,
        ThemeService theme,
        ISnackbarMessageQueue messageQueue,
        GeneralViewModel general,
        ProjectsViewModel projects,
        ServiceViewModel service,
        AboutViewModel about
    )
    {
        _settings = settings;
        _theme = theme;
        _general = general;
        MessageQueue = messageQueue;

        NavItems = new ObservableCollection<NavItem>
        {
            new("Général", PackIconKind.Tune, general),
            new("Projets", PackIconKind.FolderMultipleOutline, projects),
            new("Traitements et fichiers", PackIconKind.Server, service),
            new("À propos", PackIconKind.InformationOutline, about),
        };
        _selectedNavItem = NavItems[0];
        _isDark = _theme.IsDark;
    }

    [ObservableProperty]
    private NavItem _selectedNavItem;

    [ObservableProperty]
    private bool _isDark;

    /// <summary>
    /// Rail de navigation réduit à ses icônes (piloté par la largeur de la fenêtre, voir
    /// MainWindow). <see cref="IsRailExpanded"/> est l'inverse, pour la visibilité des libellés.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRailExpanded))]
    private bool _isRailCollapsed;

    public bool IsRailExpanded => !IsRailCollapsed;

    public object CurrentPage => SelectedNavItem.Page;

    partial void OnSelectedNavItemChanged(NavItem value) => OnPropertyChanged(nameof(CurrentPage));

    [RelayCommand]
    private void Save()
    {
        if (!_general.IsValid)
        {
            MessageQueue.Enqueue("Corrigez les champs en erreur avant d'enregistrer.");
            return;
        }
        _settings.Save();
        MessageQueue.Enqueue(
            "Configuration enregistrée. Le service la rechargera automatiquement."
        );
    }

    [RelayCommand]
    private void Reload()
    {
        _settings.Reload();
        MessageQueue.Enqueue("Configuration rechargée.");
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        _theme.Toggle();
        IsDark = _theme.IsDark;
    }
}
