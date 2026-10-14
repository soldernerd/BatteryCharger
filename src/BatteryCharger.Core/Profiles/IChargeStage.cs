using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Profiles;

/// <summary>One stage of a charge profile. Stages keep per-run counters; <see cref="Reset"/> is called on entry.</summary>
public interface IChargeStage
{
    string Name { get; }
    ChargeState State { get; }
    /// <summary>Voltage setpoint (pack).</summary>
    double Volts { get; }
    /// <summary>Current limit.</summary>
    double Amps { get; }
    /// <summary>True for stages where safety checks for a disconnected battery apply (pre-charge, CC, bulk).</summary>
    bool IsConstantCurrentStage { get; }
    /// <summary>True for float (no charge limit, reduced logging rate).</summary>
    bool IsFloat { get; }
    /// <summary>Human readable description for the profile summary.</summary>
    string Description { get; }
    void Reset();
    StageDecision Evaluate(Sample sample, TimeSpan stageElapsed);
}
