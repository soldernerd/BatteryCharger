using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;

namespace BatteryCharger.Tests.Core;

public class PreflightTests
{
    private static ChargeProfile LiIon2S(double ah = 2) =>
        ChargeProfileFactory.Create(BatteryDescriptor.CreateDefault(BatteryTechnology.LiIon, 7.2, ah), ProfileOptions.Defaults(BatteryTechnology.LiIon));

    [Fact]
    public void ReversePolarityIsRefused() =>
        Assert.Contains("Reverse polarity", PreflightChecker.Evaluate(LiIon2S(), -0.5).Refusal);

    [Fact]
    public void MissingOrDeadBatteryIsRefused() =>
        Assert.True(PreflightChecker.Evaluate(LiIon2S(), 0.05).Refused);

    [Fact]
    public void TooHighVoltageIsRefusedWithCellHint()
    {
        var r = PreflightChecker.Evaluate(LiIon2S(), 12.4);
        Assert.True(r.Refused);
        Assert.Contains(r.Hints, h => h.Contains("3 cells"));
    }

    [Fact]
    public void AlreadyChargedWarnsAndStartsInCv()
    {
        var p = LiIon2S();
        var r = PreflightChecker.Evaluate(p, 8.38);
        Assert.False(r.Refused);
        Assert.Contains(r.Warnings, w => w.Contains("already charged"));
        Assert.IsType<ConstantVoltageStage>(p.Stages[r.InitialStageIndex]);
    }

    [Fact]
    public void LowVoltageSelectsPrecharge()
    {
        var p = LiIon2S();
        var r = PreflightChecker.Evaluate(p, 5.0);
        Assert.False(r.Refused);
        Assert.IsType<PrechargeStage>(p.Stages[r.InitialStageIndex]);
    }

    [Fact]
    public void NormalVoltageHasNoWarnings()
    {
        var r = PreflightChecker.Evaluate(LiIon2S(), 7.4);
        Assert.False(r.Refused);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void InvalidProfileIsRefused()
    {
        var bad = ChargeProfileFactory.Create(BatteryDescriptor.CreateDefault(BatteryTechnology.LiIon, 7.2, 0), ProfileOptions.Defaults(BatteryTechnology.LiIon));
        Assert.True(PreflightChecker.Evaluate(bad, 7.4).Refused);
    }
}
