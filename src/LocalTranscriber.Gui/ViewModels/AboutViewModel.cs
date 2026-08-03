using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Gui.Services;
using MaterialDesignThemes.Wpf;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Page À propos : version, endpoint MCP, lien du dépôt, mises à jour.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly UpdateService _updates;
    private readonly ISnackbarMessageQueue _snackbar;

    public AboutViewModel(
        SettingsService settings,
        UpdateService updates,
        ISnackbarMessageQueue snackbar
    )
    {
        _settings = settings;
        _updates = updates;
        _snackbar = snackbar;
        _autoInstallUpdates = settings.Config.AutoInstallUpdates;

        // Vérification automatique au lancement (en tâche de fond).
        _ = CheckForUpdatesAsync(launch: true);
    }

    public string Version =>
        typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string McpEndpoint => $"http://127.0.0.1:{_settings.Config.McpPort}/mcp";
    public string RepositoryUrl => "https://github.com/JoRouquette/local-transcriber";

    // ---- Mises à jour ----
    [ObservableProperty]
    private bool _autoInstallUpdates;

    [ObservableProperty]
    private string _updateStatus = "Vérification des mises à jour…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotChecking))]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _updateReady;

    public bool IsNotChecking => !IsCheckingUpdate;

    partial void OnAutoInstallUpdatesChanged(bool value)
    {
        _settings.Config.AutoInstallUpdates = value;
        _settings.Save();
    }

    [RelayCommand]
    private async Task CheckForUpdates() => await CheckForUpdatesAsync(launch: false);

    private async Task CheckForUpdatesAsync(bool launch)
    {
        if (IsCheckingUpdate)
            return;
        IsCheckingUpdate = true;
        UpdateReady = false;
        UpdateStatus = "Recherche de mises à jour…";
        try
        {
            if (!_updates.IsInstalled)
            {
                UpdateStatus =
                    $"Version {Version} — mode développement (mises à jour désactivées).";
                return;
            }

            var version = await Task.Run(_updates.CheckAndDownloadAsync);
            if (version is null)
            {
                UpdateStatus = $"Version {Version} — vous êtes à jour.";
                return;
            }

            if (AutoInstallUpdates)
            {
                _updates.ApplyOnExit();
                UpdateStatus =
                    $"Mise à jour {version} prête — elle s'installera à la fermeture de l'application.";
                _snackbar.Enqueue($"Mise à jour {version} prête : installée à la fermeture.");
            }
            else
            {
                UpdateReady = true;
                UpdateStatus = $"Mise à jour {version} disponible.";
                if (!launch)
                    _snackbar.Enqueue(
                        $"Mise à jour {version} disponible : « Installer et redémarrer »."
                    );
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = "Échec de la vérification des mises à jour.";
            if (!launch)
                _snackbar.Enqueue("Mise à jour : " + ex.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (UpdateReady)
            _updates.ApplyAndRestart();
    }

    [RelayCommand]
    private void OpenRepository() => OpenUrl(RepositoryUrl);

    [RelayCommand]
    private void OpenMcp() => OpenUrl(McpEndpoint);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        { /* ignore */
        }
    }
}
