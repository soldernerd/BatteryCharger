using BatteryCharger.Core.Batteries;

namespace BatteryCharger.Core.Profiles;

/// <summary>
/// Tunable profile parameters (spec 6.2), per cell unless noted. Validated against hard limits; values beyond them are never accepted.
/// </summary>
public sealed record ProfileOptions
{
    public double TargetVoltagePerCell { get; init; }
    public double? PrechargeBelowVoltagePerCell { get; init; }
    public double PrechargeRateC { get; init; }
    public double TerminationCurrentC { get; init; }
    public double DvPerCellVolts { get; init; }
    public double? FloatVoltagePerCell { get; init; }
    public double MaxChargeFactor { get; init; }
    public double OvpMarginPerCell { get; init; }
    public double CcTimeoutFactor { get; init; }
    public TimeSpan CvTimeout { get; init; }
    public TimeSpan PrechargeTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public double RefuseBelowVoltagePerCell { get; init; }
    public TimeSpan NiMinTimeBeforeDv { get; init; } = TimeSpan.FromMinutes(10);
    public double NiMinFractionBeforeDv { get; init; } = 0.10;
    /// <summary>Floor for the termination current (measurement resolution), A.</summary>
    public double TerminationCurrentFloorAmps { get; init; } = 0.010;

    /// <summary>Default options for a technology.</summary>
    public static ProfileOptions Defaults(BatteryTechnology tech)
    {
        var c = CellChemistryInfo.For(tech);
        return new ProfileOptions
        {
            TargetVoltagePerCell = c.TargetV,
            PrechargeBelowVoltagePerCell = c.PrechargeBelowV,
            PrechargeRateC = c.PrechargeRateC,
            TerminationCurrentC = c.TerminationCurrentC,
            DvPerCellVolts = c.DvCellV,
            FloatVoltagePerCell = c.FloatV,
            MaxChargeFactor = c.MaxChargeFactor,
            OvpMarginPerCell = c.OvpMarginV,
            CcTimeoutFactor = c.CcTimeoutFactor,
            CvTimeout = c.CvTimeout,
            RefuseBelowVoltagePerCell = c.RefuseBelowV,
        };
    }

    /// <summary>Returns hard-limit violations (empty when valid).</summary>
    public IReadOnlyList<string> Validate(BatteryTechnology tech)
    {
        var c = CellChemistryInfo.For(tech);
        var e = new List<string>();
        void Range(string name, double v, double min, double max, string unit)
        {
            if (double.IsNaN(v) || v < min - 1e-9 || v > max + 1e-9)
                e.Add($"{name} must be between {min:0.###} and {max:0.###} {unit}.");
        }
        Range(c.IsNickel ? "Voltage ceiling" : "Target voltage", TargetVoltagePerCell, c.IsNickel ? 1.40 : c.TargetV - 0.40, c.TargetV, "V/cell");
        Range("OVP margin", OvpMarginPerCell, 0.05, c.OvpMarginV, "V/cell");
        Range("Maximum charge", MaxChargeFactor, 1.0, c.MaxChargeFactor, "x C");
        Range("CC timeout factor", CcTimeoutFactor, 1.0, c.CcTimeoutFactor, "x C/I");
        Range("Pre-charge rate", PrechargeRateC, 0.01, 0.2, "C");
        Range("Refuse-below voltage", RefuseBelowVoltagePerCell, 0.0, c.RefuseBelowV + 1.0, "V/cell");
        if (!c.IsNickel) Range("Termination current", TerminationCurrentC, 0.01, 0.2, "C");
        else Range("-dV", DvPerCellVolts, 0.003, 0.020, "V/cell");
        if (c.PrechargeBelowV is not null)
        {
            if (PrechargeBelowVoltagePerCell is null) e.Add("Pre-charge threshold is required for this chemistry.");
            else Range("Pre-charge threshold", PrechargeBelowVoltagePerCell.Value, c.RefuseBelowV, c.PrechargeBelowV.Value, "V/cell");
        }
        if (c.FloatV is not null)
        {
            if (FloatVoltagePerCell is null) e.Add("Float voltage is required for this chemistry.");
            else Range("Float voltage", FloatVoltagePerCell.Value, 2.0, Math.Min(c.FloatV.Value, TargetVoltagePerCell), "V/cell");
        }
        if (CvTimeout < TimeSpan.Zero || (!c.IsNickel && CvTimeout.TotalMinutes < 10) || CvTimeout > c.CvTimeout && c.CvTimeout > TimeSpan.Zero)
            e.Add($"CV timeout must be between 10 minutes and {c.CvTimeout.TotalHours:0.#} h.");
        if (PrechargeTimeout <= TimeSpan.Zero || PrechargeTimeout > TimeSpan.FromMinutes(30))
            e.Add("Pre-charge timeout must be between 1 and 30 minutes.");
        return e;
    }
}
