using System.Runtime.InteropServices;
using BatteryCharger.App.Services;
using BatteryCharger.App.ViewModels;
using BatteryCharger.App.Views;
using BatteryCharger.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace BatteryCharger.App;

public sealed partial class MainWindow : Window
{
    private const int MinWidth = 1000, MinHeight = 700;
    private readonly nint _hwnd;
    private readonly VisaService _visa;
    private readonly SettingsService _settings;
    private bool _closeConfirmed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public MainWindow()
    {
        var ctx = App.Services.GetRequiredService<WindowContext>();
        _visa = App.Services.GetRequiredService<VisaService>();
        _settings = App.Services.GetRequiredService<SettingsService>();

        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        ctx.Window = this;
        ctx.Hwnd = _hwnd;

        Title = $"BatteryCharger {AppVersion.Display}";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(Scale(1240), work.Width), Math.Min(Scale(860), work.Height)));
        AppWindow.Move(new PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (work.Height - AppWindow.Size.Height) / 2));
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;

        Nav.Loaded += (_, _) =>
        {
            ctx.XamlRoot = Nav.XamlRoot;
            App.Services.GetRequiredService<ThemeService>().Apply();
        };
        SettingsViewModel.DeveloperModeChanged += (_, _) => UpdateConsoleVisibility();
        UpdateConsoleVisibility();

        // VISA initialisation can block for a long time, so it runs in the background while the shell is already visible.
        ChargerItem.IsEnabled = false;
        ConsoleItem.IsEnabled = false;
        Nav.Loaded += async (_, _) => await InitializeVisaAsync();
    }

    private async Task InitializeVisaAsync()
    {
        Busy.IsActive = true;
        await _visa.InitializeAsync();
        Busy.IsActive = false;
        BusyPanel.Visibility = Visibility.Collapsed;
        if (!_visa.IsAvailable && !_settings.Current.DeveloperMode)
        {
            // Everything that needs an instrument stays disabled; Settings remains reachable.
            ContentFrame.Navigate(typeof(VisaMissingPage));
            return;
        }
        ChargerItem.IsEnabled = true;
        ConsoleItem.IsEnabled = true;
        Nav.SelectedItem = ChargerItem;
    }


    private int Scale(int dips) => (int)Math.Round(dips * GetDpiForWindow(_hwnd) / 96.0);

    private void UpdateConsoleVisibility() =>
        ConsoleItem.Visibility = _settings.Current.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { ContentFrame.Navigate(typeof(SettingsPage)); return; }
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        ContentFrame.Navigate(tag switch
        {
            "console" => typeof(ScpiConsolePage),
            _ => typeof(ChargerPage),
        });
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        var size = sender.Size;
        var minW = Scale(MinWidth);
        var minH = Scale(MinHeight);
        if (size.Width < minW || size.Height < minH)
            sender.Resize(new SizeInt32(Math.Max(size.Width, minW), Math.Max(size.Height, minH)));
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;
        args.Cancel = true;   // never close silently while the output could be on
        var charger = App.Services.GetRequiredService<ChargerViewModel>();
        if (await charger.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            await App.Services.GetRequiredService<InstrumentService>().DisconnectAsync();
            Close();
        }
    }
}
