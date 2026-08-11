using System;
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
}
