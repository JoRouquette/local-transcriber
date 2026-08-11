using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

        // Filet de securite global : rien ne doit crasher l'appli sans laisser de trace.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            Services.GetRequiredService<ThemeService>().Initialize();

            var window = Services.GetRequiredService<MainWindow>();
            window.DataContext = Services.GetRequiredService<ShellViewModel>();
            window.Show();

            // Réveil depuis un second lancement : on ramène la fenêtre existante au premier plan.
            SingleInstanceGuard.StartActivationListener(() =>
                Dispatcher.Invoke(ActivateMainWindow)
            );

            EnsureWorkerRunning();
        }
        catch (Exception ex)
        {
            // Echec de bootstrap : on trace et on previent avant un arret propre.
            LogCrash("Bootstrap", ex);
            MessageBox.Show(
                "Le démarrage a échoué : "
                    + ex.Message
                    + "\n\nUn journal a été écrit dans "
                    + CrashLogPath
                    + ".",
                "LocalTranscriber",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Shutdown(-1);
        }
    }

    /// <summary>Restaure et met au premier plan la fenêtre principale (réveil d'instance unique).</summary>
    private void ActivateMainWindow()
    {
        var window = MainWindow;
        if (window is null)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e
    )
    {
        LogCrash("Dispatcher", e.Exception);
        // Cas recuperable : on evite le crash et on informe l'utilisateur sans le bloquer.
        TryNotify("Une erreur est survenue : " + e.Exception.Message);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        LogCrash("AppDomain", e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Task", e.Exception);
        e.SetObserved();
    }

    /// <summary>Affiche un snackbar si la file est disponible ; ne leve jamais.</summary>
    private static void TryNotify(string message)
    {
        try
        {
            Services?.GetService<ISnackbarMessageQueue>()?.Enqueue(message);
        }
        catch
        { /* la notification ne doit jamais aggraver la situation */
        }
    }

    /// <summary>
    /// Au démarrage, si le worker de fond est installé mais arrêté, on le (re)lance. Utile après
    /// une mise à jour (le worker est arrêté pour libérer `current\`) ou un arrêt manuel. Ne lève jamais.
    /// </summary>
    private static void EnsureWorkerRunning()
    {
        try
        {
            if (WindowsServiceControl.QueryState() == WorkerState.Stopped)
                WindowsServiceControl.Start();
        }
        catch (Exception ex)
        {
            LogCrash("EnsureWorkerRunning", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Si une mise à jour est programmée pour la fermeture, on arrête le worker maintenant
        // pour que le remplacement Velopack (post-sortie du processus) trouve `current\` libre.
        try
        {
            Services?.GetService<UpdateService>()?.OnAppExit();
        }
        catch (Exception ex)
        {
            LogCrash("OnExit", ex);
        }
        base.OnExit(e);
    }

    private static string CrashLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalTranscriberData",
            "gui-crash.log"
        );

    /// <summary>Journalise une erreur (append, horodatee). Ne leve jamais.</summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = CrashLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch
        { /* dernier rempart : on ne peut rien faire de plus */
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISnackbarMessageQueue>(_ => new SnackbarMessageQueue(
            TimeSpan.FromSeconds(3)
        ));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<UpdateService>();

        services.AddSingleton<GeneralViewModel>();
        services.AddSingleton<ProjectsViewModel>();
        services.AddSingleton<ServiceViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<MainWindow>();
    }
}
