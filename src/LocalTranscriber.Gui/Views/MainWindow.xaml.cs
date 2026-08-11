using System.ComponentModel;
using System.Windows;
using LocalTranscriber.Gui;
using LocalTranscriber.Gui.ViewModels;

namespace LocalTranscriber.Gui.Views;

public partial class MainWindow : Window
{
    // En dessous de cette largeur de fenêtre, le rail de navigation se réduit à ses icônes.
    private const double RailCollapseThreshold = 1000;

    private bool _reallyClose;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    /// <summary>Réduit le rail de navigation (icônes seules) quand la fenêtre devient étroite.</summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;
        var collapsed = ActualWidth < RailCollapseThreshold;
        NavColumn.Width = new GridLength(collapsed ? 64 : 224);
        if (DataContext is ShellViewModel vm)
            vm.IsRailCollapsed = collapsed;
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

    // Les actions tray passent par les commandes du ServiceViewModel (singleton, partagé avec la
    // page) : exécution non bloquante (le VM déporte l'attente worker en tâche de fond), gating
    // IsBusy/CanExecute commun, et rafraîchissement de l'état affiché — plus d'appel bloquant
    // direct à WindowsServiceControl sur le thread UI, ni de désynchronisation d'état.
    private void Tray_StartService(object sender, RoutedEventArgs e) =>
        TrayWorkerCommand(vm => vm.StartServiceCommand);

    private void Tray_StopService(object sender, RoutedEventArgs e) =>
        TrayWorkerCommand(vm => vm.StopServiceCommand);

    private void Tray_Quit(object sender, RoutedEventArgs e)
    {
        _reallyClose = true;
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    private static void TrayWorkerCommand(
        Func<ServiceViewModel, System.Windows.Input.ICommand> pick
    )
    {
        try
        {
            if (App.Services?.GetService(typeof(ServiceViewModel)) is not ServiceViewModel vm)
                return;
            var command = pick(vm);
            if (command.CanExecute(null))
                command.Execute(null);
        }
        catch
        { /* geste tray best-effort */
        }
    }
}
