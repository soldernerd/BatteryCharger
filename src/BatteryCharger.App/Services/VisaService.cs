using System.Diagnostics;
using BatteryCharger.Instruments.Transport;
using Microsoft.Extensions.Logging;

namespace BatteryCharger.App.Services;

/// <summary>
/// Owns the native VISA resource manager, its availability state and the SCPI traffic trace.
/// Loading VISA can block for a long time (or forever with a broken installation), so initialisation runs off the UI
/// thread with a timeout; until it has finished no VISA call may be made.
/// </summary>
public sealed class VisaService
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(10);

    private readonly SettingsService _settings;
    private readonly ILogger<VisaService> _log;
    private Task? _init;

    public VisaService(SettingsService settings, ILogger<VisaService> log)
    {
        _settings = settings;
        _log = log;
        Manager = new NativeVisaResourceManager();
    }

    public NativeVisaResourceManager Manager { get; }
    public bool IsAvailable { get; private set; }
    public string? Problem { get; private set; } = "VISA has not been checked yet.";

    /// <summary>Checks (once) whether VISA can be used. Never blocks the caller for longer than the timeout.</summary>
    public Task InitializeAsync() => _init ??= InitCoreAsync();

    private async Task InitCoreAsync()
    {
        // 1) Probe in a child process: a broken VISA installation can hang forever, even deep inside native code that
        //    would take this process (and its UI thread) down with it. A hung probe is simply killed.
        var probe = await RunProbeAsync();
        if (probe.Ok)
        {
            // 2) VISA answered in the child, so it is safe to initialise it here (still off the UI thread).
            var work = await Task.Run(() =>
            {
                var ok = Manager.IsAvailable(out var problem);
                return (ok, problem);
            });
            (IsAvailable, Problem) = work;
        }
        else
        {
            IsAvailable = false;
            Problem = probe.Message;
        }
        if (!IsAvailable) _log.LogWarning("VISA unavailable: {Problem}", Problem);
        else _log.LogInformation("VISA is available");
    }

    private static async Task<(bool Ok, string Message)> RunProbeAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, Program.ProbeArgument)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("The VISA probe could not be started.");
            var output = proc.StandardOutput.ReadToEndAsync();
            var exited = proc.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(InitTimeout)) != exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return (false, "The installed VISA library did not respond within 10 seconds. A stale or broken VISA installation may be present (visa64.dll in the Windows system folder). Reinstall the Keysight IO Libraries Suite.");
            }
            var text = (await output).Trim();
            return proc.ExitCode == 0 ? (true, "") : (false, text.Length > 0 ? text : "VISA could not be initialised.");
        }
        catch (Exception ex)
        {
            return (false, "VISA could not be checked: " + ex.Message);
        }
    }
    /// <summary>Raised (on a background thread) for every SCPI line: "TX ..." / "RX ..." / "ERR ...".</summary>
    public event Action<string>? Traffic;

    public void OnTraffic(string line)
    {
        if (_settings.Current.LogScpiTraffic) _log.LogDebug("SCPI {Line}", line);
        Traffic?.Invoke(line);
    }
}
