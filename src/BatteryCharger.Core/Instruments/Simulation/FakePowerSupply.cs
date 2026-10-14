using System.Diagnostics;

namespace BatteryCharger.Core.Instruments.Simulation;

/// <summary>
/// Simulated single-channel supply wired to a <see cref="SimulatedBattery"/>. Used by unit tests and by the app's demo mode.
/// Not thread-safe by design (the session serialises calls).
/// </summary>
public sealed class FakePowerSupply : IPowerSupply
{
    public FakeChannel Channel { get; }
    public string Resource => "SIMULATED::PSU";
    public InstrumentIdentity Identity { get; } = new("Simulated", "SIM-PSU", "0001", "1.0");
    public IReadOnlyList<IPowerSupplyChannel> Channels { get; }

    public FakePowerSupply(SimulatedBattery battery, SimulatedClock? clock = null, TimeSpan? stepPerMeasure = null,
                           double timeScale = 1.0, ChannelLimits? limits = null)
    {
        Channel = new FakeChannel(battery, clock, stepPerMeasure, timeScale, limits ?? new ChannelLimits(60, 10, 0, 0.001));
        Channels = [Channel];
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Channel of <see cref="FakePowerSupply"/> with fault injection hooks.</summary>
public sealed class FakeChannel : IPowerSupplyChannel
{
    private readonly SimulatedBattery _battery;
    private readonly SimulatedClock? _clock;
    private readonly TimeSpan? _step;
    private readonly double _scale;
    private readonly ChannelLimits _limits;
    private long _lastTimestamp = Stopwatch.GetTimestamp();

    private double _volts = 0, _amps = 0.1, _ovp = 30;
    private bool _ovpOn, _ocpOn = true, _output;
    private readonly List<string> _errors = [];
    private int _measureCount;

    public int Index => 1;
    public string DisplayName => $"CH1 ({_limits.MaxVolts:0.#} V / {_limits.MaxAmps:0.#} A)";
    public string? OutputWarning => null;
    public bool HasVoltageRanges => false;

    // Fault injection / inspection.
    public int MeasureCalls => _measureCount;
    public bool OutputOn => _output;
    public double SetVolts => _volts;
    public double SetAmps => _amps;
    public double OvpVolts => _ovp;
    public bool OvpEnabled => _ovpOn;
    public bool OcpEnabled => _ocpOn;
    /// <summary>Throw <see cref="InstrumentCommunicationLost"/> from measure calls once this many have occurred.</summary>
    public int? CommunicationLostAfterMeasures { get; set; }
    /// <summary>Called on every measure (after the model advanced); lets tests pull the cable, etc.</summary>
    public Action<FakeChannel, int>? OnMeasure { get; set; }
    /// <summary>When set, SetOutput(false) throws this many times before working.</summary>
    public int SetOutputOffFailures { get; set; }
    public List<string> Log { get; } = [];
    public void QueueError(string error) => _errors.Add(error);
    public SimulatedBattery Battery => _battery;

    public FakeChannel(SimulatedBattery battery, SimulatedClock? clock, TimeSpan? step, double scale, ChannelLimits limits)
    {
        _battery = battery;
        _clock = clock;
        _step = step;
        _scale = scale;
        _limits = limits;
    }

    private (double V, double I) Solve()
    {
        if (!_output || !_battery.Connected) return (_output ? _volts : 0, 0);
        var voc = _battery.OpenCircuitVolts;
        var r = Math.Max(_battery.ResistanceOhm, 1e-6);
        var i = Math.Clamp((_volts - voc) / r, 0, _amps);
        return (voc + i * r, i);
    }

    private void Advance()
    {
        TimeSpan dt;
        if (_step is { } s)
        {
            dt = s;
            _clock?.Advance(dt);
        }
        else
        {
            var now = Stopwatch.GetTimestamp();
            dt = TimeSpan.FromSeconds(Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds * _scale);
            _lastTimestamp = now;
        }
        if (!_output) return;
        // Sub-step so large steps still integrate the CC->CV taper smoothly.
        const double sub = 1.0;
        var left = dt.TotalSeconds;
        while (left > 0)
        {
            var h = Math.Min(sub, left);
            var (_, i) = Solve();
            _battery.ChargeAh += i * h / 3600.0;
            left -= h;
        }
        // Supply OVP trip.
        if (_ovpOn && Solve().V > _ovp) { _output = false; Log.Add("OVP TRIP"); }
    }

    public Task<ChannelLimits> GetLimitsAsync(double requiredVolts, CancellationToken ct) => Task.FromResult(_limits);

    public Task<ChannelSnapshot> ReadStateAsync(CancellationToken ct) =>
        Task.FromResult(new ChannelSnapshot(_output, _volts, _amps, _ovp, _ovpOn, _ocpOn));

    public Task ApplySafeStateAsync(CancellationToken ct)
    {
        _output = false; _volts = 0; _amps = 0.1;
        return Task.CompletedTask;
    }

    public Task ConfigureAsync(ChargeSetpoint sp, CancellationToken ct)
    {
        Log.Add($"CONFIG {sp.Volts:0.###} V {sp.AmpsLimit:0.###} A OVP {sp.OvpVolts:0.###}");
        _output = false;
        _volts = Math.Min(sp.Volts, _limits.MaxVolts);
        _amps = Math.Min(sp.AmpsLimit, _limits.MaxAmps);
        _ovp = sp.OvpVolts; _ovpOn = true; _ocpOn = false;
        return Task.CompletedTask;
    }

    public Task SetCurrentLimitAsync(double amps, CancellationToken ct) { Log.Add($"CURR {amps:0.###}"); _amps = amps; return Task.CompletedTask; }
    public Task SetVoltageAsync(double volts, CancellationToken ct) { Log.Add($"VOLT {volts:0.###}"); _volts = volts; return Task.CompletedTask; }

    public Task SetOutputAsync(bool on, CancellationToken ct)
    {
        Log.Add(on ? "OUTPUT ON" : "OUTPUT OFF");
        if (!on && SetOutputOffFailures > 0)
        {
            SetOutputOffFailures--;
            throw new InstrumentCommunicationLost("simulated I/O failure");
        }
        _output = on;
        return Task.CompletedTask;
    }

    public Task<Measurement> MeasureAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _measureCount++;
        if (CommunicationLostAfterMeasures is { } n && _measureCount > n)
            throw new InstrumentCommunicationLost("simulated cable pull");
        Advance();
        var (v, i) = _output ? Solve() : (_battery.Connected ? _battery.OpenCircuitVolts : 0, 0);
        var m = new Measurement(_clock?.Now ?? DateTimeOffset.Now, v, i);
        OnMeasure?.Invoke(this, _measureCount);
        return Task.FromResult(m);
    }

    public Task<bool> GetOutputAsync(CancellationToken ct) => Task.FromResult(_output);

    public Task<IReadOnlyList<string>> DrainErrorsAsync(CancellationToken ct)
    {
        var copy = _errors.ToArray();
        _errors.Clear();
        return Task.FromResult<IReadOnlyList<string>>(copy);
    }

    public Task RestoreAsync(ChannelSnapshot snapshot, CancellationToken ct)
    {
        Log.Add("RESTORE");
        _output = false;
        _volts = snapshot.Volts; _amps = snapshot.AmpsLimit; _ovp = snapshot.OvpVolts;
        _ovpOn = snapshot.OvpEnabled; _ocpOn = snapshot.OcpEnabled;
        return Task.CompletedTask;
    }
}
