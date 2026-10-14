using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Profiles;

namespace BatteryCharger.Core.Session;

/// <summary>Outcome of the preflight checks (spec 6.4).</summary>
public sealed record PreflightResult(
    double BatteryVolts,
    string? Refusal,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Hints,
    int InitialStageIndex,
    ChannelSnapshot? Snapshot)
{
    public bool Refused => Refusal is not null;
}

/// <summary>Plausibility checks run with the output off, before it is ever enabled.</summary>
public static class PreflightChecker
{
    /// <summary>Pure evaluation of a measured battery voltage against a profile.</summary>
    public static PreflightResult Evaluate(ChargeProfile profile, double vbat, ChannelSnapshot? snapshot = null)
    {
        var warnings = new List<string>();
        var hints = new List<string>();
        var cells = profile.Battery.CellsInSeries;
        var chem = CellChemistryInfo.For(profile.Battery.Technology);
        string? refusal = null;

        if (!profile.IsValid)
            refusal = "The charge profile is not valid: " + string.Join(" ", profile.Errors);
        else if (vbat < -0.1)
            refusal = $"Reverse polarity detected ({vbat:0.00} V).";
        else if (vbat / cells < profile.Options.RefuseBelowVoltagePerCell)
            refusal = $"Battery voltage {vbat:0.00} V is too low for {cells} cell(s) ({vbat / cells:0.00} V/cell, minimum {profile.Options.RefuseBelowVoltagePerCell:0.00}). Battery missing, deeply discharged or damaged.";
        else if (vbat > 1.05 * profile.TargetPackVolts)
        {
            refusal = $"Battery voltage {vbat:0.00} V exceeds the target {profile.TargetPackVolts:0.00} V; check technology and cell count.";
            var n = (int)Math.Ceiling(vbat / (chem.TargetV + 0.05));
            hints.Add($"Measured voltage suggests {n} cells.");
        }

        var initial = 0;
        if (refusal is null)
        {
            if (vbat >= 0.99 * profile.TargetPackVolts && !chem.IsNickel)
                warnings.Add("Battery appears already charged.");
            initial = profile.InitialStageIndex(vbat);
            if (profile.Stages[initial] is PrechargeStage)
                warnings.Add("Battery voltage is low: starting with a pre-charge stage.");
        }
        return new PreflightResult(vbat, refusal, warnings, hints, initial, snapshot);
    }

    /// <summary>Switches the output off, measures the battery voltage and evaluates it.</summary>
    public static async Task<PreflightResult> RunAsync(IPowerSupplyChannel channel, ChargeProfile profile, CancellationToken ct)
    {
        var snapshot = await channel.ReadStateAsync(ct);
        await channel.SetOutputAsync(false, ct);
        var m = await channel.MeasureAsync(ct);
        return Evaluate(profile, m.Volts, snapshot);
    }
}
