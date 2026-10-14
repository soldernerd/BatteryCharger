using Microsoft.UI.Xaml;

namespace BatteryCharger.App.Services;

/// <summary>Applies the user's theme override to the window content.</summary>
public sealed class ThemeService(WindowContext window, SettingsService settings)
{
    public void Apply()
    {
        if (window.Window?.Content is not FrameworkElement root) return;
        root.RequestedTheme = settings.Current.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
