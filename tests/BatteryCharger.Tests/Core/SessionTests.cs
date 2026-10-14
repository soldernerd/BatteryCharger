using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Instruments.Simulation;
using BatteryCharger.Core.Profiles;
using BatteryCharger.Core.Session;

namespace BatteryCharger.Tests.Core;

public class SessionTests
{
    private sealed class MemoryLogger : ISessionLogger
    {
        public string? FilePath => "memory";
        public bool FailOpen { get; set; }
        public List<Sample> Samples { get; } = [];
        public List<string> Events { get; } = [];
        public SessionResult? Result { get; private set; }
        public Task OpenAsync(SessionHeader header, CancellationToken ct) =>
            FailOpen ? throw new IOException("disk full") : Task.CompletedTask;
        public Task WriteSampleAsync(Sample s, CancellationToken ct) { Samples.Add(s); return Task.CompletedTask; }
        public Task WriteEventAsync(DateTimeOffset t, TimeSpan e, ChargeState st, string stage, string text, CancellationToken ct)
        { Events.Add(text); return Task.CompletedTask; }
        public Task CloseAsync(SessionResult r, CancellationToken ct) { Result = r; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig
    {
        public required SimulatedClock Clock { get; init; }
        public required FakePowerSupply Psu { get; init; }
        public required ChargeSession Session { get; init; }
        public required MemoryLogger Logger { get; init; }
        public FakeChannel Ch => Psu.Channel;
        public List<ChargeState> States { get; } = [];
        public List<string> Stages { get; } = [];
    }

    private static Rig Make(BatteryTechnology tech, int cells, double ah, SimulatedBattery sim, double? rate = null, bool confirmed = true)
    {
        var clock = new SimulatedClock();
        var psu = new FakePowerSupply(sim, clock, TimeSpan.FromSeconds(60));
        var b = BatteryDescriptor.CreateDefault(tech, CellChemistryInfo.For(tech).CellNominalV * cells, ah);
        if (rate is not null) b = b with { ChargeRateC = rate.Value };
        var profile = ChargeProfileFactory.Create(b, ProfileOptions.Defaults(tech), new ChannelLimits(30, 5, 0, 0.001));
        var logger = new MemoryLogger();
        var session = new ChargeSession(psu.Channel, b, profile, logger, new SessionOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            Clock = clock,
            UserConfirmed = confirmed,
        });
        var rig = new Rig { Clock = clock, Psu = psu, Session = session, Logger = logger };
        session.Updated += (_, u) =>
        {
            if (rig.States.Count == 0 || rig.States[^1] != u.State) rig.States.Add(u.State);
            if (rig.Stages.Count == 0 || rig.Stages[^1] != u.Stage) rig.Stages.Add(u.Stage);
        };
        return rig;
    }

    private static async Task<SessionResult> Run(Rig r) =>
        await r.Session.RunAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

    [Fact]
    public async Task LiIonChargeCompletesThroughCcAndCv()
    {
        var sim = SimulatedBattery.LiIon(2, 1.0, 0.3);
        var startAh = sim.ChargeAh;
        var r = Make(BatteryTechnology.LiIon, 2, 1.0, sim);
        var res = await Run(r);

        Assert.Equal(EndReason.Completed, res.Reason);
        Assert.Equal(ChargeState.Completed, r.Session.State);
        Assert.Contains("CC", r.Stages);
        Assert.Contains("CV", r.Stages);
        Assert.True(r.Stages.IndexOf("CC") < r.Stages.IndexOf("CV"));
        Assert.False(r.Ch.OutputOn);
        Assert.Contains("RESTORE", r.Ch.Log);
        // Integrated charge matches what the simulated battery really received (within 3 %).
        var truth = sim.ChargeAh - startAh;
        Assert.InRange(res.AhDelivered, truth * 0.97, truth * 1.03);
        Assert.True(res.WhDelivered > res.AhDelivered * 6.0);
        Assert.NotEmpty(r.Logger.Samples);
        Assert.Contains(r.Logger.Samples, s => s.Event != null && s.Event.Contains("Stage: CV"));
        Assert.NotNull(r.Logger.Result);
    }

    [Fact]
    public async Task OvpIsConfiguredBeforeOutputOn()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 1.0, SimulatedBattery.LiIon(2, 1.0, 0.5));
        await Run(r);
        var cfg = r.Ch.Log.FindIndex(l => l.StartsWith("CONFIG"));
        var on = r.Ch.Log.IndexOf("OUTPUT ON");
        Assert.InRange(cfg, 0, on - 1);
        Assert.Contains("OVP 8.6", r.Ch.Log[cfg]);
    }

    [Fact]
    public async Task PrechargeStartsForDeeplyDischargedCell()
    {
        var sim = new SimulatedBattery(2, 0.5, 2.0, 4.20, 0.05, 0.409);  // 2 x 2.9 V, below the 3.0 V/cell pre-charge threshold
        var r = Make(BatteryTechnology.LiIon, 2, 1.0, sim);
        var res = await Run(r);
        Assert.True(res.Reason == EndReason.Completed, res.Message);
        Assert.Equal("Pre-charge", r.Stages[1]);
        Assert.True(r.Stages.IndexOf("Pre-charge") < r.Stages.IndexOf("CC"));
    }

    [Fact]
    public async Task ReversePolarityNeverEnablesOutput()
    {
        var sim = SimulatedBattery.LiIon(2, 1.0, 0.5);
        sim.Reversed = true;
        var r = Make(BatteryTechnology.LiIon, 2, 1.0, sim);
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.Contains("Reverse polarity", res.Message);
        Assert.DoesNotContain("OUTPUT ON", r.Ch.Log);
    }

    [Fact]
    public async Task WrongCellCountIsRefused()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 1.0, SimulatedBattery.LiIon(3, 1.0, 0.9));   // 3S battery on a 2S profile
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.Contains("3 cells", res.Message);
        Assert.DoesNotContain("OUTPUT ON", r.Ch.Log);
    }

    [Fact]
    public async Task UnconfirmedSessionDoesNotRun()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 1.0, SimulatedBattery.LiIon(2, 1.0, 0.5), confirmed: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Session.RunAsync(CancellationToken.None));
        Assert.Empty(r.Ch.Log);
    }

    [Fact]
    public async Task LogFailureAbortsBeforeTouchingTheSupply()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 1.0, SimulatedBattery.LiIon(2, 1.0, 0.5));
        r.Logger.FailOpen = true;
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.Empty(r.Ch.Log);
    }

    [Fact]
    public async Task UserStopTurnsOutputOffAndRestores()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 5.0, SimulatedBattery.LiIon(2, 5.0, 0.2), rate: 0.2);
        r.Session.Updated += (_, u) => { if (u.Sample?.Elapsed > TimeSpan.FromMinutes(5)) _ = r.Session.RequestStopAsync(); };
        var res = await Run(r);
        Assert.Equal(EndReason.UserStopped, res.Reason);
        Assert.Equal(ChargeState.Stopped, r.Session.State);
        Assert.False(r.Ch.OutputOn);
        Assert.Equal("RESTORE", r.Ch.Log[^1]);
    }

    [Fact]
    public async Task BatteryDisconnectedMidChargeFaults()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 5.0, SimulatedBattery.LiIon(2, 5.0, 0.4), rate: 0.2);
        r.Ch.OnMeasure = (ch, n) => { if (n == 6) ch.Battery.Connected = false; };
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.False(r.Ch.OutputOn);
    }

    [Fact]
    public async Task CommunicationLossEndsInFaultWithWarning()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 5.0, SimulatedBattery.LiIon(2, 5.0, 0.4), rate: 0.2);
        r.Ch.CommunicationLostAfterMeasures = 4;
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.Contains("lost", res.Message);
        Assert.False(r.Ch.OutputOn);   // shutdown path retried SetOutput(false)
        Assert.Equal(ChargeState.Faulted, r.Session.State);
    }

    [Fact]
    public async Task OutputThatCannotBeConfirmedOffIsReported()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 5.0, SimulatedBattery.LiIon(2, 5.0, 0.4), rate: 0.2);
        r.Ch.CommunicationLostAfterMeasures = 3;
        r.Ch.SetOutputOffFailures = 99;
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.Contains("could not be confirmed off", res.Message);
    }

    [Fact]
    public async Task InstrumentErrorQueueFaultsTheSession()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 5.0, SimulatedBattery.LiIon(2, 5.0, 0.4), rate: 0.2);
        r.Ch.OnMeasure = (ch, n) => { if (n == 5) ch.QueueError("-222,\"Data out of range\""); };
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.Contains("Data out of range", res.Message);
    }

    [Fact]
    public async Task OvpTripFaults()
    {
        var r = Make(BatteryTechnology.LiIon, 2, 5.0, SimulatedBattery.LiIon(2, 5.0, 0.4), rate: 0.2);
        r.Ch.OnMeasure = (ch, n) => { if (n == 5) ch.SetOutputAsync(false, default); };
        var res = await Run(r);
        Assert.Equal(EndReason.Fault, res.Reason);
        Assert.Contains("switched off unexpectedly", res.Message);
    }

    [Fact]
    public async Task NiMhChargeEndsOnMinusDv()
    {
        var sim = new SimulatedBattery(4, 2.0, 1.0, 1.45, 0.02, 0.2) { DvCellVolts = 0.010 };
        var r = Make(BatteryTechnology.NiMH, 4, 2.0, sim);
        var res = await Run(r);
        Assert.Equal(EndReason.Completed, res.Reason);
        Assert.Contains("-dV", res.Message);
        Assert.False(r.Ch.OutputOn);
    }

    [Fact]
    public async Task LeadAcidReachesFloatAndRunsUntilStopped()
    {
        var sim = new SimulatedBattery(6, 7.0, 1.9, 2.40, 0.01, 0.5);
        var r = Make(BatteryTechnology.LeadAcid, 6, 7.0, sim);
        var floatTicks = 0;
        r.Session.Updated += (_, u) =>
        {
            if (u.State == ChargeState.Float && u.Sample is not null && ++floatTicks > 5) _ = r.Session.RequestStopAsync();
        };
        var res = await Run(r);
        Assert.Contains(ChargeState.Float, r.States);
        Assert.Equal(EndReason.UserStopped, res.Reason);
        Assert.Contains(r.Stages, s => s.StartsWith("Absorption"));
    }

    [Fact]
    public async Task ChargeLimitTerminatesWithWarning()
    {
        // A "battery" that never fills up: huge capacity, so only the delivered-charge limit can end the session.
        var sim = new SimulatedBattery(2, 1000.0, 3.0, 4.2, 0.05, 0.1);
        var clock = new SimulatedClock();
        var psu = new FakePowerSupply(sim, clock, TimeSpan.FromSeconds(60));
        var b = BatteryDescriptor.CreateDefault(BatteryTechnology.LiIon, 7.2, 1.0);
        var profile = ChargeProfileFactory.Create(b, ProfileOptions.Defaults(BatteryTechnology.LiIon), new ChannelLimits(30, 5, 0, 0.001));
        var logger = new MemoryLogger();
        var session = new ChargeSession(psu.Channel, b, profile, logger,
            new SessionOptions { PollInterval = TimeSpan.FromMilliseconds(1), Clock = clock, UserConfirmed = true });
        var res = await session.RunAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.Equal(EndReason.CompletedWithWarning, res.Reason);
        Assert.Contains("Charge limit", res.Message);
    }
}
