using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;

namespace BatteryCharger.Tests.Core;

public class NickelCeilingTests
{
    private static NickelChargeStage Stage() =>
        new(ceilingVolts: 1.65, amps: 0.8, cells: 1, dvPerCell: 0.005, capacityAh: 1.6,
            minTime: TimeSpan.FromMinutes(10), minFraction: 0.10, timeout: TimeSpan.FromHours(3.2));

    private static Sample S(double seconds, double volts, double amps, double ah, PsuMode mode) =>
        new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(seconds), ChargeState.ConstantCurrent, "CC", volts, amps, ah, 0, 1.65, 0.8, mode);

    private static StageDecision RunAtCeiling(NickelChargeStage stage, double amps, double ah)
    {
        StageDecision last = StageDecision.Continue;
        for (var t = 0; t <= 70; t++) last = stage.Evaluate(S(t, 1.647, amps, ah, PsuMode.CV), TimeSpan.FromSeconds(t));
        return last;
    }

    [Fact]
    public void CeilingAtLowCurrentExplainsHighResistance()
    {
        var d = RunAtCeiling(Stage(), amps: 0.12, ah: 0.002);
        Assert.Equal(StageAction.CompleteWithWarning, d.Action);
        Assert.Contains("high resistance", d.Message);
        Assert.Contains("0.120 A", d.Message);
    }

    [Fact]
    public void CeilingNearFullChargeStaysTheShortMessage()
    {
        var d = RunAtCeiling(Stage(), amps: 0.7, ah: 1.5);
        Assert.Equal(StageAction.CompleteWithWarning, d.Action);
        Assert.Equal("Voltage ceiling reached", d.Message);
    }
}
