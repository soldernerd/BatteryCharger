using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Instruments.Simulation;
using BatteryCharger.Instruments.Drivers;
using BatteryCharger.Instruments.Discovery;
using Microsoft.Extensions.Logging;

namespace BatteryCharger.App.Services;

/// <summary>The currently connected power supply, shared by the Charger page and the SCPI console.</summary>
public sealed class InstrumentService
{
    public const string SimulatorResource = "SIMULATED::PSU";
    /// <summary>Time compression of the simulated supply (1 s of real time = this many seconds of charging).</summary>
    public const double SimulatorTimeScale = 60;

    private readonly VisaService _visa;
    private readonly SettingsService _settings;
    private readonly ILogger<InstrumentService> _log;

    public InstrumentService(VisaService visa, SettingsService settings, ILogger<InstrumentService> log)
    {
        _visa = visa;
        _settings = settings;
        _log = log;
    }

    public IPowerSupply? Current { get; private set; }
    public ScpiLink? Link => (Current as ScpiPowerSupply)?.Link;
    public event EventHandler? Changed;

    public Task<IReadOnlyList<VisaResourceInfo>> DiscoverAsync(IProgress<VisaResourceInfo>? progress, CancellationToken ct)
    {
        var d = new InstrumentDiscovery(_visa.Manager);
        return d.DiscoverAsync(_settings.Current.LanInstruments, TimeSpan.FromSeconds(2), progress, ct);
    }

    public Task<VisaResourceInfo> IdentifyAsync(string resource, CancellationToken ct) =>
        new InstrumentDiscovery(_visa.Manager).IdentifyAsync(resource, true, TimeSpan.FromSeconds(2), ct);

    public async Task<IPowerSupply> ConnectAsync(string resource, SimulatedBattery? simulatedBattery, CancellationToken ct)
    {
        await DisconnectAsync();
        IPowerSupply psu;
        if (resource == SimulatorResource)
            psu = new FakePowerSupply(simulatedBattery ?? SimulatedBattery.LiIon(1, 2.0, 0.3), timeScale: SimulatorTimeScale);
        else
            psu = await DriverFactory.ConnectAsync(_visa.Manager, resource, _settings.Current.VisaTimeoutMs, _visa.OnTraffic, ct);
        _log.LogInformation("Connected to {Idn} at {Resource}", psu.Identity, resource);
        Current = psu;
        Changed?.Invoke(this, EventArgs.Empty);
        return psu;
    }

    public async Task DisconnectAsync()
    {
        var psu = Current;
        Current = null;
        if (psu is null) return;
        try { await psu.DisposeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Error while disconnecting"); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
