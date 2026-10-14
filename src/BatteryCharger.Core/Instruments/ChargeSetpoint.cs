namespace BatteryCharger.Core.Instruments;

/// <summary>Setpoints applied before output-on.</summary>
public sealed record ChargeSetpoint(double Volts, double AmpsLimit, double OvpVolts);
