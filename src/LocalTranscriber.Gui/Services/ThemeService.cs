using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace LocalTranscriber.Gui.Services;

/// <summary>
/// Applique le thème Material clair/sombre. Au démarrage, suit la préférence de
/// Windows ; l'utilisateur peut ensuite basculer manuellement.
/// </summary>
public sealed class ThemeService
{
    private readonly PaletteHelper _palette = new();

    public bool IsDark { get; private set; }

    public void Initialize() => Apply(SystemPrefersDark());

    public void Toggle() => Apply(!IsDark);

    public void Apply(bool dark)
    {
        IsDark = dark;
        var theme = _palette.GetTheme();
        theme.SetBaseTheme(dark ? BaseTheme.Dark : BaseTheme.Light);
        _palette.SetTheme(theme);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
            );
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
