using BatteryCharger.Core.Profiles;

namespace BatteryCharger.Core.Session;

/// <summary>Kind of verdict the monitor returns.</summary>
public enum SafetyAction { Ok, Fault, TerminateWithWarning }

public readonly record struct SafetyVerdict(SafetyAction Action, string? Message = null)
{
    public static readonly SafetyVerdict Ok = new(SafetyAction.Ok);
}

/// <summary>Per-sample safety checks (spec 7.4). Faults abort the session and turn the output off.</summary>
public sealed class SafetyMonitor(ChargeProfile profile)
{
    private int _ov, _oc, _disc, _collapse, _abrupt;
    private double _refAmps;

    /// <summary>Resets counters when a stage is entered (setpoints change, grace period restarts).</summary>
    public void OnStageEntered() { _ov = _oc = _disc = _collapse = 0; }

    public SafetyVerdict Check(Sample s, IChargeStage stage, TimeSpan stageElapsed)
    {
        var ovLimit = profile.TargetPackVolts + 0.5 * profile.OvpMarginPackVolts;
        _ov = s.Volts > ovLimit ? _ov + 1 : 0;
        if (_ov >= 3) return Fault($"Over-voltage: {s.Volts:0.000} V exceeds {ovLimit:0.000} V");

        var ocLimit = 1.10 * stage.Amps + 0.020;
        _oc = s.Amps > ocLimit ? _oc + 1 : 0;
        if (_oc >= 3) return Fault($"Over-current: {s.Amps:0.000} A exceeds {ocLimit:0.000} A");

        // Output collapsed (e.g. OVP trip) without OUTP? noticing it yet.
        _collapse = s.Volts < 0.1 && Math.Abs(s.Amps) < 0.001 ? _collapse + 1 : 0;
        if (_collapse >= 3) return Fault("Output tripped: voltage collapsed with no current (supply protection?)");

        // A real battery never drops the charge current by 10x between two samples; a lead pulled off the terminals does.
        // Without this, a disconnect looks exactly like "CV finished" (supply sits at its voltage setpoint with I = 0).
        _abrupt = _refAmps >= 0.3 * stage.Amps && s.Amps < 0.1 * _refAmps ? _abrupt + 1 : 0;
        if (_abrupt == 0) _refAmps = s.Amps;
        if (_abrupt >= 2) return Fault("Charge current collapsed abruptly: battery disconnected?");

        if (stage.IsConstantCurrentStage && stageElapsed > TimeSpan.FromSeconds(5))
        {
            _disc = s.Amps < 0.5 * stage.Amps && s.Volts < 0.97 * stage.Volts ? _disc + 1 : 0;
            if (_disc >= 5) return Fault("Battery disconnected or current not reaching the battery");
        }
        else _disc = 0;

        if (!stage.IsFloat && s.Ah > profile.MaxChargeAh)
            return new(SafetyAction.TerminateWithWarning, $"Charge limit reached ({profile.MaxChargeAh:0.###} Ah)");
        return SafetyVerdict.Ok;
    }

    public static SafetyVerdict CheckOutputState(bool outputOn) =>
        outputOn ? SafetyVerdict.Ok : Fault("Output tripped: the supply output switched off unexpectedly (OVP or protection)");

    private static SafetyVerdict Fault(string m) => new(SafetyAction.Fault, m);
}
