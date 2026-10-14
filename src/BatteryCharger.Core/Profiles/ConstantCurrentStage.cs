using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Profiles;

/// <summary>Li-ion/LiPo/LiFePO4 CC stage and lead-acid bulk stage. Advances to CV when the supply leaves current limit.</summary>
public sealed class ConstantCurrentStage(string name, double volts, double amps, TimeSpan timeout) : IChargeStage
{
    private int _samples;
    public string Name => name;
    public ChargeState State => ChargeState.ConstantCurrent;
    public double Volts => volts;
    public double Amps => amps;
    public bool IsConstantCurrentStage => true;
    public bool IsFloat => false;
    public string Description => $"{name}: {Volts:0.00} V limit, {Amps:0.###} A, timeout {FormatTimeout(timeout)}";
    public void Reset() => _samples = 0;

    internal static string FormatTimeout(TimeSpan t) => $"{t.TotalHours:0.0} h";

    public StageDecision Evaluate(Sample s, TimeSpan stageElapsed)
    {
        _samples++;
        if (stageElapsed > timeout) return new(StageAction.Fault, $"{name} timeout ({FormatTimeout(timeout)})");
        // The first sample after output-on may still be ramping; do not leave CC on it.
        if (_samples >= 2 && (s.Volts >= volts * 0.99 || s.Amps < 0.98 * amps))
            return new(StageAction.Advance);
        return StageDecision.Continue;
    }
}
