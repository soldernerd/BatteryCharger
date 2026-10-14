using Microsoft.UI.Xaml;

namespace BatteryCharger.App.Helpers;

/// <summary>Static helpers for x:Bind function bindings.</summary>
public static class Fn
{
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotVis(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
}
