namespace BatteryCharger.Core.Instruments.Simulation;

/// <summary>
/// Very simple battery model: linear open-circuit voltage between empty and full, series resistance.
/// Good enough to exercise CC/CV transitions and termination logic.
/// </summary>
public sealed class SimulatedBattery
{
    public int Cells { get; }
    public double CapacityAh { get; }
    public double EmptyCellVolts { get; }
    public double FullCellVolts { get; }
    public double CellResistanceOhm { get; }
    public double ChargeAh { get; set; }
    public bool Connected { get; set; } = true;
    /// <summary>When true the cell voltage shows a -dV drop after reaching full (Ni chemistries).</summary>
    public double DvCellVolts { get; init; }
    public bool Reversed { get; set; }

    public SimulatedBattery(int cells, double capacityAh, double emptyCellVolts, double fullCellVolts, double cellResistanceOhm, double initialSoc)
    {
        Cells = cells;
        CapacityAh = capacityAh;
        EmptyCellVolts = emptyCellVolts;
        FullCellVolts = fullCellVolts;
        CellResistanceOhm = cellResistanceOhm;
        ChargeAh = Math.Clamp(initialSoc, 0, 1) * capacityAh;
    }

    public double Soc => Math.Clamp(ChargeAh / CapacityAh, 0, 1);

    public double OpenCircuitVolts
    {
        get
        {
            var v = Cells * (EmptyCellVolts + (FullCellVolts - EmptyCellVolts) * Soc);
            // -dV: past ~97 % state of charge the voltage sags a little (Ni chemistries).
            if (DvCellVolts > 0 && Soc > 0.97) v -= Cells * DvCellVolts * 3 * Math.Min(1, (Soc - 0.97) / 0.03 + 0.0);
            return Reversed ? -v : v;
        }
    }

    public double ResistanceOhm => Cells * CellResistanceOhm;

    /// <summary>Convenience factory for common test setups.</summary>
    public static SimulatedBattery LiIon(int cells, double capacityAh, double soc) =>
        new(cells, capacityAh, 3.0, 4.20, 0.05, soc);
}
