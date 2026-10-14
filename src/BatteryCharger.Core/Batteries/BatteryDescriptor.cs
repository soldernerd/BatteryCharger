namespace BatteryCharger.Core.Batteries;

/// <summary>User description of the battery pack to be charged.</summary>
/// <param name="Technology">Chemistry.</param>
/// <param name="NominalVoltage">Pack nominal voltage in volts, as entered by the user.</param>
/// <param name="CapacityAh">Rated capacity in ampere-hours.</param>
/// <param name="CellsInSeries">Number of series cells (derived from nominal voltage, user-overridable).</param>
/// <param name="ChargeRateC">C-rate of the main charge stage.</param>
public sealed record BatteryDescriptor(
    BatteryTechnology Technology,
    double NominalVoltage,
    double CapacityAh,
    int CellsInSeries,
    double ChargeRateC)
{
    /// <summary>Creates a descriptor with derived cell count and the default charge rate for the technology.</summary>
    public static BatteryDescriptor CreateDefault(BatteryTechnology tech, double nominalVoltage, double capacityAh)
    {
        var info = CellChemistryInfo.For(tech);
        return new BatteryDescriptor(tech, nominalVoltage, capacityAh,
            CellChemistryInfo.DeriveCells(tech, nominalVoltage), info.DefaultRateC);
    }
}
