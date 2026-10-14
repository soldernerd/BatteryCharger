namespace BatteryCharger.Core.Instruments;

/// <summary>One voltage/current reading.</summary>
public readonly record struct Measurement(DateTimeOffset Time, double Volts, double Amps);
