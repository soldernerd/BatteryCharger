using System.Collections.ObjectModel;
using BatteryCharger.App.ViewModels;
using BatteryCharger.Instruments.Discovery;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BatteryCharger.App.Services;

/// <summary>
/// Finds the power supplies and owns one independent <see cref="SupplyViewModel"/> (a tab) per supported supply.
/// Instruments that are already connected are never re-identified, so a refresh cannot disturb a running charge.
/// </summary>
public sealed partial class SupplyRegistry : ObservableObject
{
    public const string InstrumentsRootTitle = "Charger";

    private readonly SettingsService _settings;
    private readonly VisaService _visa;
    private readonly DialogService _dialogs;
    private readonly SleepInhibitor _sleep;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<SupplyRegistry> _log;

    public SupplyRegistry(SettingsService settings, VisaService visa, DialogService dialogs, SleepInhibitor sleep, ILoggerFactory loggers)
    {
        _settings = settings;
        _visa = visa;
        _dialogs = dialogs;
        _sleep = sleep;
        _loggers = loggers;
        _log = loggers.CreateLogger<SupplyRegistry>();
    }

    /// <summary>The tabs: every supported supply that was found (plus the simulator in developer mode).</summary>
    public ObservableCollection<SupplyViewModel> Supplies { get; } = [];

    /// <summary>Everything the search found, supported or not, for the overview page.</summary>
    public ObservableCollection<SupplyItem> Found { get; } = [];

    /// <summary>The tab currently shown (used by the SCPI console).</summary>
    public SupplyViewModel? Selected { get; set; }

    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _discoveryStatus = "";

    public bool VisaAvailable => _visa.IsAvailable;
    public bool AnyActive => Supplies.Any(s => s.IsSessionActive);

    // ---------------------------------------------------------------- discovery

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RefreshAsync(CancellationToken ct)
    {
        EnsureSimulator();
        if (!_visa.IsAvailable)
        {
            DiscoveryStatus = "VISA is not available.";
            return;
        }
        IsDiscovering = true;
        DiscoveryStatus = "Searching for instruments...";
        var owned = Supplies.Where(s => !s.IsSimulator && (s.IsConnected || s.IsBusy)).Select(s => s.Resource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<VisaResourceInfo>();
        try
        {
            var progress = new Progress<VisaResourceInfo>(info =>
            {
                results.Add(info);
                _log.LogInformation("Discovery: {Display} (supported: {Supported})", info.Display, info.Supported);
                DiscoveryStatus = $"Found {results.Count}: {info.Display}";
                Apply(results, owned, removeMissing: false);      // usable straight away, not only after the slow LAN check
            });
            await new InstrumentDiscovery(_visa.Manager).DiscoverAsync(_settings.Current.LanInstruments, TimeSpan.FromSeconds(2), progress, ct, owned);
            Apply(results, owned, removeMissing: true);
            var supported = results.Count(r => r.Supported) + owned.Count;
            DiscoveryStatus = supported == 0
                ? "No supported power supply found. For LAN instruments use \"Add LAN instrument\"."
                : $"{supported} supported power supply(ies) found.";
        }
        catch (OperationCanceledException)
        {
            Apply(results, owned, removeMissing: false);
            DiscoveryStatus = "Search cancelled.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discovery failed");
            DiscoveryStatus = "Search failed: " + ex.Message;
        }
        finally { IsDiscovering = false; }
    }

    [RelayCommand]
    private async Task AddLanAsync()
    {
        var text = await _dialogs.PromptLanInstrumentAsync();
        if (text is null) return;
        var resource = InstrumentDiscovery.ToResourceString(text);
        if (!_settings.Current.LanInstruments.Contains(resource, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Current.LanInstruments.Add(resource);
            _settings.Save();
        }
        IsDiscovering = true;
        DiscoveryStatus = $"Contacting {resource}...";
        try
        {
            var info = await new InstrumentDiscovery(_visa.Manager).IdentifyAsync(resource, true, TimeSpan.FromSeconds(2), CancellationToken.None);
            var existing = Found.FirstOrDefault(f => f.Resource.Equals(resource, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) Found.Remove(existing);
            Found.Add(ToItem(info));
            if (info.Supported) AddSupply(info);
            DiscoveryStatus = info.Supported ? $"Added {info.Display}" : $"Added, but: {info.Reason}";
        }
        finally { IsDiscovering = false; }
    }

    private void Apply(List<VisaResourceInfo> results, HashSet<string> owned, bool removeMissing)
    {
        Found.Clear();
        foreach (var s in Supplies.Where(s => !s.IsSimulator && owned.Contains(s.Resource)))
            Found.Add(new SupplyItem(s.IdentityText + "  " + s.Resource, s.Resource, true, false));
        foreach (var r in results) Found.Add(ToItem(r));
        foreach (var r in results.Where(r => r.Supported)) AddSupply(r);

        if (!removeMissing) return;
        var present = results.Select(r => r.Resource).Concat(owned).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in Supplies.Where(s => !s.IsSimulator && !present.Contains(s.Resource) && !s.IsConnected && !s.IsBusy).ToList())
            Supplies.Remove(gone);
        RenameForUniqueness();
    }

    private static SupplyItem ToItem(VisaResourceInfo f) =>
        new(f.Supported ? f.Display : $"{f.Display}  [not a supported power supply]", f.Resource, f.Supported, false);

    // ---------------------------------------------------------------- tabs

    private void AddSupply(VisaResourceInfo info)
    {
        if (info.Identity is null) return;
        if (Supplies.Any(s => s.Resource.Equals(info.Resource, StringComparison.OrdinalIgnoreCase))) return;
        // The same instrument can show up under two resources (e.g. USB and LAN): one tab per serial number.
        if (info.Identity.Serial.Length > 0 && Supplies.Any(s => !s.IsSimulator && s.Serial == info.Identity.Serial)) return;
        var vm = new SupplyViewModel(info.Resource, info.Identity.Model, info.Identity.Serial, false, info.Identity.Firmware,
            _settings, _visa, _dialogs, _sleep, _loggers);
        InsertSorted(vm);
        RenameForUniqueness();
    }

    private void InsertSorted(SupplyViewModel vm)
    {
        var i = 0;
        while (i < Supplies.Count && Compare(Supplies[i], vm) <= 0) i++;
        Supplies.Insert(i, vm);
    }

    private static int Compare(SupplyViewModel a, SupplyViewModel b)
    {
        if (a.IsSimulator != b.IsSimulator) return a.IsSimulator ? -1 : 1;
        var c = string.Compare(a.Model, b.Model, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.Compare(a.Serial, b.Serial, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two supplies of the same model get the end of their serial number in the label.</summary>
    private void RenameForUniqueness()
    {
        foreach (var group in Supplies.GroupBy(s => s.Model))
            foreach (var s in group)
                s.BaseNavTitle = s.IsSimulator ? "Simulated supply"
                    : group.Count() > 1 && s.Serial.Length > 0 ? $"{s.Model} ...{(s.Serial.Length > 4 ? s.Serial[^4..] : s.Serial)}" : s.Model;
    }

    private void EnsureSimulator()
    {
        var sim = Supplies.FirstOrDefault(s => s.IsSimulator);
        if (_settings.Current.DeveloperMode && sim is null)
        {
            var vm = new SupplyViewModel(InstrumentService.SimulatorResource, "Simulated supply", "SIM", true, "1.0",
                _settings, _visa, _dialogs, _sleep, _loggers);
            InsertSorted(vm);
            RenameForUniqueness();
        }
        else if (!_settings.Current.DeveloperMode && sim is not null && !sim.IsConnected && !sim.IsBusy)
            Supplies.Remove(sim);
    }

    /// <summary>Re-reads settings that can change on the Settings page (developer mode adds the simulated supply).</summary>
    public void OnSettingsChanged() => EnsureSimulator();

    // ---------------------------------------------------------------- shutdown

    /// <summary>Stops every running charge and closes all instrument sessions. Returns false when the user cancelled.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (AnyActive)
        {
            var names = string.Join(", ", Supplies.Where(s => s.IsSessionActive).Select(s => s.BaseNavTitle));
            var stop = await _dialogs.ConfirmAsync("Charging in progress",
                $"A charge is running on {names}. Closing the application will stop charging and switch the power supply output off.",
                "Stop charging and exit", "Cancel");
            if (!stop) return false;
            foreach (var s in Supplies.Where(s => s.IsSessionActive).ToList()) await s.StopForShutdownAsync();
        }
        foreach (var s in Supplies) await s.DisconnectAsync();
        return true;
    }
}
