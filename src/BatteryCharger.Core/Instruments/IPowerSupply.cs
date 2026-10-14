namespace BatteryCharger.Core.Instruments;

/// <summary>A connected power supply (one or more channels).</summary>
public interface IPowerSupply : IAsyncDisposable
{
    string Resource { get; }
    InstrumentIdentity Identity { get; }
    IReadOnlyList<IPowerSupplyChannel> Channels { get; }
}
