using BatteryCharger.Core.Instruments;

namespace BatteryCharger.App.ViewModels;

/// <summary>Entry of the power supply drop-down.</summary>
public sealed record SupplyItem(string Display, string Resource, bool Supported, bool IsSimulator)
{
    /// <summary>Unsupported instruments are shown greyed out.</summary>
    public double Dim => Supported ? 1.0 : 0.5;
}

/// <summary>Entry of the channel drop-down.</summary>
public sealed record ChannelItem(string Name, IPowerSupplyChannel Channel);
