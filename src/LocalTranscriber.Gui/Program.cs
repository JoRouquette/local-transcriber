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
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
