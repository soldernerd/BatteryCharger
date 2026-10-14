using BatteryCharger.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace BatteryCharger.App.Views;

/// <summary>The "Charger" entry of the navigation pane: found instruments, refresh and add-LAN.</summary>
public sealed partial class ChargerOverviewPage : Page
{
    public SupplyRegistry Registry { get; } = App.Services.GetRequiredService<SupplyRegistry>();

    public ChargerOverviewPage() => InitializeComponent();
}
