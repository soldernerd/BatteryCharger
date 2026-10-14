using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using BatteryCharger.App.Services;
using BatteryCharger.App.ViewModels;
using BatteryCharger.App.Views;
using BatteryCharger.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private readonly SupplyRegistry _registry;
    private readonly Dictionary<SupplyViewModel, ChargerPage> _pages = [];
    private bool _closeConfirmed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public MainWindow()
    {
        var ctx = App.Services.GetRequiredService<WindowContext>();
        _visa = App.Services.GetRequiredService<VisaService>();
        _settings = App.Services.GetRequiredService<SettingsService>();
        _registry = App.Services.GetRequiredService<SupplyRegistry>();

        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        ctx.Window = this;
        ctx.Hwnd = _hwnd;

        Title = $"BatteryCharger {AppVersion.Display}";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "BatteryCharger.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
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
        _registry.Supplies.CollectionChanged += OnSuppliesChanged;
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
            Host.Content = new VisaMissingPage();
            return;
        }
        ChargerItem.IsEnabled = true;
        ConsoleItem.IsEnabled = true;
        Nav.SelectedItem = ChargerItem;
        // Look for the power supplies right away; every supported one appears as a tab below "Charger".
        _registry.RefreshCommand.Execute(null);
    }

    /// <summary>Keeps the child items of "Charger" in step with the supplies the registry knows.</summary>
    private void OnSuppliesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var selected = Nav.SelectedItem as NavigationViewItem;
        var selectedSupply = selected?.Tag as SupplyViewModel;
        ChargerItem.MenuItems.Clear();
        foreach (var supply in _registry.Supplies)
        {
            var item = new NavigationViewItem { Content = supply.NavTitle, Tag = supply, Icon = new FontIcon { Glyph = "\uE945" } };
            ToolTipService.SetToolTip(item, supply.IdentityText);
            AutomationProperties.SetName(item, supply.NavTitle);
            supply.PropertyChanged -= OnSupplyPropertyChanged;
            supply.PropertyChanged += OnSupplyPropertyChanged;
            ChargerItem.MenuItems.Add(item);
            if (supply == selectedSupply) Nav.SelectedItem = item;
        }
        if (selectedSupply is not null && !_registry.Supplies.Contains(selectedSupply)) Nav.SelectedItem = ChargerItem;
        // Children added while the parent is expanded are only realized after it is expanded again.
        ChargerItem.IsExpanded = false;
        ChargerItem.IsExpanded = true;
    }

    private void OnSupplyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SupplyViewModel.NavTitle) || sender is not SupplyViewModel supply) return;
        foreach (var item in ChargerItem.MenuItems.OfType<NavigationViewItem>().Where(i => i.Tag == supply))
        {
            item.Content = supply.NavTitle;
            AutomationProperties.SetName(item, supply.NavTitle);
        }
    }


    private int Scale(int dips) => (int)Math.Round(dips * GetDpiForWindow(_hwnd) / 96.0);

    private void UpdateConsoleVisibility() =>
        ConsoleItem.Visibility = _settings.Current.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { Host.Content = new SettingsPage(); return; }
        var tag = args.SelectedItemContainer?.Tag;
        _registry.Selected = tag as SupplyViewModel;
        switch (tag)
        {
            case SupplyViewModel supply:
                if (!_pages.TryGetValue(supply, out var page)) _pages[supply] = page = new ChargerPage(supply);
                Host.Content = page;
                break;
            case "console":
                Host.Content = new ScpiConsolePage();
                break;
            case "charger":
                Host.Content = new ChargerOverviewPage();
                break;
        }
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
        if (await _registry.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
