using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace LocalTranscriber.Gui;

/// <summary>Etat du worker de fond, decouple de tout libelle localise affiche a l'UI.</summary>
public enum WorkerState
{
    Running,
    Stopped,
    NotInstalled,
    Error,
}

/// <summary>
/// Pilote le worker en tache de fond via le Planificateur de taches Windows, dans la
/// SESSION DE L'UTILISATEUR (declencheur « a l'ouverture de session »). On evite ainsi le
/// mode service LocalSystem / session 0, que les EDR (ex. SentinelOne) neutralisent quand
/// le binaire n'est pas signe par un editeur reconnu — le meme exe tourne sans probleme
/// en contexte utilisateur. Aucun droit administrateur requis (pas d'UAC).
///
/// Le nom de classe et l'API (Install/Start/Stop/QueryStatus) restent inchanges pour le
/// reste de la GUI ; seule l'implementation passe du service `sc` a une tache planifiee.
/// </summary>
public static class WindowsServiceControl
{
    public const string TaskName = "LocalTranscriber";
    private const string ProcessName = "LocalTranscriber.Service"; // sans .exe

    public static string? ServiceExePath => FindSibling("LocalTranscriber.Service.exe");

    private static string? FindSibling(string fileName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Cree (ou remplace) la tache planifiee et la demarre. Retourne vrai si le worker tourne.</summary>
    public static bool Install()
    {
        var exe =
            ServiceExePath
            ?? throw new FileNotFoundException("LocalTranscriber.Service.exe introuvable.");
        var user = WindowsIdentity.GetCurrent().Name; // DOMAINE\Utilisateur
        var xmlPath = Path.Combine(Path.GetTempPath(), "localtranscriber-task.xml");
        // schtasks /XML exige un fichier UTF-16.
        File.WriteAllText(xmlPath, BuildTaskXml(exe, user), new UnicodeEncoding());
        Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
        try
        {
            File.Delete(xmlPath);
        }
        catch
        { /* sans importance */
        }
        return Start();
    }

    // Delais d'attente : on VERIFIE l'etat cible plutot que de supposer un temps fixe. C'est la
    // cle contre le cote « aleatoire » : le process peut mettre plus ou moins de temps a
    // apparaitre/disparaitre selon la charge et le sidecar Python.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Lance la tache et ATTEND que le worker soit reellement en cours (poll, timeout). Si le
    /// planificateur refuse le demarrage parce qu'une instance residuelle est encore « en cours »
    /// (policy IgnoreNew), on force un /End puis on retente une fois. Retourne l'etat atteint.
    /// </summary>
    public static bool Start()
    {
        Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");
        if (WaitForState(s => s == WorkerState.Running, StartTimeout))
            return true;

        // Retry : on nettoie l'etat cote planificateur puis on relance.
        Run("schtasks.exe", $"/End /TN \"{TaskName}\"");
        Thread.Sleep(500);
        Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");
        return WaitForState(s => s == WorkerState.Running, StartTimeout);
    }

    /// <summary>
    /// Arrete le worker et son arbre de process (sidecar Python), puis ATTEND la disparition
    /// effective. On termine d'abord la tache cote planificateur (/End) pour liberer son etat
    /// d'instance — sinon un Start immediat serait ignore (IgnoreNew) — avant le taskkill /T qui
    /// garantit la mort de l'arbre complet. Retourne vrai si le worker n'est plus en cours.
    /// </summary>
    public static bool Stop()
    {
        // 1) Fin « propre » cote planificateur : libere l'etat d'instance de la tache.
        Run("schtasks.exe", $"/End /TN \"{TaskName}\"");
        // 2) Filet : on tue l'arbre (le sidecar Python est un enfant du worker).
        Run("taskkill.exe", "/F /T /IM LocalTranscriber.Service.exe");
        return WaitForState(s => s != WorkerState.Running, StopTimeout);
    }

    /// <summary>
    /// Redemarrage atomique : arret verifie puis demarrage verifie. Elimine la course entre le
    /// taskkill et le /Run qui rendait le redemarrage manuel aleatoire. Retourne l'etat final.
    /// </summary>
    public static bool Restart()
    {
        Stop();
        return Start();
    }

    /// <summary>Attend que l'etat du worker satisfasse <paramref name="predicate"/> (poll, timeout).</summary>
    private static bool WaitForState(Func<WorkerState, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(QueryState()))
                return true;
            Thread.Sleep(PollInterval);
        }
        return predicate(QueryState());
    }

    /// <summary>Arrete puis supprime la tache planifiee.</summary>
    public static void Uninstall()
    {
        Stop();
        Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
    }

    /// <summary>Etat du worker sous forme d'enum (logique metier, sans libelle localise).</summary>
    public static WorkerState QueryState()
    {
        try
        {
            if (Process.GetProcessesByName(ProcessName).Length > 0)
                return WorkerState.Running;
            return TaskExists() ? WorkerState.Stopped : WorkerState.NotInstalled;
        }
        catch
        {
            return WorkerState.Error;
        }
    }

    /// <summary>Libelle localise correspondant a un etat (pour affichage UI uniquement).</summary>
    public static string Describe(WorkerState state) =>
        state switch
        {
            WorkerState.Running => "En cours d'execution",
            WorkerState.Stopped => "Arrete",
            WorkerState.NotInstalled => "Non installe",
            _ => "Erreur",
        };

    /// <summary>Libelle localise de l'etat courant (conserve pour compatibilite d'appel).</summary>
    public static string QueryStatus() => Describe(QueryState());

    private static bool TaskExists() => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0;

    private static int Run(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        // Lecture non bloquante : on consomme stderr en async pendant qu'on lit stdout,
        // pour eviter tout interblocage si l'un des tampons de sortie se remplit.
        var errTask = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        errTask.GetAwaiter().GetResult();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string BuildTaskXml(string exePath, string user)
    {
        var workDir = Path.GetDirectoryName(exePath);
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>LocalTranscriber - worker de transcription en tache de fond (session utilisateur).</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exePath}</Command>
      <WorkingDirectory>{workDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
    }
}
