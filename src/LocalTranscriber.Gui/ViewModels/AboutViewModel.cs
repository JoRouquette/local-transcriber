using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalTranscriber.Gui.Services;

namespace LocalTranscriber.Gui.ViewModels;

/// <summary>Page À propos : version, endpoint MCP, lien du dépôt.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public AboutViewModel(SettingsService settings) => _settings = settings;

    public string Version => typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string McpEndpoint => $"http://127.0.0.1:{_settings.Config.McpPort}/mcp";
    public string RepositoryUrl => "https://github.com/JoRouquette/local-transcriber";

    [RelayCommand] private void OpenRepository() => OpenUrl(RepositoryUrl);
    [RelayCommand] private void OpenMcp() => OpenUrl(McpEndpoint);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
