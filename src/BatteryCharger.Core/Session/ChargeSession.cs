using System.Diagnostics;
using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Instruments;
using BatteryCharger.Core.Profiles;

namespace BatteryCharger.Core.Session;

/// <summary>
/// Runs one charge: preflight, staged charging with software supervision, safe shutdown, snapshot restore and logging.
/// A session instance is single use; it holds no static state so several sessions may run side by side later.
/// </summary>
public sealed class ChargeSession : IAsyncDisposable
{
    private readonly IPowerSupplyChannel _channel;
    private readonly BatteryDescriptor _battery;
    private readonly ChargeProfile _profile;
    private readonly ISessionLogger _logger;
    private readonly SessionOptions _options;
    private readonly SafetyMonitor _monitor;
    private readonly CancellationTokenSource _stopCts = new();

    private int _started;
    private volatile ChargeState _state = ChargeState.Idle;
    private volatile string _stageName = "";
    private int _stageIndex;
    private ChannelSnapshot? _snapshot;
    private bool _outputTouched;
    private bool _logOpen;
    private Sample? _lastSample;
    private string? _logPath;

    public ChargeSession(IPowerSupplyChannel channel, BatteryDescriptor battery, ChargeProfile profile,
                         ISessionLogger logger, SessionOptions options)
    {
        _channel = channel;
        _battery = battery;
        _profile = profile;
        _logger = logger;
        _options = options;
        _monitor = new SafetyMonitor(profile);
    }

    public ChargeState State => _state;
    public string StageName => _stageName;
    public BatteryDescriptor Battery => _battery;
    public ChargeProfile Profile => _profile;

    /// <summary>Raised on every sample and notable state change, on the session's background thread.</summary>
    public event EventHandler<SessionUpdate>? Updated;

    /// <summary>Requests a user stop; the output is switched off within one tick.</summary>
    public Task RequestStopAsync()
    {
        try { _stopCts.Cancel(); } catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    /// <summary>Runs the session to a terminal state. Never throws for instrument or logging failures; they end as a fault.</summary>
    public async Task<SessionResult> RunAsync(CancellationToken ct)
    {
        if (!_options.UserConfirmed)
            throw new InvalidOperationException("The user has not confirmed the safety checklist.");
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("A session can only be run once.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
        var clock = _options.Clock;
        clock.Start();
        EndReason reason;
        string message;

        try
        {
            (reason, message) = await RunCoreAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            reason = EndReason.UserStopped;
            message = "Stopped by user";
        }
        catch (InstrumentCommunicationLost ex)
        {
            reason = EndReason.Fault;
            message = $"Communication with the power supply lost: {ex.Message}. The output state is unknown; check the power supply manually.";
        }
        catch (Exception ex)
        {
            reason = EndReason.Fault;
            message = ex.Message;
        }

        // Shutdown, whatever the reason.
        var outputOff = true;
        if (_outputTouched)
        {
            outputOff = await TurnOutputOffAsync();
            if (!outputOff)
            {
                if (reason != EndReason.Fault) reason = EndReason.Fault;
                message += " WARNING: the output could not be confirmed off. Check the power supply manually.";
            }
        }
        if (outputOff && _snapshot is not null && _options.RestorePreviousSetpoints && _outputTouched)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _channel.RestoreAsync(_snapshot, cts.Token);
            }
            catch (Exception ex)
            {
                message += $" (Restoring previous supply setpoints failed: {ex.Message})";
            }
        }

        var last = _lastSample;
        var result = new SessionResult(reason, message, clock.Elapsed, last?.Ah ?? 0, last?.Wh ?? 0,
            last?.Volts ?? 0, _logger.FilePath ?? _logPath);

        if (_logOpen)
        {
            try { await _logger.CloseAsync(result, CancellationToken.None); }
            catch (Exception ex) { result = result with { Message = result.Message + $" (Writing the log footer failed: {ex.Message})" }; }
        }

        var finalState = reason switch
        {
            EndReason.UserStopped => ChargeState.Stopped,
            EndReason.Fault => ChargeState.Faulted,
            _ => ChargeState.Completed,
        };
        _state = finalState;
        Raise(new SessionUpdate(finalState, _stageName, _stageIndex, null, result.Message, reason is EndReason.Fault or EndReason.CompletedWithWarning));
        return result;
    }

    private async Task<(EndReason, string)> RunCoreAsync(CancellationToken ct)
    {
        var clock = _options.Clock;
        SetState(ChargeState.Preflight, "Preflight");

        if (!_profile.IsValid)
            return (EndReason.Fault, "Invalid charge profile: " + string.Join(" ", _profile.Errors));

        // Charging without logging is not allowed: failing to open the log aborts before the instrument is touched.
        await _logger.OpenAsync(new SessionHeader(clock.Now, _options.Instrument, _profile, _options.PollInterval), ct);
        _logOpen = true;
        _logPath = _logger.FilePath;

        _outputTouched = true;
        var pre = await PreflightChecker.RunAsync(_channel, _profile, ct, _options.AllowLowVoltageStart);
        _snapshot = pre.Snapshot;
        if (pre.Snapshot is { OutputOn: true })
            await LogEventAsync("Output was on before the session; it was switched off", ct);
        foreach (var w in pre.Warnings)
        {
            await LogEventAsync("Warning: " + w, ct);
            Raise(new SessionUpdate(_state, _stageName, _stageIndex, null, w, true));
        }
        if (pre.Refused)
            return (EndReason.Fault, "Preflight refused: " + pre.Refusal + (pre.Hints.Count > 0 ? " " + string.Join(" ", pre.Hints) : ""));
        await LogEventAsync($"Preflight OK: battery {pre.BatteryVolts:0.000} V", ct);

        _stageIndex = pre.InitialStageIndex;
        var stage = _profile.Stages[_stageIndex];
        await _channel.ConfigureAsync(new ChargeSetpoint(stage.Volts, stage.Amps, _profile.OvpVolts), ct);
        var errors = await _channel.DrainErrorsAsync(ct);
        if (errors.Count > 0)
            return (EndReason.Fault, "Instrument error while configuring: " + string.Join("; ", errors));

        stage.Reset();
        _monitor.OnStageEntered();
        SetState(stage.State, stage.Name);
        await _channel.SetOutputAsync(true, ct);

        var interval = _options.PollInterval > TimeSpan.Zero ? _options.PollInterval : TimeSpan.FromSeconds(1);
        using var timer = new PeriodicTimer(interval);
        var stageStart = clock.Elapsed;
        var last = clock.Elapsed;
        double prevI = 0, prevP = 0, ah = 0, wh = 0;
        long tick = 0;
        TimeSpan lastLogged = TimeSpan.MinValue;
        string? pending = $"Stage: {stage.Name} ({stage.Volts:0.000} V, {stage.Amps:0.###} A)";
        var health = Math.Max(1, _options.HealthCheckEveryTicks);

        while (await timer.WaitForNextTickAsync(ct))
        {
            var t0 = Stopwatch.GetTimestamp();
            tick++;
            var m = await _channel.MeasureAsync(ct);
            var el = clock.Elapsed;
            var dt = Math.Max(0, (el - last).TotalSeconds);
            last = el;
            var p = m.Volts * m.Amps;
            ah += 0.5 * (prevI + m.Amps) * dt / 3600.0;
            wh += 0.5 * (prevP + p) * dt / 3600.0;
            prevI = m.Amps;
            prevP = p;

            var mode = m.Amps >= 0.98 * stage.Amps ? PsuMode.CC : PsuMode.CV;
            var sample = new Sample(clock.Now, el, stage.State, stage.Name, m.Volts, m.Amps, ah, wh,
                stage.Volts, stage.Amps, mode, pending);
            pending = null;

            EndReason? end = null;
            string? note = null;

            var verdict = _monitor.Check(sample, stage, el - stageStart);
            if (verdict.Action == SafetyAction.Ok && tick % health == 0)
            {
                verdict = SafetyMonitor.CheckOutputState(await _channel.GetOutputAsync(ct));
                if (verdict.Action == SafetyAction.Ok)
                {
                    var errs = await _channel.DrainErrorsAsync(ct);
                    if (errs.Count > 0)
                        verdict = new SafetyVerdict(SafetyAction.Fault, "Instrument error: " + string.Join("; ", errs));
                }
            }

            if (verdict.Action == SafetyAction.Fault) { end = EndReason.Fault; note = verdict.Message; }
            else if (verdict.Action == SafetyAction.TerminateWithWarning) { end = EndReason.CompletedWithWarning; note = verdict.Message; }
            else
            {
                var d = stage.Evaluate(sample, el - stageStart);
                switch (d.Action)
                {
                    case StageAction.Advance when _stageIndex + 1 < _profile.Stages.Count:
                        var next = _profile.Stages[_stageIndex + 1];
                        await ApplyStageSetpointsAsync(stage, next, ct);
                        _stageIndex++;
                        stage = next;
                        stage.Reset();
                        _monitor.OnStageEntered();
                        stageStart = el;
                        note = $"Stage: {stage.Name} ({stage.Volts:0.000} V, {stage.Amps:0.###} A)" + (d.Message is null ? "" : $" [{d.Message}]");
                        SetState(stage.State, stage.Name);
                        break;
                    case StageAction.Advance or StageAction.Complete:
                        end = EndReason.Completed; note = d.Message ?? "Charge complete"; break;
                    case StageAction.CompleteWithWarning:
                        end = EndReason.CompletedWithWarning; note = d.Message; break;
                    case StageAction.Fault:
                        end = EndReason.Fault; note = d.Message; break;
                }
            }

            if (note is not null)
                sample = sample with { Event = sample.Event is null ? note : sample.Event + "; " + note };
            _lastSample = sample;

            if (!(stage.IsFloat && sample.Event is null && el - lastLogged < _options.FloatLogInterval))
            {
                await _logger.WriteSampleAsync(sample, ct);
                lastLogged = el;
            }
            Raise(new SessionUpdate(_state, _stageName, _stageIndex, sample, note, end is EndReason.Fault or EndReason.CompletedWithWarning));

            if (end is { } e)
                return (e, note ?? (e == EndReason.Completed ? "Charge complete" : ""));

            var tickMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (tickMs > 3 * interval.TotalMilliseconds)
                pending = $"Slow tick: {tickMs:0} ms";
        }
        return (EndReason.UserStopped, "Stopped by user");
    }

    private async Task ApplyStageSetpointsAsync(IChargeStage from, IChargeStage to, CancellationToken ct)
    {
        if (Math.Abs(to.Volts - from.Volts) > 1e-9) await _channel.SetVoltageAsync(to.Volts, ct);
        if (Math.Abs(to.Amps - from.Amps) > 1e-9) await _channel.SetCurrentLimitAsync(to.Amps, ct);
        var errs = await _channel.DrainErrorsAsync(ct);
        if (errs.Count > 0) throw new InstrumentException("Instrument error at stage transition: " + string.Join("; ", errs));
    }

    private async Task<bool> TurnOutputOffAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _channel.SetOutputAsync(false, cts.Token);
                if (!await _channel.GetOutputAsync(cts.Token)) return true;
            }
            catch (Exception) { /* retry */ }
        }
        return false;
    }

    private async Task LogEventAsync(string text, CancellationToken ct)
    {
        await _logger.WriteEventAsync(_options.Clock.Now, _options.Clock.Elapsed, _state, _stageName, text, ct);
    }

    private void SetState(ChargeState state, string stage)
    {
        _state = state;
        _stageName = stage;
        Raise(new SessionUpdate(state, stage, _stageIndex, null, null, false));
    }

    private void Raise(SessionUpdate u)
    {
        try { Updated?.Invoke(this, u); }
        catch (Exception) { /* a faulty subscriber must never disturb charging */ }
    }

    public ValueTask DisposeAsync()
    {
        _stopCts.Dispose();
        return ValueTask.CompletedTask;
    }
}
