using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Profiles;

/// <summary>Lead-acid float; continues until the user stops the session.</summary>
public sealed class FloatStage(double volts, double amps) : IChargeStage
{
    public string Name => "Float";
    public ChargeState State => ChargeState.Float;
    public double Volts => volts;
    public double Amps => amps;
    public bool IsConstantCurrentStage => false;
    public bool IsFloat => true;
    public string Description => $"Float: {Volts:0.00} V until stopped (charged, floating)";
    public void Reset() { }
    public StageDecision Evaluate(Sample s, TimeSpan stageElapsed) => StageDecision.Continue;
}
