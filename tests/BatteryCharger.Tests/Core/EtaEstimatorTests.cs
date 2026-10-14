using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;

namespace BatteryCharger.Tests.Core;

public class EtaEstimatorTests
{
    private static ChargeProfile Profile(BatteryTechnology t, double nominal, double ah) =>
        ChargeProfileFactory.Create(BatteryDescriptor.CreateDefault(t, nominal, ah), ProfileOptions.Defaults(t));

    private static Sample S(ChargeState state, double seconds, double amps, double ah) =>
        new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(seconds), state, "x", 4.0, amps, ah, 0, 4.2, amps, PsuMode.CC);

    [Fact]
    public void NickelEtaIsRemainingChargeOverCurrent()
    {
        var p = Profile(BatteryTechnology.NiMH, 1.2, 1.6);
        var eta = new EtaEstimator(p).Update(S(ChargeState.ConstantCurrent, 600, 0.8, 0.2));
        Assert.NotNull(eta);
        Assert.Equal((1.25 * 1.6 - 0.2) / 0.8, eta!.Value.TotalHours, 3);
    }

    [Fact]
    public void LiIonCcEtaShrinksAsChargeIsDelivered()
    {
        var p = Profile(BatteryTechnology.LiIon, 3.6, 2.0);
        var e = new EtaEstimator(p);
        var early = e.Update(S(ChargeState.ConstantCurrent, 60, p.MainCurrentAmps, 0.05))!.Value;
        var later = e.Update(S(ChargeState.ConstantCurrent, 3000, p.MainCurrentAmps, 0.9))!.Value;
        Assert.True(later < early);
        Assert.True(early > TimeSpan.FromHours(1) && early < TimeSpan.FromHours(6));
    }

    [Fact]
    public void CvEtaFollowsObservedDecay()
    {
        var p = Profile(BatteryTechnology.LiIon, 3.6, 2.0);
        var e = new EtaEstimator(p);
        // Current decays with tau = 10 min; the real remaining time to the termination current is tau * ln(I/Iterm).
        var i0 = p.MainCurrentAmps;
        TimeSpan? eta = null;
        for (var t = 0; t <= 120; t += 10)
            eta = e.Update(S(ChargeState.ConstantVoltage, 5000 + t, i0 * Math.Exp(-t / 600.0), 1.5));
        var iNow = i0 * Math.Exp(-120 / 600.0);
        var expected = TimeSpan.FromMinutes(10 * Math.Log(iNow / p.TerminationAmps));
        Assert.InRange(eta!.Value.TotalMinutes, expected.TotalMinutes * 0.9, expected.TotalMinutes * 1.1);
    }

    [Fact]
    public void FloatIsZeroAndPrechargeUnknown()
    {
        var p = Profile(BatteryTechnology.LiIon, 3.6, 2.0);
        Assert.Equal(TimeSpan.Zero, new EtaEstimator(p).Update(S(ChargeState.Float, 1, 0.1, 1)));
        Assert.Null(new EtaEstimator(p).Update(S(ChargeState.Precharge, 1, 0.1, 0)));
    }

    [Theory]
    [InlineData(20, "less than a minute")]
    [InlineData(600, "10 min")]
    [InlineData(5100, "1:25 h")]
    public void FormatsReadably(int seconds, string text) => Assert.Equal(text, EtaEstimator.Format(TimeSpan.FromSeconds(seconds)));
}
