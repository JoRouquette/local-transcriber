using System.Windows.Controls;

namespace LocalTranscriber.Gui.Views.Pages;

public partial class ServicePage : UserControl
{
    public ServicePage() => InitializeComponent();

    /// <summary>Garde le journal live collé au bas quand de nouvelles lignes arrivent.</summary>
    private void LiveLog_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.ScrollToEnd();
    }
}
