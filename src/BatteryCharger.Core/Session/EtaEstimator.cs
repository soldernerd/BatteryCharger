using BatteryCharger.Core.Batteries;
using BatteryCharger.Core.Profiles;

namespace BatteryCharger.Core.Session;

/// <summary>
/// Rough estimate of the time until the charge is complete, refreshed on every sample. The starting state of charge is
/// unknown, so it assumes an empty battery: the real end usually comes earlier ("up to"). Returns null while it cannot say.
/// </summary>
public sealed class EtaEstimator(ChargeProfile profile)
{
    /// <summary>Longest estimate that is reported; anything above is shown as "more than ...".</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromHours(99);

    private const double CcFraction = 0.8;       // share of the capacity delivered before a CC/CV charger reaches the voltage limit
    private const double CvWindowSeconds = 60;   // window used to measure the current decay in CV

    private readonly List<(double T, double I)> _cv = [];

    private double Capacity => profile.Battery.CapacityAh;

    /// <summary>Charge (in capacity multiples) a full charge from empty takes, including the losses of the chemistry.</summary>
    private double FullFactor => profile.Battery.Technology switch
    {
        BatteryTechnology.NiMH or BatteryTechnology.NiCd => 1.25,
        BatteryTechnology.LeadAcid => 1.1,
        _ => 1.0,
    };

    public TimeSpan? Update(Sample s)
    {
        if (s.State is ChargeState.Float or ChargeState.Completed) return TimeSpan.Zero;
        if (s.State is ChargeState.Stopped or ChargeState.Faulted) return null;
        if (s.State != ChargeState.ConstantVoltage) _cv.Clear();
        var iMain = profile.MainCurrentAmps;
        if (!(Capacity > 0) || !(iMain > 0)) return null;

        double hours;
        switch (s.State)
        {
            case ChargeState.Precharge:
                return null;                                    // short and hard to predict
            case ChargeState.ConstantVoltage:
                hours = CvHours(s);
                break;
            default:                                            // CC (also the single nickel stage)
                if (s.Amps < 0.05 * iMain) return null;
                hours = profile.Battery.Technology is BatteryTechnology.NiMH or BatteryTechnology.NiCd
                    ? Math.Max(0, FullFactor * Capacity - s.Ah) / s.Amps
                    : Math.Max(0, CcFraction * Capacity - s.Ah) / s.Amps + TauHours() * Math.Log(iMain / Math.Max(profile.TerminationAmps, 1e-6));
                break;
        }
        if (double.IsNaN(hours) || double.IsInfinity(hours)) return null;
        var t = TimeSpan.FromHours(Math.Max(0, hours));
        return t > Cap ? Cap : t;
    }

    /// <summary>Time constant of the CV current decay assumed from the capacity (the remaining 20 % is delivered while decaying).</summary>
    private double TauHours()
    {
        var iMain = profile.MainCurrentAmps;
        var iTerm = Math.Max(profile.TerminationAmps, 1e-6);
        var spread = Math.Max(iMain - iTerm, 0.05 * iMain);
        return (1 - CcFraction) * Capacity / spread;
    }

    private double CvHours(Sample s)
    {
        var iTerm = Math.Max(profile.TerminationAmps, 1e-6);
        if (s.Amps <= iTerm) return 0;
        var t = s.Elapsed.TotalSeconds;
        _cv.Add((t, s.Amps));
        _cv.RemoveAll(p => p.T < t - CvWindowSeconds);

        var tau = TauHours();
        if (_cv.Count >= 3 && _cv[^1].T - _cv[0].T >= 20 && _cv[0].I > s.Amps)
        {
            // Observed exponential decay: I2 = I1 * exp(-dt / tau)
            var observed = (_cv[^1].T - _cv[0].T) / 3600.0 / Math.Log(_cv[0].I / s.Amps);
            if (observed > 0 && !double.IsInfinity(observed)) tau = observed;
        }
        return tau * Math.Log(s.Amps / iTerm);
    }

    /// <summary>"1:25 h", "12 min" or "less than a minute".</summary>
    public static string Format(TimeSpan t)
    {
        if (t >= Cap) return "more than 99 h";
        if (t < TimeSpan.FromMinutes(1)) return "less than a minute";
        if (t < TimeSpan.FromHours(1)) return $"{(int)Math.Round(t.TotalMinutes)} min";
        return $"{(int)t.TotalHours}:{t.Minutes:00} h";
    }
}
