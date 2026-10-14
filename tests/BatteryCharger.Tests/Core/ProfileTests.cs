using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Profiles;

namespace BatteryCharger.Tests.Core;

public class ProfileTests
{
    private static ChargeProfile Make(BatteryTechnology t, double v, double ah, ChannelLimits? lim = null, double? rate = null, ProfileOptions? o = null)
    {
        var b = BatteryDescriptor.CreateDefault(t, v, ah);
        if (rate is not null) b = b with { ChargeRateC = rate.Value };
        return ChargeProfileFactory.Create(b, o ?? ProfileOptions.Defaults(t), lim);
    }

    [Theory]
    [InlineData(BatteryTechnology.LiIon, 3.6, 1)]
    [InlineData(BatteryTechnology.LiIon, 14.4, 4)]
    [InlineData(BatteryTechnology.LiPo, 11.1, 3)]
    [InlineData(BatteryTechnology.LiFePO4, 12.8, 4)]
    [InlineData(BatteryTechnology.NiMH, 4.8, 4)]
    [InlineData(BatteryTechnology.LeadAcid, 12, 6)]
    public void CellsAreDerivedFromNominalVoltage(BatteryTechnology t, double v, int cells) =>
        Assert.Equal(cells, CellChemistryInfo.DeriveCells(t, v));

    [Fact]
    public void CellCountMismatchWarns()
    {
        Assert.NotNull(CellChemistryInfo.CellCountWarning(BatteryTechnology.LiPo, 11.1, 2));
        Assert.Null(CellChemistryInfo.CellCountWarning(BatteryTechnology.LiPo, 11.1, 3));
    }

    [Fact]
    public void LiIon3S_HasExpectedSetpoints()
    {
        var p = Make(BatteryTechnology.LiIon, 10.8, 2.2);
        Assert.True(p.IsValid);
        Assert.Equal(3, p.Battery.CellsInSeries);
        Assert.Equal(12.6, p.TargetPackVolts, 6);
        Assert.Equal(12.6 + 0.3, p.OvpVolts, 6);
        Assert.Equal(1.1, p.MainCurrentAmps, 6);
        Assert.Equal(0.11, p.TerminationAmps, 6);
        Assert.Equal(1.3 * 2.2, p.MaxChargeAh, 6);
        Assert.Collection(p.Stages, s => Assert.IsType<PrechargeStage>(s), s => Assert.IsType<ConstantCurrentStage>(s), s => Assert.IsType<ConstantVoltageStage>(s));
    }

    [Fact]
    public void TerminationCurrentHasFloor()
    {
        var p = Make(BatteryTechnology.LiIon, 3.6, 0.1);   // 0.05 C = 5 mA -> floor 10 mA
        Assert.Equal(0.010, p.TerminationAmps, 6);
    }

    [Fact]
    public void CurrentIsClampedToSupplyAndWarns()
    {
        var p = Make(BatteryTechnology.LiIon, 7.2, 20, new ChannelLimits(30, 5, 0, 0.001));
        Assert.True(p.IsValid);
        Assert.Equal(5, p.MainCurrentAmps, 6);
        Assert.Contains(p.Warnings, w => w.Contains("limited by the supply"));
    }

    [Fact]
    public void VoltageAboveSupplyMaxIsRefused()
    {
        var p = Make(BatteryTechnology.LiIon, 14.4, 2, new ChannelLimits(8, 5, 0, 0.001));
        Assert.False(p.IsValid);
        Assert.Contains(p.Errors, e => e.Contains("exceeds the supply maximum"));
    }

    [Fact]
    public void RateAboveHardLimitIsRejected()
    {
        Assert.False(Make(BatteryTechnology.LeadAcid, 12, 7, rate: 0.5).IsValid);
        Assert.False(Make(BatteryTechnology.LiIon, 3.7, 1, rate: 1.5).IsValid);
        Assert.True(Make(BatteryTechnology.LiIon, 3.7, 1, rate: 1.0).IsValid);
    }

    [Fact]
    public void TargetVoltageAboveHardLimitIsRejected()
    {
        var o = ProfileOptions.Defaults(BatteryTechnology.LiIon) with { TargetVoltagePerCell = 4.25 };
        Assert.False(Make(BatteryTechnology.LiIon, 3.6, 1, o: o).IsValid);
        var ok = ProfileOptions.Defaults(BatteryTechnology.LiIon) with { TargetVoltagePerCell = 4.10 };
        Assert.True(Make(BatteryTechnology.LiIon, 3.6, 1, o: ok).IsValid);
    }

    [Theory]
    [InlineData(BatteryTechnology.LiIon)]
    [InlineData(BatteryTechnology.LiPo)]
    [InlineData(BatteryTechnology.LiFePO4)]
    [InlineData(BatteryTechnology.NiMH)]
    [InlineData(BatteryTechnology.NiCd)]
    [InlineData(BatteryTechnology.LeadAcid)]
    public void DefaultOptionsAreAlwaysValid(BatteryTechnology t)
    {
        Assert.Empty(ProfileOptions.Defaults(t).Validate(t));
        Assert.True(Make(t, CellChemistryInfo.For(t).CellNominalV * 4, 2).IsValid);
    }

    [Fact]
    public void LeadAcidEndsInFloatAndNickelHasSingleStage()
    {
        var pb = Make(BatteryTechnology.LeadAcid, 12, 7);
        Assert.IsType<FloatStage>(pb.Stages[^1]);
        Assert.Equal(6 * 2.27, pb.Stages[^1].Volts, 6);
        Assert.Equal(1.4, pb.MainCurrentAmps, 6);
        var ni = Make(BatteryTechnology.NiMH, 4.8, 2);
        Assert.Single(ni.Stages);
        Assert.Equal(4 * 1.65, ni.TargetPackVolts, 6);
    }

    [Fact]
    public void InitialStageDependsOnBatteryVoltage()
    {
        var p = Make(BatteryTechnology.LiIon, 7.2, 2);
        Assert.IsType<PrechargeStage>(p.Stages[p.InitialStageIndex(5.0)]);
        Assert.IsType<ConstantCurrentStage>(p.Stages[p.InitialStageIndex(7.0)]);
        Assert.IsType<ConstantVoltageStage>(p.Stages[p.InitialStageIndex(8.35)]);
    }

    [Fact]
    public void SummaryMentionsSafetyLimits()
    {
        var p = Make(BatteryTechnology.LiIon, 7.2, 2);
        Assert.Contains(p.SummaryLines, l => l.Contains("OVP"));
    }
}
