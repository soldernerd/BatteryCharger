using BatteryCharger.App.Services;
using BatteryCharger.App.ViewModels;
using BatteryCharger.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;

namespace BatteryCharger.App;

public partial class App : Application
{
    /// <summary>Application-wide service provider (built from the generic host; the host itself is never run).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    private Window? _window;

    public App()
    {
        Services = BuildServices();
        InitializeComponent();
    }

    private static IServiceProvider BuildServices()
    {
        var diagDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryCharger", "diagnostics");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(diagDir, "batterycharger-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);

        var s = builder.Services;
        s.AddSingleton(new SettingsStore(SettingsStore.DefaultPath));
        s.AddSingleton<SettingsService>();
        s.AddSingleton<WindowContext>();
        s.AddSingleton<VisaService>();
        s.AddSingleton<InstrumentService>();
        s.AddSingleton<DialogService>();
        s.AddSingleton<ThemeService>();
        s.AddSingleton<SleepInhibitor>();
        s.AddSingleton<ChargerViewModel>();
        s.AddSingleton<ScpiConsoleViewModel>();
        s.AddTransient<SettingsViewModel>();
        return builder.Build().Services;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var log = Services.GetRequiredService<ILogger<App>>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical("Unhandled exception: {Ex}", e.ExceptionObject);
        UnhandledException += (_, e) =>
        {
            log.LogError(e.Exception, "Unhandled UI exception: {Message}", e.Message);
            e.Handled = true;
        };

        _window = new MainWindow();
        _window.Activate();
    }
}
