using System.Collections.ObjectModel;
using BatteryCharger.App.Services;
using BatteryCharger.Core;
using BatteryCharger.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BatteryCharger.App.ViewModels;

/// <summary>Two-way view of <see cref="AppSettings"/>; every change is validated and saved immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly VisaService _visa;
    private readonly ThemeService _theme;
    private readonly SupplyRegistry _charger;
    private readonly WindowContext _window;

    public SettingsViewModel(SettingsService settings, VisaService visa, ThemeService theme, SupplyRegistry charger, WindowContext window)
    {
        _settings = settings;
        _visa = visa;
        _theme = theme;
        _charger = charger;
        _window = window;
        foreach (var l in settings.Current.LanInstruments) LanInstruments.Add(l);
    }

    private AppSettings S => _settings.Current;

    public double PollIntervalSeconds
    {
        get => S.PollIntervalSeconds;
        set
        {
            if (double.IsNaN(value)) return;
            S.PollIntervalSeconds = Math.Clamp(value, 0.5, 10);
            Commit();
        }
    }

    public int CsvSeparatorIndex
    {
        get => S.CsvSeparator == ";" ? 1 : 0;
        set { S.CsvSeparator = value == 1 ? ";" : ","; Commit(); }
    }

    public string LogFolder
    {
        get => S.EffectiveLogFolder;
        set { S.LogFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); Commit(); }
    }

    public bool RestorePreviousSetpoints
    {
        get => S.RestorePreviousSetpoints;
        set { S.RestorePreviousSetpoints = value; Commit(); }
    }

    public bool LogScpiTraffic
    {
        get => S.LogScpiTraffic;
        set { S.LogScpiTraffic = value; Commit(); }
    }

    public double VisaTimeoutMs
    {
        get => S.VisaTimeoutMs;
        set
        {
            if (double.IsNaN(value)) return;
            S.VisaTimeoutMs = (int)Math.Clamp(value, 500, 60_000);
            Commit();
        }
    }

    public int ThemeIndex
    {
        get => S.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        set
        {
            S.Theme = value switch { 1 => "Light", 2 => "Dark", _ => "System" };
            Commit();
            _theme.Apply();
        }
    }

    public bool DeveloperMode
    {
        get => S.DeveloperMode;
        set
        {
            S.DeveloperMode = value;
            Commit();
            _charger.OnSettingsChanged();
            DeveloperModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised so the shell can show or hide the SCPI console entry.</summary>
    public static event EventHandler? DeveloperModeChanged;

    public ObservableCollection<string> LanInstruments { get; } = [];

    public string VersionText => $"BatteryCharger {AppVersion.Display}";
    public string VisaText => _visa.IsAvailable ? "VISA library found (visa64.dll)" : (_visa.Problem ?? "VISA not available");
    public string SettingsPath => _settings.FilePath;
    public string DiagnosticsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryCharger", "diagnostics");

    private void Commit()
    {
        _settings.Save();
    }

    [RelayCommand]
    private void RemoveLan(string? resource)
    {
        if (resource is null) return;
        LanInstruments.Remove(resource);
        S.LanInstruments.RemoveAll(r => r.Equals(resource, StringComparison.OrdinalIgnoreCase));
        Commit();
    }

    [RelayCommand]
    private async Task BrowseLogFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _window.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        LogFolder = folder.Path;
        OnPropertyChanged(nameof(LogFolder));
    }
}
