namespace BatteryCharger.Core.Batteries;

/// <summary>Per-cell constants for one chemistry (spec sections 6.1 and 6.2). Voltages are per cell.</summary>
public sealed record CellChemistryInfo(
    BatteryTechnology Technology,
    string DisplayName,
    double CellNominalV,
    double DefaultRateC,
    double MaxRateC,
    double TargetV,            // CV / absorption target; for Ni chemistries the voltage ceiling
    double RefuseBelowV,
    double? PrechargeBelowV,
    double PrechargeRateC,
    double TerminationCurrentC,   // Li / LFP / lead-acid; 0 for Ni
    double DvCellV,               // Ni -dV per cell; 0 otherwise
    double? FloatV,
    double MaxChargeFactor,
    double OvpMarginV,
    double CcTimeoutFactor,
    TimeSpan CvTimeout,
    bool IsNickel,
    double[] PresetVoltages)
{
    private static readonly CellChemistryInfo[] All =
    [
        new(BatteryTechnology.LiIon, "Li-ion", 3.6, 0.5, 1.0, 4.20, 2.0, 3.0, 0.1, 0.05, 0, null, 1.3, 0.10, 1.5, TimeSpan.FromHours(3), false, [3.6, 7.2, 10.8, 14.4]),
        new(BatteryTechnology.LiPo, "LiPo", 3.7, 0.5, 1.0, 4.20, 2.0, 3.0, 0.1, 0.05, 0, null, 1.3, 0.10, 1.5, TimeSpan.FromHours(3), false, [3.7, 7.4, 11.1, 14.8]),
        new(BatteryTechnology.LiFePO4, "LiFePO4", 3.2, 0.5, 1.0, 3.65, 2.0, 2.8, 0.1, 0.05, 0, null, 1.3, 0.15, 1.5, TimeSpan.FromHours(3), false, [3.2, 6.4, 9.6, 12.8]),
        new(BatteryTechnology.NiMH, "NiMH", 1.2, 0.5, 1.0, 1.65, 0.5, null, 0.1, 0, 0.005, null, 1.5, 0.10, 1.6, TimeSpan.Zero, true, [1.2, 2.4, 4.8, 9.6, 12.0]),
        new(BatteryTechnology.NiCd, "NiCd", 1.2, 0.5, 1.0, 1.65, 0.5, null, 0.1, 0, 0.010, null, 1.5, 0.10, 1.6, TimeSpan.Zero, true, [1.2, 2.4, 4.8, 9.6, 12.0]),
        new(BatteryTechnology.LeadAcid, "Lead-acid (SLA/AGM)", 2.0, 0.2, 0.3, 2.40, 1.5, null, 0.1, 0.03, 0, 2.27, 1.4, 0.20, 1.5, TimeSpan.FromHours(8), false, [6, 12, 24]),
    ];

    /// <summary>All chemistries in display order.</summary>
    public static IReadOnlyList<CellChemistryInfo> Table => All;

    /// <summary>Looks up the constants for a technology.</summary>
    public static CellChemistryInfo For(BatteryTechnology tech) => All[(int)tech];

    /// <summary>Derives the series cell count from a nominal pack voltage.</summary>
    public static int DeriveCells(BatteryTechnology tech, double nominalVoltage) =>
        Math.Max(1, (int)Math.Round(nominalVoltage / For(tech).CellNominalV, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Returns a warning when the cell count does not fit the entered voltage by more than 8 % of one cell, else null.
    /// </summary>
    public static string? CellCountWarning(BatteryTechnology tech, double nominalVoltage, int cells)
    {
        var cell = For(tech).CellNominalV;
        if (Math.Abs(cells * cell - nominalVoltage) > 0.08 * cell)
            return $"{cells} cells x {cell:0.0#} V = {cells * cell:0.0#} V does not match the entered {nominalVoltage:0.0#} V.";
        return null;
    }
}
