using BatteryCharger.Core.Settings;
using Microsoft.Extensions.Logging;

namespace BatteryCharger.App.Services;

/// <summary>Holds the loaded <see cref="AppSettings"/> and persists them.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly ILogger<SettingsService> _log;

    public SettingsService(SettingsStore store, ILogger<SettingsService> log)
    {
        _store = store;
        _log = log;
        Current = store.Load();
    }

    public AppSettings Current { get; }
    public string FilePath => _store.Path;

    public void Save()
    {
        try { _store.Save(Current); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save settings"); }
    }
}
