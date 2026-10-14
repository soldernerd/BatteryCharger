using System.Runtime.InteropServices;

namespace BatteryCharger.App.Services;

/// <summary>
/// Keeps Windows awake while a charge session is active. SetThreadExecutionState applies to the calling thread, so
/// <see cref="Acquire"/> and <see cref="Release"/> must be called from the same long-lived thread (the UI thread).
/// </summary>
public sealed class SleepInhibitor
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    public void Acquire() => SetThreadExecutionState(EsContinuous | EsSystemRequired);
    public void Release() => SetThreadExecutionState(EsContinuous);
}
