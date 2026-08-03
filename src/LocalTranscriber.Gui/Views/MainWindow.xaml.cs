using System.ComponentModel;
using System.Windows;
using LocalTranscriber.Gui;

namespace LocalTranscriber.Gui.Views;

public partial class MainWindow : Window
{
    private bool _reallyClose;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Réduire envoie l'application dans le tray.
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Fermer garde l'app active dans le tray ; « Quitter » ferme réellement.
        if (_reallyClose)
            return;
        e.Cancel = true;
        Hide();
    }

    private void Tray_ShowWindow(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Tray_StartService(object sender, RoutedEventArgs e) =>
        Safe(WindowsServiceControl.Start);

    private void Tray_StopService(object sender, RoutedEventArgs e) =>
        Safe(WindowsServiceControl.Stop);

    private void Tray_Quit(object sender, RoutedEventArgs e)
    {
        _reallyClose = true;
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch
        { /* geste tray best-effort */
        }
    }
}
