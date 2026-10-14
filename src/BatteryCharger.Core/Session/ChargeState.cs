namespace BatteryCharger.Core.Session;

/// <summary>Lifecycle state of a charge session.</summary>
public enum ChargeState
{
    Idle, Preflight, Precharge, ConstantCurrent, ConstantVoltage, Float,
    Completed, Stopped, Faulted
}

public static class ChargeStateExtensions
{
    public static bool IsTerminal(this ChargeState s) => s is ChargeState.Completed or ChargeState.Stopped or ChargeState.Faulted;
}
