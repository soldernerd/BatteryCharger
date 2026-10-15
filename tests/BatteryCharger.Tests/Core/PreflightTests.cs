using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
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

    private static ChargeProfile NiMH1Cell() =>
        ChargeProfileFactory.Create(BatteryDescriptor.CreateDefault(BatteryTechnology.NiMH, 1.2, 1.6), ProfileOptions.Defaults(BatteryTechnology.NiMH));

    [Fact]
    public async Task SupplyThatReadsZeroWithOutputOffIsProbedBriefly()
    {
        var ch = new ZeroWhenOffChannel(batteryVolts: 1.2);
        var r = await PreflightChecker.RunAsync(ch, NiMH1Cell(), CancellationToken.None);
        Assert.False(r.Refused);
        Assert.Equal(1.2, r.BatteryVolts, 3);
        Assert.True(ch.WasOnDuringProbe);
        Assert.False(ch.OutputOn);                          // always off again afterwards
        Assert.True(ch.ProbeVolts < 0.5);                   // below the refuse threshold: a battery is never driven
    }

    [Fact]
    public async Task ProbeWithoutBatteryStillRefuses()
    {
        var ch = new ZeroWhenOffChannel(batteryVolts: 0);
        var r = await PreflightChecker.RunAsync(ch, NiMH1Cell(), CancellationToken.None);
        Assert.True(r.Refused);
        Assert.False(ch.OutputOn);
    }

    [Fact]
    public async Task ReversePolarityIsNeverProbed()
    {
        var ch = new ZeroWhenOffChannel(batteryVolts: -1.0, readsBatteryWhenOff: true);
        var r = await PreflightChecker.RunAsync(ch, NiMH1Cell(), CancellationToken.None);
        Assert.Contains("Reverse polarity", r.Refusal);
        Assert.False(ch.WasOnDuringProbe);
    }

    [Fact]
    public void LowVoltageIsRefusedButOverridable()
    {
        var r = PreflightChecker.Evaluate(NiMH1Cell(), 0.3);
        Assert.True(r.Refused);
        Assert.True(r.LowVoltageOverridable);
        Assert.False(PreflightChecker.Evaluate(NiMH1Cell(), -0.5).LowVoltageOverridable);   // reverse polarity never is
    }

    [Fact]
    public void LowVoltageOverrideStartsWithGentlePrecharge()
    {
        var p = NiMH1Cell().WithRecoveryStage();
        var r = PreflightChecker.Evaluate(p, 0.3, allowLowVoltage: true);
        Assert.False(r.Refused);
        Assert.Contains(r.Warnings, w => w.Contains("very low"));
        var pre = Assert.IsType<PrechargeStage>(p.Stages[r.InitialStageIndex]);
        Assert.True(pre.Amps <= 0.1 * 1.6 + 1e-9);      // at most 0.1 C
        Assert.True(p.Stages.Count == NiMH1Cell().Stages.Count + 1);
    }

    private static readonly ProfileOptions NiMHOptions = ProfileOptions.Defaults(BatteryTechnology.NiMH);

    [Fact]
    public async Task IdleMeasurementProbesAndLeavesTheOutputOff()
    {
        var ch = new ZeroWhenOffChannel(batteryVolts: 1.3);
        var v = await PreflightChecker.MeasureBatteryAsync(ch, BatteryTechnology.NiMH, 1, NiMHOptions, CancellationToken.None);
        Assert.Equal(1.3, v, 3);
        Assert.True(ch.WasOnDuringProbe);
        Assert.False(ch.OutputOn);
    }

    [Fact]
    public async Task IdleMeasurementWithoutBatteryReadsTheProbeVoltage()
    {
        var ch = new ZeroWhenOffChannel(batteryVolts: 0);
        var v = await PreflightChecker.MeasureBatteryAsync(ch, BatteryTechnology.NiMH, 1, NiMHOptions, CancellationToken.None);
        Assert.True(v < NiMHOptions.RefuseBelowVoltagePerCell);
        Assert.False(ch.OutputOn);
    }

    [Fact]
    public async Task IdleMeasurementNeverEnablesTheOutputOnReversePolarity()
    {
        var ch = new ZeroWhenOffChannel(batteryVolts: -0.8, readsBatteryWhenOff: true);
        var v = await PreflightChecker.MeasureBatteryAsync(ch, BatteryTechnology.NiMH, 1, NiMHOptions, CancellationToken.None);
        Assert.True(v < -0.1);
        Assert.False(ch.WasOnDuringProbe);
    }

    [Fact]
    public void LowVoltageIsFlaggedWhenAllowed()
    {
        var p = NiMH1Cell().WithRecoveryStage();
        Assert.True(PreflightChecker.Evaluate(p, 0.3, allowLowVoltage: true).LowVoltage);
        Assert.False(PreflightChecker.Evaluate(p, 1.2, allowLowVoltage: true).LowVoltage);
    }

    /// <summary>Models a supply whose terminals read 0 V with the output off and show max(battery, set voltage) when on.</summary>
    private sealed class ZeroWhenOffChannel(double batteryVolts, bool readsBatteryWhenOff = false) : IPowerSupplyChannel
    {
        private double _set;
        public bool OutputOn { get; private set; }
        public bool WasOnDuringProbe { get; private set; }
        public double ProbeVolts => _set;

        public int Index => 1;
        public string DisplayName => "stub";
        public string? OutputWarning => null;
        public bool HasVoltageRanges => false;
        public Task<ChannelLimits> GetLimitsAsync(double requiredVolts, CancellationToken ct) => Task.FromResult(new ChannelLimits(6, 5, 0, 0));
        public Task<ChannelSnapshot> ReadStateAsync(CancellationToken ct) => Task.FromResult(new ChannelSnapshot(false, 0, 0, 7, true, false));
        public Task ApplySafeStateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ConfigureAsync(ChargeSetpoint sp, CancellationToken ct) { _set = sp.Volts; return Task.CompletedTask; }
        public Task SetCurrentLimitAsync(double amps, CancellationToken ct) => Task.CompletedTask;
        public Task SetVoltageAsync(double volts, CancellationToken ct) { _set = volts; return Task.CompletedTask; }
        public Task SetOutputAsync(bool on, CancellationToken ct) { OutputOn = on; WasOnDuringProbe |= on; return Task.CompletedTask; }
        public Task<Measurement> MeasureAsync(CancellationToken ct) =>
            Task.FromResult(new Measurement(DateTimeOffset.UtcNow, OutputOn ? Math.Max(batteryVolts, _set) : readsBatteryWhenOff ? batteryVolts : 0, 0));
        public Task<bool> GetOutputAsync(CancellationToken ct) => Task.FromResult(OutputOn);
        public Task<IReadOnlyList<string>> DrainErrorsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RestoreAsync(ChannelSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
    }
}
