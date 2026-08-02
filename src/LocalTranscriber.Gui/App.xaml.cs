using System.Windows;
using LocalTranscriber.Gui.Services;
using LocalTranscriber.Gui.ViewModels;
using LocalTranscriber.Gui.Views;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace LocalTranscriber.Gui;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        Services.GetRequiredService<ThemeService>().Initialize();

        var window = Services.GetRequiredService<MainWindow>();
        window.DataContext = Services.GetRequiredService<ShellViewModel>();
        window.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISnackbarMessageQueue>(_ => new SnackbarMessageQueue(TimeSpan.FromSeconds(3)));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();

        services.AddSingleton<GeneralViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<ServiceViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<MainWindow>();
    }
}
