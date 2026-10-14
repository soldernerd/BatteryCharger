using System.Diagnostics;

namespace BatteryCharger.Core.Session;

/// <summary>Time source for a session (elapsed time is monotonic, not wall-clock). Replaceable in tests.</summary>
public interface ISessionClock
{
    /// <summary>Starts (or restarts) the elapsed counter.</summary>
    void Start();
    TimeSpan Elapsed { get; }
    DateTimeOffset Now { get; }
}

/// <summary>Real-time clock based on <see cref="Stopwatch"/>.</summary>
public sealed class StopwatchClock : ISessionClock
{
    private readonly Stopwatch _sw = new();
    public void Start() => _sw.Restart();
    public TimeSpan Elapsed => _sw.Elapsed;
    public DateTimeOffset Now => DateTimeOffset.Now;
}
