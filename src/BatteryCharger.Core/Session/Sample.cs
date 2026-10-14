namespace BatteryCharger.Core.Session;

/// <summary>One processed measurement tick of a session.</summary>
public sealed record Sample(
    DateTimeOffset Time,
    TimeSpan Elapsed,
    ChargeState State,
    string Stage,
    double Volts,
    double Amps,
    double Ah,
    double Wh,
    double VoltageSet,
    double CurrentLimit,
    PsuMode Mode,
    string? Event = null)
{
    public double Watts => Volts * Amps;
}
