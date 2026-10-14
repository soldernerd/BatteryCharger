namespace BatteryCharger.Core;

/// <summary>Single source of truth for the application version (read by the CI release job).</summary>
public static class AppVersion
{
    public const int Major = 0;
    public const int Minor = 1;
    public const int Patch = 6;

    public static string Full => $"{Major}.{Minor}.{Patch}";
    public static string Display => $"v{Full}";
}
