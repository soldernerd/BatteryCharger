using BatteryCharger.Core.Instruments;

namespace BatteryCharger.Instruments.Drivers;

/// <summary>A connected Keysight/Agilent supply: identity, channels and the shared link.</summary>
public sealed class ScpiPowerSupply : IPowerSupply
{
    private readonly ScpiLink _link;

    public ScpiPowerSupply(string resource, InstrumentIdentity identity, ScpiLink link, IReadOnlyList<IPowerSupplyChannel> channels)
    {
        Resource = resource;
        Identity = identity;
        _link = link;
        Channels = channels;
    }

    public string Resource { get; }
    public InstrumentIdentity Identity { get; }
    public IReadOnlyList<IPowerSupplyChannel> Channels { get; }
    public ScpiLink Link => _link;

    public ValueTask DisposeAsync() => _link.DisposeAsync();
}
