using Microsoft.UI.Xaml;

namespace BatteryCharger.App.Services;

/// <summary>Gives services access to the main window handle, XamlRoot and dispatcher.</summary>
public sealed class WindowContext
{
    public nint Hwnd { get; set; }
    public Window? Window { get; set; }
    public XamlRoot? XamlRoot { get; set; }
}
