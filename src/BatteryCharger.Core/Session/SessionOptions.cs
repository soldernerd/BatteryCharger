namespace BatteryCharger.Core.Session;

/// <summary>Identification of the instrument/channel, written to the log header.</summary>
public sealed record InstrumentInfo(string Idn, string Resource, string Channel);

/// <summary>Runtime options of a session.</summary>
public sealed class SessionOptions
{
    /// <summary>Measurement poll interval (0.5 .. 10 s).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Restore the supply setpoints captured before the session (output stays off).</summary>
    public bool RestorePreviousSetpoints { get; init; } = true;
    /// <summary>Must be true: the user confirmed polarity, chemistry, cell count and supervision (spec 6.4 step 7).</summary>
    public bool UserConfirmed { get; init; }
    public InstrumentInfo Instrument { get; init; } = new("", "", "");
    public ISessionClock Clock { get; init; } = new StopwatchClock();
    /// <summary>Log interval while floating.</summary>
    public TimeSpan FloatLogInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Check output state and the error queue every N ticks.</summary>
    public int HealthCheckEveryTicks { get; init; } = 5;
}
