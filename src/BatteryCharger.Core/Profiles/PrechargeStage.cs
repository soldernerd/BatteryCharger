using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Profiles;

/// <summary>Gentle low-current charge of a deeply discharged Li cell until it reaches the exit voltage.</summary>
public sealed class PrechargeStage(double volts, double amps, double exitPackVolts, TimeSpan timeout, double exitPerCell) : IChargeStage
{
    public string Name => "Pre-charge";
    public ChargeState State => ChargeState.Precharge;
    public double Volts => volts;
    public double Amps => amps;
    public bool IsConstantCurrentStage => true;
    public bool IsFloat => false;
    public double ExitPackVolts => exitPackVolts;
    public string Description => $"Pre-charge: {Volts:0.00} V limit, {Amps:0.###} A until {exitPackVolts:0.00} V ({exitPerCell:0.00} V/cell), timeout {timeout.TotalMinutes:0} min";
    public void Reset() { }

    public StageDecision Evaluate(Sample s, TimeSpan stageElapsed)
    {
        if (s.Volts >= exitPackVolts) return new(StageAction.Advance, "Pre-charge complete");
        if (stageElapsed > timeout) return new(StageAction.Fault, "Pre-charge timeout: cell not recovering");
        return StageDecision.Continue;
    }
}
