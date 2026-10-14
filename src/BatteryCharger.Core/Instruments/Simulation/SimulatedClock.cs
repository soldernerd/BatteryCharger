using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Instruments.Simulation;

/// <summary>Manually advanced clock for deterministic tests of the session engine.</summary>
public sealed class SimulatedClock : ISessionClock
{
    private TimeSpan _elapsed;
    private readonly DateTimeOffset _origin = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public void Start() => _elapsed = TimeSpan.Zero;
    public void Advance(TimeSpan dt) => _elapsed += dt;
    public TimeSpan Elapsed => _elapsed;
    public DateTimeOffset Now => _origin + _elapsed;
}
