using System.Diagnostics;
using BatteryCharger.Core.Session;

namespace BatteryCharger.Core.Instruments.Simulation;

/// <summary>Real-time clock running <c>scale</c> times faster; used to demo a whole charge in minutes with the simulated supply.</summary>
public sealed class ScaledClock(double scale) : ISessionClock
{
    private readonly Stopwatch _sw = new();
    public void Start() => _sw.Restart();
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_sw.Elapsed.TotalSeconds * scale);
    public DateTimeOffset Now => DateTimeOffset.Now;
}
