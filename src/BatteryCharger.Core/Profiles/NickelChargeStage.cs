using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Profiles;

/// <summary>NiMH/NiCd constant-current charge terminated by -dV detection, voltage ceiling, or timeout.</summary>
public sealed class NickelChargeStage(
    double ceilingVolts, double amps, int cells, double dvPerCell, double capacityAh,
    TimeSpan minTime, double minFraction, TimeSpan timeout) : IChargeStage
{
    private const int FilterLength = 5;
    private readonly double[] _window = new double[FilterLength];
    private int _count;
    private double _peak;
    private int _dropSamples;
    private TimeSpan? _ceilingSince;

    public string Name => "CC";
    public ChargeState State => ChargeState.ConstantCurrent;
    public double Volts => ceilingVolts;
    public double Amps => amps;
    public bool IsConstantCurrentStage => true;
    public bool IsFloat => false;
    public string Description =>
        $"CC: {amps:0.###} A, ceiling {ceilingVolts:0.00} V; end on -dV {dvPerCell * 1000:0} mV/cell (armed after {minTime.TotalMinutes:0} min and {minFraction:P0} of capacity), timeout {ConstantCurrentStage.FormatTimeout(timeout)}";

    public void Reset()
    {
        _count = 0; _peak = 0; _dropSamples = 0; _ceilingSince = null;
    }

    public StageDecision Evaluate(Sample s, TimeSpan stageElapsed)
    {
        _window[_count % FilterLength] = s.Volts;
        _count++;
        var n = Math.Min(_count, FilterLength);
        var sorted = _window.Take(n).OrderBy(v => v).ToArray();
        var filtered = sorted[n / 2];
        if (_count >= FilterLength) _peak = Math.Max(_peak, filtered);

        if (s.Mode == PsuMode.CV && s.Volts >= ceilingVolts * 0.995)
        {
            _ceilingSince ??= stageElapsed;
            if (stageElapsed - _ceilingSince.Value >= TimeSpan.FromSeconds(60))
                return new(StageAction.CompleteWithWarning, "Voltage ceiling reached");
        }
        else _ceilingSince = null;

        var armed = s.Elapsed >= minTime && s.Ah >= minFraction * capacityAh && _count >= FilterLength;
        if (armed && filtered < _peak - cells * dvPerCell)
        {
            if (++_dropSamples >= 3) return new(StageAction.Complete, $"-dV detected ({dvPerCell * 1000:0} mV/cell)");
        }
        else _dropSamples = 0;

        if (stageElapsed > timeout)
            return new(StageAction.CompleteWithWarning, "Charge timeout reached without -dV detection");
        return StageDecision.Continue;
    }
}
