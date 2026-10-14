using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;

namespace BatteryCharger.Core.Profiles;

/// <summary>Builds <see cref="ChargeProfile"/> instances (spec section 6). Pure logic, no instrument access.</summary>
public static class ChargeProfileFactory
{
    /// <param name="battery">Battery description.</param>
    /// <param name="options">Profile options.</param>
    /// <param name="limits">Optional supply limits (at the range for the pack target voltage) for clamping and refusal.</param>
    public static ChargeProfile Create(BatteryDescriptor battery, ProfileOptions options, ChannelLimits? limits = null)
    {
        var chem = CellChemistryInfo.For(battery.Technology);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!(battery.NominalVoltage > 0)) errors.Add("Nominal voltage must be greater than zero.");
        if (!(battery.CapacityAh > 0)) errors.Add("Capacity must be greater than zero.");
        if (battery.CellsInSeries < 1) errors.Add("At least one cell is required.");
        if (!(battery.ChargeRateC > 0) || battery.ChargeRateC > chem.MaxRateC + 1e-9)
            errors.Add($"Charge rate must be above 0 and at most {chem.MaxRateC:0.##} C for {chem.DisplayName}.");
        errors.AddRange(options.Validate(battery.Technology));

        if (errors.Count > 0 || battery.CellsInSeries < 1)
            return Invalid(battery, options, errors, warnings);

        if (CellChemistryInfo.CellCountWarning(battery.Technology, battery.NominalVoltage, battery.CellsInSeries) is { } cw)
            warnings.Add(cw);

        var cells = battery.CellsInSeries;
        var cap = battery.CapacityAh;
        var vTarget = options.TargetVoltagePerCell * cells;
        var ovpMargin = options.OvpMarginPerCell * cells;
        var icc = battery.ChargeRateC * cap;
        var iPre = options.PrechargeRateC * cap;

        if (limits is not null)
        {
            if (vTarget > limits.MaxVolts + 1e-9)
                errors.Add($"Required voltage {vTarget:0.00} V exceeds the supply maximum of {limits.MaxVolts:0.00} V. Charging refused.");
            if (icc > limits.MaxAmps)
            {
                warnings.Add($"Charge current limited by the supply from {icc:0.###} A to {limits.MaxAmps:0.###} A; charging will take longer.");
                icc = limits.MaxAmps;
            }
            if (icc < limits.MinAmps) errors.Add($"Charge current {icc:0.####} A is below the supply minimum of {limits.MinAmps:0.####} A.");
            if (iPre > icc) iPre = icc;
        }

        var iTerm = Math.Max(options.TerminationCurrentC * cap, options.TerminationCurrentFloorAmps);
        if (!chem.IsNickel && iTerm >= 0.98 * icc)
            warnings.Add("Termination current is close to the charge current; the CV stage may end immediately.");

        var ccTimeout = TimeSpan.FromHours(options.CcTimeoutFactor * cap / icc);
        var stages = new List<IChargeStage>();
        double? preBelowPack = null;
        double fullPack = vTarget;

        switch (battery.Technology)
        {
            case BatteryTechnology.LiIon or BatteryTechnology.LiPo or BatteryTechnology.LiFePO4:
                if (options.PrechargeBelowVoltagePerCell is { } pre)
                {
                    preBelowPack = pre * cells;
                    stages.Add(new PrechargeStage(vTarget, iPre, preBelowPack.Value, options.PrechargeTimeout, pre));
                }
                stages.Add(new ConstantCurrentStage("CC", vTarget, icc, ccTimeout));
                stages.Add(new ConstantVoltageStage("CV", vTarget, icc, iTerm, options.CvTimeout, advanceWhenDone: false));
                break;
            case BatteryTechnology.NiMH or BatteryTechnology.NiCd:
                stages.Add(new NickelChargeStage(vTarget, icc, cells, options.DvPerCellVolts, cap,
                    options.NiMinTimeBeforeDv, options.NiMinFractionBeforeDv, ccTimeout));
                iTerm = 0;
                break;
            case BatteryTechnology.LeadAcid:
                stages.Add(new ConstantCurrentStage("Bulk (CC)", vTarget, icc, ccTimeout));
                stages.Add(new ConstantVoltageStage("Absorption (CV)", vTarget, icc, iTerm, options.CvTimeout, advanceWhenDone: true));
                stages.Add(new FloatStage((options.FloatVoltagePerCell ?? 2.27) * cells, icc));
                break;
        }

        return new ChargeProfile
        {
            Battery = battery,
            Options = options,
            Stages = stages,
            TargetPackVolts = fullPack,
            OvpVolts = vTarget + ovpMargin,
            OvpMarginPackVolts = ovpMargin,
            MainCurrentAmps = icc,
            TerminationAmps = iTerm,
            MaxChargeAh = options.MaxChargeFactor * cap,
            RefuseBelowPackVolts = options.RefuseBelowVoltagePerCell * cells,
            PrechargeBelowPackVolts = preBelowPack,
            Warnings = warnings,
            Errors = errors,
        };
    }

    private static ChargeProfile Invalid(BatteryDescriptor b, ProfileOptions o, List<string> errors, List<string> warnings) => new()
    {
        Battery = b, Options = o, Stages = [], Warnings = warnings, Errors = errors,
    };
}
