using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using LocalTranscriber.Core.Configuration;
using LocalTranscriber.Core.Jobs;

namespace LocalTranscriber.Gui.Services;

/// <summary>
/// Source unique de la configuration pour la GUI : charge, expose et sauvegarde
/// l'<see cref="AppConfig"/> partage par toutes les pages. Le secret (token HF)
/// est ecrit dans config.local.json par <see cref="ConfigStore.Save"/>.
/// </summary>
public sealed class SettingsService
{
    public AppConfig Config { get; private set; }
    public ObservableCollection<ProjectConfig> Projects { get; }

    /// <summary>Emis apres un rechargement (les pages rafraichissent leurs liaisons).</summary>
    public event Action? Reloaded;

    public SettingsService()
    {
        Config = ConfigStore.Load();
        Projects = new ObservableCollection<ProjectConfig>(Config.Projects);
    }

    public void Save()
    {
        Config.Projects = Projects.ToList();
        ConfigStore.Save(Config);
    }

    public void Reload()
    {
        Config = ConfigStore.Load();
        Projects.Clear();
        foreach (var p in Config.Projects) Projects.Add(p);
        Reloaded?.Invoke();
    }

    /// <summary>Empile une commande pour le service (retraiter un fichier / un projet).</summary>
    public void EnqueueCommand(string type, string payload)
    {
        var db = Path.Combine(ConfigStore.ExpandPath(Config.DataDir), "commands.db");
        new CommandStore(db).Enqueue(type, payload);
    }
}
