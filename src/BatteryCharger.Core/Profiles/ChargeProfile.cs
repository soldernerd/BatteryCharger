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

    /// <summary>
    /// For a battery below the refuse threshold that the user chose to try anyway: the same profile, but guaranteed to start
    /// with a gentle pre-charge (added for chemistries that have none, e.g. NiMH) that must lift the pack before the normal stages.
    /// </summary>
    public ChargeProfile WithRecoveryStage()
    {
        if (!IsValid || Stages.Any(s => s is PrechargeStage)) return this;
        var chem = CellChemistryInfo.For(Battery.Technology);
        var cells = Battery.CellsInSeries;
        var exit = 0.8 * chem.CellNominalV * cells;
        var amps = Math.Min(Options.PrechargeRateC * Battery.CapacityAh, MainCurrentAmps);
        var stage = new PrechargeStage(Stages[0].Volts, amps, exit, Options.PrechargeTimeout, 0.8 * chem.CellNominalV);
        return new ChargeProfile
        {
            Battery = Battery, Options = Options, Stages = [stage, .. Stages],
            TargetPackVolts = TargetPackVolts, OvpVolts = OvpVolts, OvpMarginPackVolts = OvpMarginPackVolts,
            MainCurrentAmps = MainCurrentAmps, TerminationAmps = TerminationAmps, MaxChargeAh = MaxChargeAh,
            RefuseBelowPackVolts = RefuseBelowPackVolts, PrechargeBelowPackVolts = exit,
            Warnings = Warnings, Errors = Errors,
        };
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
