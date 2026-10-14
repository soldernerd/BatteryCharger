using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Profiles;

/// <summary>CV (Li) / absorption (lead-acid) stage ending when the current stays below the termination threshold.</summary>
public sealed class ConstantVoltageStage(string name, double volts, double amps, double terminationAmps, TimeSpan timeout, bool advanceWhenDone) : IChargeStage
{
    private int _below;
    public string Name => name;
    public ChargeState State => ChargeState.ConstantVoltage;
    public double Volts => volts;
    public double Amps => amps;
    public double TerminationAmps => terminationAmps;
    public bool IsConstantCurrentStage => false;
    public bool IsFloat => false;
    public string Description => $"{name}: {Volts:0.00} V until I < {terminationAmps * 1000:0} mA, timeout {ConstantCurrentStage.FormatTimeout(timeout)}";
    public void Reset() => _below = 0;

    public StageDecision Evaluate(Sample s, TimeSpan stageElapsed)
    {
        _below = s.Amps < terminationAmps ? _below + 1 : 0;
        if (_below >= 10)
            return new(advanceWhenDone ? StageAction.Advance : StageAction.Complete, $"{name} complete (I < {terminationAmps * 1000:0} mA)");
        if (stageElapsed > timeout)
            return new(StageAction.CompleteWithWarning, "CV timeout");
        return StageDecision.Continue;
    }
}
