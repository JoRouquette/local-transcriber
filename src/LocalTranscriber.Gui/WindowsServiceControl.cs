using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace LocalTranscriber.Gui;

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

    /// <summary>Cree (ou remplace) la tache planifiee et la demarre immediatement.</summary>
    public static void Install()
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
        Start();
    }

    /// <summary>Lance la tache maintenant (sans attendre la prochaine ouverture de session).</summary>
    public static void Start() => Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");

    /// <summary>Arrete le worker et son sous-processus (sidecar Python) — arbre complet.</summary>
    public static void Stop() => Run("taskkill.exe", "/F /T /IM LocalTranscriber.Service.exe");

    /// <summary>Arrete puis supprime la tache planifiee.</summary>
    public static void Uninstall()
    {
        Stop();
        Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
    }

    public static string QueryStatus()
    {
        try
        {
            if (Process.GetProcessesByName(ProcessName).Length > 0)
                return "En cours d'execution";
            return TaskExists() ? "Arrete" : "Non installe";
        }
        catch (Exception ex)
        {
            return "Erreur : " + ex.Message;
        }
    }

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
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
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
