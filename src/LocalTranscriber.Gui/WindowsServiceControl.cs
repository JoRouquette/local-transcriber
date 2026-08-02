using System;
using System.Diagnostics;
using System.IO;

namespace LocalTranscriber.Gui;

/// <summary>
/// Pilotage du service Windows LocalTranscriber via sc.exe. Les actions qui modifient
/// le service (install/start/stop/remove) sont lancees en mode eleve (runas).
/// </summary>
public static class WindowsServiceControl
{
    public const string ServiceName = "LocalTranscriber";

    public static string? ServiceExePath =>
        FindSibling("LocalTranscriber.Service.exe");

    private static string? FindSibling(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, fileName);
        if (File.Exists(candidate)) return candidate;
        // En dev : ..\LocalTranscriber.Service\bin\...
        return null;
    }

    private static void RunElevated(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + arguments,
            UseShellExecute = true,
            Verb = "runas", // declenche l'UAC
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
    }

    public static void Install()
    {
        var exe = ServiceExePath ?? throw new FileNotFoundException("LocalTranscriber.Service.exe introuvable.");
        RunElevated($"sc create {ServiceName} binPath= \"{exe}\" start= auto & sc description {ServiceName} \"Transcription et diarisation locales\"");
    }

    public static void Start() => RunElevated($"sc start {ServiceName}");
    public static void Stop() => RunElevated($"sc stop {ServiceName}");
    public static void Uninstall() => RunElevated($"sc stop {ServiceName} & sc delete {ServiceName}");

    public static string QueryStatus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {ServiceName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (output.Contains("RUNNING")) return "En cours d'execution";
            if (output.Contains("STOPPED")) return "Arrete";
            if (output.Contains("does not exist") || output.Contains("1060")) return "Non installe";
            return "Etat inconnu";
        }
        catch (Exception ex)
        {
            return "Erreur : " + ex.Message;
        }
    }
}
