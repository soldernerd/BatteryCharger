namespace BatteryCharger.Core.Instruments;

/// <summary>State of a channel captured before the app touches it, used for restore and the "output is on" warning.</summary>
public sealed record ChannelSnapshot(
    bool OutputOn,
    double Volts,
    double AmpsLimit,
    double OvpVolts,
    bool OvpEnabled,
    bool OcpEnabled,
    string? ProtectionState = null,
    string? VoltageRange = null);
