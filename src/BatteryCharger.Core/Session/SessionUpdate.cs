namespace BatteryCharger.Core.Session;

/// <summary>Raised by the session for every sample and notable state change. Not marshalled to any UI thread.</summary>
public sealed record SessionUpdate(ChargeState State, string Stage, int StageIndex, Sample? Sample, string? Message, bool IsWarning);
