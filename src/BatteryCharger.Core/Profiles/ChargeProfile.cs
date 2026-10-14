using BatteryCharger.Core.Batteries;

namespace BatteryCharger.Core.Profiles;

/// <summary>A fully derived charge profile: ordered stages, safety envelope, warnings and blocking errors.</summary>
public sealed class ChargeProfile
{
    public required BatteryDescriptor Battery { get; init; }
    public required ProfileOptions Options { get; init; }
    public required IReadOnlyList<IChargeStage> Stages { get; init; }
    /// <summary>Pack voltage the supply regulates to (CV/absorption target, or NiMH/NiCd ceiling).</summary>
    public double TargetPackVolts { get; init; }
    public double OvpVolts { get; init; }
    /// <summary>Total OVP margin above the target for the pack.</summary>
    public double OvpMarginPackVolts { get; init; }
    public double MainCurrentAmps { get; init; }
    public double TerminationAmps { get; init; }
    public double MaxChargeAh { get; init; }
    public double RefuseBelowPackVolts { get; init; }
    public double? PrechargeBelowPackVolts { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool IsValid => Errors.Count == 0;

    /// <summary>Human readable stage and safety lines for the profile summary card.</summary>
    public IReadOnlyList<string> SummaryLines
    {
        get
        {
            var lines = Stages.Select(s => s.Description).ToList();
            if (IsValid)
            {
                lines.Add($"Safety: supply OVP {OvpVolts:0.00} V, software over-current fault at {1.10 * MainCurrentAmps + 0.02:0.###} A");
                lines.Add($"Safety: stop at {MaxChargeAh:0.###} Ah delivered ({Options.MaxChargeFactor:0.##} x capacity)");
            }
            return lines;
        }
    }

    /// <summary>Picks the stage to start with given the measured battery voltage (skips pre-charge / CC as appropriate).</summary>
    public int InitialStageIndex(double batteryVolts)
    {
        var pre = Stages.ToList().FindIndex(s => s is PrechargeStage);
        if (pre >= 0 && PrechargeBelowPackVolts is { } th && batteryVolts < th) return pre;
        if (Battery.Technology is not (BatteryTechnology.NiMH or BatteryTechnology.NiCd) && batteryVolts >= 0.99 * TargetPackVolts)
        {
            var cv = Stages.ToList().FindIndex(s => s is ConstantVoltageStage);
            if (cv >= 0) return cv;
        }
        return Stages.ToList().FindIndex(s => s is not PrechargeStage);
    }
}
