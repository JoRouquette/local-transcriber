using System;
using System.IO;
using Velopack;

namespace LocalTranscriber.Gui;

/// <summary>
/// Point d'entrée explicite. Velopack DOIT s'exécuter en tout premier (gestion
/// installation/mise à jour/désinstallation) avant de démarrer l'application WPF.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Filet de crash TRES precoce : les handlers globaux d'App ne sont abonnes qu'une fois
        // dans OnStartup, donc une exception dans Velopack ou dans le parsing XAML de App
        // passerait inapercue (crash silencieux au 1er lancement). On trace ici en dernier ressort.
        try
        {
            // Velopack DOIT passer en premier (hooks install/update/uninstall lancent des process
            // transitoires). Le verrou d'instance unique vient APRÈS, pour ne pas les compter.
            VelopackApp.Build().Run();

            // Instance unique : si une GUI tourne déjà, on la réveille et on sort sans doublon.
            if (!SingleInstanceGuard.TryAcquire())
                return;

            try
            {
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
            finally
            {
                SingleInstanceGuard.Release();
            }
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            throw;
        }
    }

    private static void WriteCrash(Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalTranscriberData",
                "gui-crash.log"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Startup] {ex}{Environment.NewLine}"
            );
        }
        catch
        { /* dernier rempart : on ne peut rien faire de plus */
        }
    }
}
