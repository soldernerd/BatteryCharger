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
    ChannelSnapshot? Snapshot,
    bool LowVoltageOverridable = false)
{
    public bool Refused => Refusal is not null;
}

/// <summary>Plausibility checks run with the output off, before it is ever enabled.</summary>
public static class PreflightChecker
{
    /// <summary>Pure evaluation of a measured battery voltage against a profile.</summary>
    public static PreflightResult Evaluate(ChargeProfile profile, double vbat, ChannelSnapshot? snapshot = null, bool allowLowVoltage = false)
    {
        var warnings = new List<string>();
        var hints = new List<string>();
        var cells = profile.Battery.CellsInSeries;
        var chem = CellChemistryInfo.For(profile.Battery.Technology);
        string? refusal = null;
        var overridable = false;

        if (!profile.IsValid)
            refusal = "The charge profile is not valid: " + string.Join(" ", profile.Errors);
        else if (vbat < -0.1)
            refusal = $"Reverse polarity detected ({vbat:0.00} V).";
        else if (vbat / cells < profile.Options.RefuseBelowVoltagePerCell && allowLowVoltage)
            warnings.Add($"Battery voltage is very low ({vbat / cells:0.00} V/cell): starting with a gentle pre-charge. The battery may not recover; stop if the voltage does not rise.");
        else if (vbat / cells < profile.Options.RefuseBelowVoltagePerCell)
        {
            overridable = true;
            refusal = $"Battery voltage {vbat:0.00} V is too low for {cells} cell(s) ({vbat / cells:0.00} V/cell, minimum {profile.Options.RefuseBelowVoltagePerCell:0.00}). Battery missing, deeply discharged or damaged.";
        }
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
            initial = profile.InitialStageIndex(allowLowVoltage ? Math.Min(vbat, profile.RefuseBelowPackVolts) : vbat);
            if (profile.Stages[initial] is PrechargeStage)
                warnings.Add("Battery voltage is low: starting with a pre-charge stage.");
        }
        return new PreflightResult(vbat, refusal, warnings, hints, initial, snapshot, overridable);
    }

    /// <summary>
    /// Switches the output off, measures the battery voltage and evaluates it. Supplies that read 0 V at their terminals
    /// while the output is off (spec section 12, item 7) get a short probe with the output on instead, see <see cref="ProbeAsync"/>.
    /// </summary>
    public static async Task<PreflightResult> RunAsync(IPowerSupplyChannel channel, ChargeProfile profile, CancellationToken ct, bool allowLowVoltage = false)
    {
        var snapshot = await channel.ReadStateAsync(ct);
        await channel.SetOutputAsync(false, ct);
        var m = await channel.MeasureAsync(ct);
        var vbat = m.Volts;   // a negative reading is reverse polarity: never probe with the output on
        if (profile.IsValid && vbat >= -0.1 && vbat / profile.Battery.CellsInSeries < profile.Options.RefuseBelowVoltagePerCell)
            vbat = await ProbeAsync(channel, profile, ct);
        return Evaluate(profile, vbat, snapshot, allowLowVoltage);
    }

    /// <summary>
    /// Reads the battery voltage with the output briefly on. The probe voltage is 80 % of the refuse threshold, so a
    /// plausible battery is above it (the supply sources no current and the terminals show the battery), while a missing
    /// battery reads the probe voltage itself and is still refused. Current is limited and OVP is armed; the output is
    /// always switched off again, and the session configures its real setpoints afterwards.
    /// </summary>
    private static async Task<double> ProbeAsync(IPowerSupplyChannel channel, ChargeProfile profile, CancellationToken ct)
    {
        var probeVolts = 0.8 * profile.Options.RefuseBelowVoltagePerCell * profile.Battery.CellsInSeries;
        var probeAmps = Math.Min(ProbeCurrentAmps, profile.Stages[0].Amps);
        // VERIFY: item 13 of docs/verification-checklist.md (MEAS:VOLT? with the output off reads 0 V on the E36103A).
        await channel.ConfigureAsync(new ChargeSetpoint(probeVolts, probeAmps, profile.OvpVolts), ct);
        try
        {
            await channel.SetOutputAsync(true, ct);
            await Task.Delay(ProbeSettleTime, ct);
            return (await channel.MeasureAsync(ct)).Volts;
        }
        finally
        {
            await channel.SetOutputAsync(false, CancellationToken.None);
        }
    }

    private const double ProbeCurrentAmps = 0.05;
    private static readonly TimeSpan ProbeSettleTime = TimeSpan.FromMilliseconds(400);
}
