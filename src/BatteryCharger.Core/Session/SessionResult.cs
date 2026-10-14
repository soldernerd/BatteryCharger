namespace BatteryCharger.Core.Session;

public enum EndReason { Completed, CompletedWithWarning, UserStopped, Fault }

/// <summary>Outcome of a session.</summary>
public sealed record SessionResult(
    EndReason Reason,
    string Message,
    TimeSpan Duration,
    double AhDelivered,
    double WhDelivered,
    double FinalVoltage,
    string? LogFilePath);
