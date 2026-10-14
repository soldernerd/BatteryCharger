using BatteryCharger.Instruments.Transport;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BatteryCharger.App;

/// <summary>
/// Entry point. With <c>--visa-probe</c> the process only checks whether VISA can be initialised and exits, so the
/// main app can survive a VISA installation whose initialisation never returns (it simply kills the probe).
/// </summary>
public static class Program
{
    public const string ProbeArgument = "--visa-probe";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == ProbeArgument) return RunProbe();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static int RunProbe()
    {
        try
        {
            using var rm = new NativeVisaResourceManager();
            if (rm.IsAvailable(out var problem)) return 0;
            Console.Out.Write(problem);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Out.Write("VISA probe failed: " + ex.Message);
            return 2;
        }
    }
}
